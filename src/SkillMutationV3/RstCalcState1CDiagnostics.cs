using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Read-only investigation telemetry correlating rstcalc.rstCalc's own
    // finish (i.e. after ALL Harmony patches on rstCalcSkillPowerUpCore -
    // including MutationDisabledBit6Guard / SkillPowerUpChanceAlwaysPatch -
    // have already run and the corrected value has been returned to this
    // native caller) with GBWK.PUpSkillResult and State_182e31630+0x1C at
    // that same instant.
    //
    // Why this instead of hooking VA 0x18227F012 directly: this session's
    // disassembly (.analysis/disasm_state1c_writer_context.py) found that
    // immediately before the State+0x1C write, rstCalc re-reads
    // GBWK.PUpSkillResult FRESH from memory (movsx edx, byte ptr [gbwk+0x4b]
    // at 0x18227F000) rather than reusing a register carried from the Core
    // call - and the only instructions between the two Core call sites'
    // "mov [gbwk+0x4b], al" writes (0x18227E9CE / 0x18227EFE3) and this
    // re-read do not touch GBWK.PUpSkillResult again. So, statically,
    // State+0x1C is guaranteed to end up equal to whatever the LAST write to
    // GBWK.PUpSkillResult was - there is no instruction-level window for a
    // "State+0x1C=raw, PUpSkillResult=corrected" mismatch. Reading both
    // AFTER rstCalc returns (this Postfix) is a read-only way to cross-check
    // that inference at runtime without an inline/raw hook on 0x18227F012
    // itself.
    //
    // rstCalc is a per-frame function (not skill-specific) - the old,
    // disabled MutationResultCalculatorBoundaryPatch (SkillMutationAlways.cs)
    // already established the same fact and used a change-dedup guard for
    // the same reason; this class does the same to avoid per-frame log
    // spam.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class RstCalcState1CDiagnostics
    {
        private static string _lastState = string.Empty;

        private static void Postfix()
        {
            if (!PowerUpMutationCfgDiagnostics.Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;
                int unit = stock.id;
                if (!PowerUpMutationCfgDiagnostics.IsTargetUnit(unit)) return;

                int seq = gbwk.SeqInfo.Current;
                sbyte pUpResult = gbwk.PUpSkillResult;
                sbyte pUpIndex = gbwk.PUpSkillIndex;
                int? state1C = SeqSkillPowerUpBit6ClearDiagnostics.TryReadState1C();

                string state1CText = state1C.HasValue ? state1C.Value.ToString() : "NULL";
                string state = $"{unit}/{seq}/{pUpResult}/{pUpIndex}/{state1CText}";
                if (string.Equals(state, _lastState, StringComparison.Ordinal)) return;
                _lastState = state;

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-RSTCALC-STATE1C; " +
                    $"unit={unit} frame={frame} seq={seq} " +
                    $"pUpSkillResult={pUpResult} pUpSkillIndex={pUpIndex} state1C={state1CText}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] RstCalcState1CDiagnostics postfix failed safely: {ex.Message}");
            }
        }
    }
}
