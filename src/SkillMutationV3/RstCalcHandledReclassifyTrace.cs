using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // OLD QUEUE LOOP SUPPRESSION ARCHAEOLOGY - "where does DefSkillResult
    // get set back to 2 for an already-handled candidate" investigation,
    // step 2. Read-only observer only. Never writes any field, never
    // suppresses anything.
    //
    // Background: DefaultSkillHandledReentryTrace confirmed
    // rstupdate.rstUpdateSeqDefaultSkill is NOT where the reclassification
    // happens - every observed call for a handled candidate (349) already
    // had DefSkillResult==2 at entry (ALREADY-2-AT-ENTRY in 100% of
    // samples). That pushes the reclassification further upstream, into
    // rstcalc.rstCalc's own DefaultSkill classification loop - static
    // disassembly from an earlier phase of this investigation located the
    // DefSkillResult write at VA 0x18227EEA7 ("mov byte[rbx+0x3c],cl"),
    // downstream of an ownership check at VA 0x18227EE4D (call
    // 0x18227F940) - but this class does NOT assume that CFG path is
    // exercised; it only captures the OBSERVABLE fact, at the rstCalc call
    // boundary, of whether DefSkillResult transitions from something other
    // than 2 to exactly 2 during a single invocation, for a candidate
    // HandledCandidatesObserver already considers handled.
    //
    // Hooks rstcalc.rstCalc's Prefix/Postfix - Harmony supports multiple
    // independent patches on one method; ForgetFlowRuntimeTrace already
    // patches this same method's Postfix for a different purpose (its own
    // FORGET-SEQ-CHANGE/FORGET-PUPSKILL-CHANGE tracking), so this adds no
    // new hook target, only more read-only work in an already-instrumented
    // per-frame call.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class RstCalcHandledReclassifyTrace
    {
        internal static readonly bool Enabled = true;

        private static bool _captured;
        private static long _stockPtrBefore;
        private static int _unitBefore;
        private static ushort _eventParamBefore;
        private static bool _handledBefore;
        private static sbyte _defSkillResultBefore;
        private static sbyte _eventNumsBefore;
        private static sbyte _eventOfsBefore;
        private static short _levelUpCntBefore;
        private static int _seqCurrentBefore;
        private static int _seqLastBefore;
        private static sbyte _seqChangeBefore;
        private static sbyte _pUpSkillResultBefore;

        private static void Prefix()
        {
            _captured = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                _stockPtrBefore = stock.Pointer.ToInt64();
                _unitBefore = stock.id;
                _eventParamBefore = gbwk.EventParam;
                _handledBefore = HandledCandidatesObserver.IsHandled(_stockPtrBefore, _eventParamBefore);
                _defSkillResultBefore = gbwk.DefSkillResult;
                _eventNumsBefore = gbwk.EventNums;
                _eventOfsBefore = gbwk.EventOfs;
                _levelUpCntBefore = gbwk.LevelUpCnt;
                _seqCurrentBefore = gbwk.SeqInfo.Current;
                _seqLastBefore = gbwk.SeqInfo.Last;
                _seqChangeBefore = gbwk.SeqInfo.Change;
                _pUpSkillResultBefore = gbwk.PUpSkillResult;
                _captured = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] RstCalcHandledReclassifyTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_captured) return;
            _captured = false;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;

                long stockPtrAfter = stock?.Pointer.ToInt64() ?? 0;
                ushort eventParamAfter = gbwk.EventParam;
                bool handledAfter = stockPtrAfter != 0 &&
                    HandledCandidatesObserver.IsHandled(stockPtrAfter, eventParamAfter);
                sbyte defSkillResultAfter = gbwk.DefSkillResult;

                // The headline event this class exists to catch: a
                // candidate ALREADY marked handled (before this rstCalc
                // call even started) whose DefSkillResult was NOT 2 at
                // entry becomes 2 by the time this call returns - i.e. the
                // classification loop re-decided "needs forget" for a
                // candidate that should already be closed.
                bool reclassifyEvent =
                    _handledBefore && _defSkillResultBefore != 2 && defSkillResultAfter == 2 &&
                    _eventParamBefore == eventParamAfter && _stockPtrBefore == stockPtrAfter;

                // Also log any OTHER DefSkillResult change for a handled
                // candidate, for completeness, but only when something
                // actually changed - avoid flooding the log with every
                // ordinary per-frame rstCalc call.
                bool anyChangeForHandled =
                    _handledBefore &&
                    (_defSkillResultBefore != defSkillResultAfter ||
                     _eventNumsBefore != gbwk.EventNums ||
                     _eventOfsBefore != gbwk.EventOfs ||
                     _levelUpCntBefore != gbwk.LevelUpCnt ||
                     _seqCurrentBefore != gbwk.SeqInfo.Current ||
                     _seqLastBefore != gbwk.SeqInfo.Last ||
                     _seqChangeBefore != gbwk.SeqInfo.Change ||
                     _pUpSkillResultBefore != gbwk.PUpSkillResult);

                if (!reclassifyEvent && !anyChangeForHandled) return;

                int frame = UnityEngine.Time.frameCount;
                sbyte eventNumsAfter = gbwk.EventNums;
                sbyte eventOfsAfter = gbwk.EventOfs;
                short levelUpCntAfter = gbwk.LevelUpCnt;
                int seqCurrentAfter = gbwk.SeqInfo.Current;
                int seqLastAfter = gbwk.SeqInfo.Last;
                sbyte seqChangeAfter = gbwk.SeqInfo.Change;
                sbyte pUpSkillResultAfter = gbwk.PUpSkillResult;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] RSTCALC-HANDLED-RECLASSIFY; " +
                    $"frame={frame}; unit={_unitBefore}; stockPtr=0x{_stockPtrBefore:X}; " +
                    $"eventParam {_eventParamBefore}->{eventParamAfter}; " +
                    $"handled {_handledBefore}->{handledAfter}; " +
                    $"defSkillResult {_defSkillResultBefore}->{defSkillResultAfter}; " +
                    $"eventNums {_eventNumsBefore}->{eventNumsAfter}; " +
                    $"eventOfs {_eventOfsBefore}->{eventOfsAfter}; " +
                    $"levelUpCnt {_levelUpCntBefore}->{levelUpCntAfter}; " +
                    $"seqCurrent {_seqCurrentBefore}->{seqCurrentAfter}; " +
                    $"seqLast {_seqLastBefore}->{seqLastAfter}; " +
                    $"seqChange {_seqChangeBefore}->{seqChangeAfter}; " +
                    $"pUpSkillResult {_pUpSkillResultBefore}->{pUpSkillResultAfter}; " +
                    $"RECLASSIFY-EVENT={reclassifyEvent}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] RstCalcHandledReclassifyTrace postfix failed safely: {ex.Message}");
            }
        }
    }
}
