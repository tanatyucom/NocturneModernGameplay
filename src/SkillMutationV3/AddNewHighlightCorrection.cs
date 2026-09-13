using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // PRESENTATION FIX (Queue-era archaeology, adapted for V3) - list entry
    // highlight/selection correction while the borrowed forget UI is open.
    //
    // Background: real-machine screenshot evidence (High Pixie, LV13) showed
    // that after a curriculum skill (宝探し) undergoes Power-Up/Mutation into
    // a different skill (メディア), the description text panel correctly
    // shows メディア's effect, but the skill LIST's highlighted/selected row
    // stays on 宝探し's original slot. Confirmed cosmetic-only (discarding
    // the stale-highlighted entry does not affect real data - status screen
    // still shows the original skill afterward).
    //
    // The legacy Queue-era implementation
    // (legacy/skillmutation/SkillMutationLearnAsNew.LegacyFinal.cs,
    // CorrectQueuedUiSelection(), line 1688) already solved exactly this,
    // for its own synthetic Learn-As-New flow: every frame while its
    // transaction was active, it read the native forget-UI's own cursor
    // position (GBWK+0x88 -> +0x20 -> +0x14, the SAME "action/actionData"
    // object chain this session's static analysis independently identified
    // as a UI/message-window object during the "native seq21 auxiliary
    // transition" investigation) and wrote GBWK.SelectSkillID to match:
    //   cursor == MaxLearnedSkills (8, the synthetic/hidden 9th slot)
    //     -> SelectSkillID = the target/mutated skill
    //   cursor in [0, MaxLearnedSkills) and a valid owned slot
    //     -> SelectSkillID = whatever skill genuinely occupies that slot
    // This class re-derives that same correction for V3's own
    // FullCapacityAddNewBridge transaction, without reviving any legacy
    // code - only the read offsets and the correction shape are reused as a
    // concept.
    //
    // Scope: active ONLY while FullCapacityAddNewBridgeState.Active is true
    // (i.e. only during THIS bridge's own borrowed forget UI window) - never
    // touches SelectSkillID outside that window, so ordinary native
    // Power-Up/Mutation/Overwrite flows are completely unaffected.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class AddNewHighlightCorrection
    {
        internal static readonly bool Enabled = true;

        private const int MaxLearnedSkills = 8;

        private static int _lastObservedCursor = int.MinValue;

        private static void Postfix()
        {
            if (!Enabled) return;
            if (!FullCapacityAddNewBridgeState.Active) return;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                IntPtr action = Marshal.ReadIntPtr(gbwk.Pointer, 0x88);
                if (action == IntPtr.Zero) return;
                IntPtr actionData = Marshal.ReadIntPtr(action, 0x20);
                if (actionData == IntPtr.Zero) return;
                int cursor = Marshal.ReadByte(actionData, 0x14);

                if (cursor != _lastObservedCursor)
                {
                    _lastObservedCursor = cursor;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-HIGHLIGHT-CURSOR; " +
                        $"unit={FullCapacityAddNewBridgeState.WatchedUnit}; cursor={cursor}; " +
                        $"selectedBefore={gbwk.SelectSkillID}; " +
                        $"target={SkillNameResolver.Format(FullCapacityAddNewBridgeState.Target)}.");
                }

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                if (cursor == MaxLearnedSkills)
                {
                    gbwk.SelectSkillID = (ushort)FullCapacityAddNewBridgeState.Target;
                }
                else if (cursor >= 0 && cursor < MaxLearnedSkills &&
                         cursor < stock.skillcnt && cursor < stock.skill.Length)
                {
                    gbwk.SelectSkillID = unchecked((ushort)stock.skill[cursor]);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] AddNewHighlightCorrection postfix failed safely: {ex.Message}");
            }
        }
    }
}
