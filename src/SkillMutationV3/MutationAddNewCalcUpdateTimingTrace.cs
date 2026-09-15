using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Mutation AddNew investigation (investigations/ACQUISITION_LEARNASNEW/
    // PLAN.md) - Calc/Update timing check (2026-09-15).
    // Read-only observer only. Never writes any field, never touches
    // hardware debug registers - plain Harmony Prefix/Postfix logging.
    //
    // Purpose: this session's static disassembly confirmed GBWK.EventParam
    // is written by rstcalc.rstCalcEventInfo (VA 0x18227C5FB) and
    // GBWK.DefSkillResult is set moments later by the retry loop inside
    // rstcalc.rstGetDefaultSkill (VA 0x18227EEA7) - both reached from
    // rstcalc.rstCalc, NOT rstupdate.rstUpdateSeqDefaultSkill. Whether
    // rstUpdateSeqDefaultSkill consumes that state in the SAME frame or a
    // LATER one could not be determined statically (both rstCalc and
    // rstUpdate are dispatched indirectly - zero direct call xrefs found in
    // the whole .text section). This is the last open prerequisite before
    // evaluating a Mutation AddNew design that reuses this native pipeline.
    //
    // Four minimal capture points, one shared log tag (MUTADDNEW-TIMING)
    // with a `point` field so a single grep reconstructs the sequence in
    // frame order:
    //   1. rstcalc.rstCalcEventInfo Postfix        - EventParam write site.
    //   2. rstcalc.rstGetDefaultSkill Postfix       - DefSkillResult write
    //      site (the whole retry-loop call boundary; the exact instruction
    //      cannot be hooked directly since it is inlined, not a call).
    //   3. rstupdate.rstUpdateSeqDefaultSkill Prefix - the Update-phase
    //      consumer's entry point.
    //   4. rstupdate.rstAddSkill Prefix              - reached only when
    //      DefSkillResult != 2 (native's own "insert" path); absence in a
    //      given episode means the forget-flow branch was taken instead.
    internal static class MutationAddNewCalcUpdateTimingTrace
    {
        internal static readonly bool Enabled = true;

        private static void Log(string point, sbyte? methodResult = null)
        {
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                var stock = gbwk.pCurrentStock;

                int frame = UnityEngine.Time.frameCount;
                int seq = gbwk.SeqInfo.Current;
                int unit = stock?.id ?? -1;
                ushort eventParam = gbwk.EventParam;
                sbyte defSkillResult = gbwk.DefSkillResult;
                string resultStr = methodResult.HasValue ? methodResult.Value.ToString() : "n/a";

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MUTADDNEW-TIMING; " +
                    $"point={point}; frame={frame}; seq={seq}; unit={unit}; " +
                    $"eventParam={eventParam}; defSkillResult={defSkillResult}; methodResult={resultStr}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationAddNewCalcUpdateTimingTrace log failed safely: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcEventInfo))]
        internal static class CalcEventInfoPoint
        {
            private static void Postfix(sbyte __result) => Log("1-CalcEventInfo.Postfix", __result);
        }

        [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstGetDefaultSkill))]
        internal static class GetDefaultSkillPoint
        {
            private static void Postfix(sbyte __result) => Log("2-GetDefaultSkill.Postfix", __result);
        }

        [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
        internal static class UpdateSeqDefaultSkillPoint
        {
            private static void Prefix() => Log("3-UpdateSeqDefaultSkill.Prefix");
        }

        [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstAddSkill))]
        internal static class AddSkillPoint
        {
            private static void Prefix() => Log("4-AddSkill.Prefix");
        }
    }
}
