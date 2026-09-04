using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Minimal, compile-time-isolated experiment for Design B (per-unit inline
    // gate). This file changes NOTHING else in the mod: production behavior
    // (Global Pending Queue, Native Context Snapshot/Restore, HandledSlots,
    // OverwriteSuppressed) is completely untouched. Restore production by
    // setting Enabled = false.
    //
    // Purpose: answer exactly one question - can rstCalcSeqDevilLevelUp be
    // held from executing, for several seconds, for ONE specific unit's
    // already-active Learn-As-New transaction, without native state drifting?
    //
    // Explicitly NOT done by this experiment (by design, per review):
    //   - pCurrentStock / WorkStock are never written here.
    //   - TargetIndex/TargetCnt are never written here.
    //   - SeqInfo fields are never written here.
    //   - Timer is never frozen/altered here.
    //   - rstSetCurrentDevil is not patched here.
    //   - OverwriteSuppressed / rstOverWriteSkill gating is not enabled here.
    // This experiment only ever returns a non-zero result from
    // rstCalcSeqDevilLevelUp's Prefix and skips its native body. Nothing else.
    internal static class ExperimentalInlineGateV2
    {
        internal static readonly bool Enabled = true;

        private static bool _wasGated;
        private static int _framesHeld;
        private const int LogIntervalFrames = 60; // ~1s at 60fps, to avoid log spam during a multi-second hold

        // Gate condition: ONLY true when the unit rstCalcSeqDevilLevelUp would
        // currently act on (GBWK.pCurrentStock) is the SAME stock as the
        // in-flight Learn-As-New transaction (_active in SkillMutationLearnAsNew).
        // This is intentionally narrow: a different unit, a not-yet-active
        // candidate, or a queued-but-not-yet-active Pending item do NOT gate.
        private static bool ShouldGateNow(out IntPtr currentStockPointer)
        {
            currentStockPointer = IntPtr.Zero;
            if (!Enabled) return false;
            var gbwk = rstinit.GBWK;
            if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return false;
            var currentStock = gbwk.pCurrentStock;
            if (currentStock == null || currentStock.Pointer == IntPtr.Zero) return false;
            currentStockPointer = currentStock.Pointer;
            return SkillMutationLearnAsNew.HasActiveTransactionFor(currentStock.Pointer);
        }

        private static string CaptureFields(IntPtr currentStockPointer)
        {
            try
            {
                var gbwk = rstinit.GBWK;
                var seq = gbwk.SeqInfo;
                var currentStock = gbwk.pCurrentStock;
                var workStock = gbwk.WorkStock;
                int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero ? -1 : currentStock.id;
                int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero ? -1 : workStock.id;
                IntPtr currentStockPtr = currentStock?.Pointer ?? IntPtr.Zero;
                IntPtr workStockPtr = workStock?.Pointer ?? IntPtr.Zero;
                byte gbwk4a = Marshal.ReadByte(gbwk.Pointer, 0x4a);
                string workStock4a = workStockPtr != IntPtr.Zero
                    ? Marshal.ReadByte(workStockPtr, 0x4a).ToString() : "n/a";

                return
                    $"frame={UnityEngine.Time.frameCount} " +
                    $"currentUnit={currentUnit} workUnit={workUnit} " +
                    $"currentStockPtr=0x{currentStockPtr.ToInt64():X} workStockPtr=0x{workStockPtr.ToInt64():X} " +
                    $"targetIndex={gbwk.TargetIndex} targetCnt={gbwk.TargetCnt} " +
                    $"gbwk+0x4a={gbwk4a} workStock+0x4a={workStock4a} " +
                    $"seqCurrent={seq.Current} seqNext={seq.Next} seqLast={seq.Last} " +
                    $"seqChange={seq.Change} seqMesFlag={seq.MesFlag} seqTimer={seq.Timer} " +
                    $"activeTransaction=[{SkillMutationLearnAsNew.ActiveSummary}].";
            }
            catch (Exception ex)
            {
                return $"capture-failed-safely: {ex.Message}";
            }
        }

        // Called from the Prefix every frame. Returns true if the native body
        // should be skipped (gate held), false if it should run normally.
        internal static bool OnPrefix(ref int forcedResult)
        {
            bool gate = ShouldGateNow(out IntPtr currentStockPointer);

            if (gate)
            {
                if (!_wasGated)
                {
                    _wasGated = true;
                    _framesHeld = 0;
                    MelonLogger.Msg("[NocturneModernGameplay] INLINE-GATE-V2 begin; " +
                        CaptureFields(currentStockPointer));
                }
                else
                {
                    _framesHeld++;
                    if (_framesHeld % LogIntervalFrames == 0)
                    {
                        MelonLogger.Msg("[NocturneModernGameplay] INLINE-GATE-V2 held; " +
                            $"framesHeld={_framesHeld} " + CaptureFields(currentStockPointer));
                    }
                }
                forcedResult = 1; // non-zero: caller (rstCalc) takes its existing, already-confirmed early-return path
                return true;
            }

            if (_wasGated)
            {
                // Gate is releasing this frame (transaction completed/cancelled/failed
                // elsewhere - this code does not decide completion, it only observes it).
                MelonLogger.Msg("[NocturneModernGameplay] INLINE-GATE-V2 release; " +
                    $"framesHeld={_framesHeld} " + CaptureFields(currentStockPointer));
                _wasGated = false;
                _framesHeld = 0;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSeqDevilLevelUp))]
    [HarmonyPriority(Priority.First)]
    internal static class ExperimentalInlineGateV2Patch
    {
        private static bool Prefix(ref int __result)
        {
            if (!ExperimentalInlineGateV2.Enabled) return true;
            int forced = 0;
            bool skip = ExperimentalInlineGateV2.OnPrefix(ref forced);
            if (skip)
            {
                __result = forced;
                return false; // skip native body entirely; nothing it does gets to run
            }
            return true; // let native rstCalcSeqDevilLevelUp run exactly as before
        }
    }
}
