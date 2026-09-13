using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // PRESENTATION FIX (Queue-era archaeology, adapted for V3) - part 2:
    // the actual LIST ROW LABEL TEXT source.
    //
    // AddNewHighlightCorrection.cs fixed GBWK.SelectSkillID (confirmed via
    // ADDNEW-HIGHLIGHT-CURSOR logs to correctly track the native forget-UI
    // cursor every frame) but real-machine testing showed the visible list
    // row label was STILL wrong afterward - proving SelectSkillID is not
    // what drives the row's displayed name text. The legacy Queue-era
    // implementation (legacy/skillmutation/SkillMutationLearnAsNew.LegacyFinal.cs,
    // OverrideQueuedUiSkillId + MutationQueuedSkillNamePatch, lines 599-622
    // and 1870-1879) shows the actual source: native calls
    // datSkillName.Get(int skillId, int context) to resolve the row's
    // display text, and for the synthetic/hidden slot it passes a STALE
    // native skill id (not the mutated/AddNew target) as skillId. The old
    // Queue intercepted this call directly, gated by the SAME forget-UI
    // cursor read (GBWK+0x88 -> +0x20 -> +0x14) used for SelectSkillID, so
    // it only rewrites the name lookup while the player is actually
    // focused on the synthetic row - never touching any of the real 8
    // owned-skill name lookups.
    //
    // Re-derived here for V3's own FullCapacityAddNewBridge transaction
    // (FullCapacityAddNewBridgeState.Active), without reviving any legacy
    // code - only the call target, gating logic, and override shape are
    // reused as a concept.
    [HarmonyPatch(typeof(datSkillName), nameof(datSkillName.Get), new[] { typeof(int), typeof(int) })]
    internal static class AddNewSkillNameLabelFix
    {
        internal static readonly bool Enabled = true;

        private static void Prefix(ref int __0, int __1)
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
                if (actionData == IntPtr.Zero || Marshal.ReadByte(actionData, 0x14) != 8) return;

                int unit = FullCapacityAddNewBridgeState.WatchedUnit;
                // Name rendering passes the demon id for list entries and 0
                // for the confirmation message. Ignore calls belonging to a
                // different demon, and never rewrite one of the real 8 slots.
                if (__1 > 0 && __1 != unit) return;

                if (__1 != 0)
                {
                    var stock = gbwk.pCurrentStock;
                    if (stock != null && stock.Pointer != IntPtr.Zero &&
                        HasSkill(stock, unchecked((ushort)__0)))
                    {
                        return;
                    }
                }

                int before = __0;
                __0 = FullCapacityAddNewBridgeState.Target;

                if (before != __0)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-SKILLNAME-LABEL-FIX; " +
                        $"unit={unit}; context={__1}; skillId {before}->{__0}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] AddNewSkillNameLabelFix prefix failed safely: {ex.Message}");
            }
        }

        private static bool HasSkill(Il2Cppnewdata_H.datUnitWork_t stock, ushort skill)
        {
            int count = Math.Min(stock.skillcnt, stock.skill.Length);
            for (int i = 0; i < count; i++)
            {
                if (unchecked((ushort)stock.skill[i]) == skill) return true;
            }
            return false;
        }
    }
}
