using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - read-only
    // confirmation step. Never writes any field.
    //
    // Purpose: full static disassembly of cmpDrawSkill.cmpDrawSkillList
    // (VA 0x182525040) this session found the highlight/selection
    // computation for a given row is gated on
    // "stock.skill[idx] != 0" (VA ~0x18252526C-2E) BEFORE it ever computes
    // SelFlag - if the array slot at that index reads as 0, the whole
    // selection check is skipped for that row and it never gets highlighted,
    // regardless of where the cursor actually is. For the synthetic/hidden
    // 9th row (index 8, one past the real 8 owned-skill slots), this is a
    // very plausible explanation for master-archive.md Section 22's
    // "entry exists, cursor reaches it, description/confirm work, but the
    // highlight frame never renders" finding - if index 8 of the SAME
    // skill array stays 0 during this bridge's own borrowed forget UI,
    // while native's own genuine "still has a native-learnable skill"
    // case (Section 22's "High Pixie-type" comparison) might populate it.
    //
    // This class only OBSERVES stock.skill[0..8] (index 8 = ONE PAST the
    // official 8-slot capacity, read-only, never written) alongside cursor/
    // SelectSkillID/EventParam/target, while FullCapacityAddNewBridgeState.Active
    // is true, to confirm or refute the "skill[8] stays 0" hypothesis before
    // any write is attempted.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class HiddenSlotArrayObserver
    {
        internal static readonly bool Enabled = true;

        private static string _lastSnapshot = "";

        private static void Postfix()
        {
            if (!Enabled) return;
            if (!FullCapacityAddNewBridgeState.Active) return;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                int cursor = -1;
                IntPtr action = Marshal.ReadIntPtr(gbwk.Pointer, 0x88);
                if (action != IntPtr.Zero)
                {
                    IntPtr actionData = Marshal.ReadIntPtr(action, 0x20);
                    if (actionData != IntPtr.Zero)
                    {
                        cursor = Marshal.ReadByte(actionData, 0x14);
                    }
                }

                int skillCnt = stock.skillcnt;
                var arr = stock.skill;
                if (arr == null) return;

                // Read index 8 explicitly (one past the official 8-slot
                // capacity) plus 0..7 for context - read-only, never written.
                int arrLen = arr.Length;
                var values = new int[9];
                for (int i = 0; i < 9; i++)
                {
                    values[i] = i < arrLen ? arr[i] : -999999; // sentinel: out of bounds
                }

                string snapshot =
                    $"skillcnt={skillCnt}; arrLen={arrLen}; cursor={cursor}; " +
                    $"selectSkillID={gbwk.SelectSkillID}; eventParam={gbwk.EventParam}; " +
                    $"target={FullCapacityAddNewBridgeState.Target}; " +
                    $"skill=[{string.Join(",", values)}]";

                if (snapshot == _lastSnapshot) return;
                _lastSnapshot = snapshot;

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HIDDEN-SLOT-ARRAY-CHECK; " +
                    $"frame={frame}; unit={FullCapacityAddNewBridgeState.WatchedUnit}; {snapshot}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HiddenSlotArrayObserver postfix failed safely: {ex.Message}");
            }
        }
    }
}
