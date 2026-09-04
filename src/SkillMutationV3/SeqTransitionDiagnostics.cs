using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Read-only investigation telemetry: logs GBWK.SeqInfo.Current transitions
    // for GBWK.pCurrentStock, filtered to unit 59/60 (PowerUpMutationCfgDiagnostics
    // .IsTargetUnit), one line per actual change only. Added to directly observe
    // the seq21 -> seq22 (forget confirm) -> ? -> seq8/9/10 (default skill /
    // skill power-up / mutation) sequence for a full-skill-roster unit.
    //
    // Anchor: rstupdate.rstUpdate(dds3ProcessID_t PID) - the top-level per-tick
    // dispatcher (VA 0x18228CDE0, CONFIRMED in 01_CURRENT_STATE.md), called every
    // frame regardless of which seq value is active, so this observes every seq
    // value the state machine passes through (unlike cmbChkSkillOwner/rstCalc-
    // SkillPowerUpCore hooks, which only fire when native happens to reach them).
    //
    // Read-only: never writes SeqInfo.Current, TargetIndex, TargetCnt,
    // pCurrentStock, WorkStock, or any skill array element. The only state this
    // class mutates is its own two private tracking fields (last observed unit /
    // seq value), used solely to detect a change worth logging - this is
    // diagnostic bookkeeping, not native memory.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class SeqTransitionDiagnostics
    {
        private static int _lastUnit = int.MinValue;
        private static int _lastSeq = int.MinValue;

        private static void Postfix()
        {
            if (!PowerUpMutationCfgDiagnostics.Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                int unit = stock.id;
                if (!PowerUpMutationCfgDiagnostics.IsTargetUnit(unit)) return;

                int seq = gbwk.SeqInfo.Current;
                bool sameUnit = unit == _lastUnit;
                if (sameUnit && seq == _lastSeq) return;

                string fromSeq = sameUnit ? _lastSeq.ToString() : "?";
                _lastUnit = unit;
                _lastSeq = seq;

                int frame = UnityEngine.Time.frameCount;
                int pUpSkillResult = gbwk.PUpSkillResult;
                int pUpSkillIndex = gbwk.PUpSkillIndex;
                uint flagRaw = stock.flag;
                bool bit6 = (flagRaw & 0x40) != 0;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-SEQ-TRANSITION; " +
                    $"frame={frame} unit={unit} from={fromSeq} to={seq} " +
                    $"pUpSkillResult={pUpSkillResult} pUpSkillIndex={pUpSkillIndex} " +
                    $"stockFlagRaw=0x{flagRaw:X} bit6={bit6}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] V3-SEQ-TRANSITION failed safely: {ex.Message}");
            }
        }
    }
}
