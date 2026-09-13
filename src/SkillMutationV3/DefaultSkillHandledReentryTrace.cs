using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // OLD QUEUE LOOP SUPPRESSION ARCHAEOLOGY - "where does 349 get
    // reclassified as DefSkillResult=2 a second time" investigation.
    // Read-only observer only. Never writes any field, never suppresses
    // anything.
    //
    // Background: CoreReentryHandledCheck's one-shot Core consumption guard
    // (VA-level: rstcalc.rstCalcSkillPowerUpCore) is CONFIRMED working at
    // its own boundary (SUPPRESS-REENTRY, coreResult=0, PUpSkillID
    // unchanged, sequence proceeds cleanly to the next unit) - but real-
    // machine testing showed the player still sees the "息吹の具足" (skill
    // 349) forget-UI itself reappear a second time. That means the bug is
    // NOT at the Core/Mutation-roll layer at all - it is upstream, at
    // whatever decides to set GBWK.DefSkillResult=2 for EventParam=349 a
    // SECOND time, causing rstUpdateSeqDefaultSkill to open seq21 again.
    //
    // This class hooks rstupdate.rstUpdateSeqDefaultSkill's Prefix/Postfix
    // purely to answer one question: by the time this function is ENTERED
    // for the recurrence, is DefSkillResult ALREADY 2 (meaning this
    // function is innocent - just presenting an already-made decision), or
    // does it become 2 DURING this call (meaning this function itself is
    // where the reclassification happens)? Per the static disassembly
    // already on file (rstUpdateSeqDefaultSkill fully disassembled this
    // session - VA 0x182288790 region - the only write near
    // DefSkillResult-adjacent logic was Change=1/Current=21, not
    // DefSkillResult itself), the expectation is DefSkillResult is already
    // 2 at Prefix time - which would point the investigation back further,
    // into rstcalc.rstCalc's own DefaultSkill classification loop.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class DefaultSkillHandledReentryTrace
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
        private static sbyte _seqNextBefore;
        private static sbyte _seqChangeBefore;

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
                _seqNextBefore = gbwk.SeqInfo.Next;
                _seqChangeBefore = gbwk.SeqInfo.Change;
                _captured = true;

                // Only interesting when this call concerns a candidate
                // HandledCandidatesObserver already marked handled AND the
                // native result is (or is about to become) the "needs
                // forget" code - avoids logging every ordinary DefaultSkill
                // call.
                if (_handledBefore)
                {
                    int frame = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] DEFAULTSKILL-HANDLED-REENTRY-ENTRY; " +
                        $"frame={frame}; unit={_unitBefore}; stockPtr=0x{_stockPtrBefore:X}; " +
                        $"eventParam={_eventParamBefore}; handled={_handledBefore}; " +
                        $"defSkillResult={_defSkillResultBefore}; " +
                        $"eventNums={_eventNumsBefore}; eventOfs={_eventOfsBefore}; " +
                        $"levelUpCnt={_levelUpCntBefore}; " +
                        $"seqCurrent={_seqCurrentBefore}; seqLast={_seqLastBefore}; " +
                        $"seqNext={_seqNextBefore}; seqChange={_seqChangeBefore}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DefaultSkillHandledReentryTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_captured) return;
            _captured = false;
            try
            {
                if (!_handledBefore) return;

                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                sbyte defSkillResultAfter = gbwk.DefSkillResult;
                sbyte eventNumsAfter = gbwk.EventNums;
                sbyte eventOfsAfter = gbwk.EventOfs;
                short levelUpCntAfter = gbwk.LevelUpCnt;
                int seqCurrentAfter = gbwk.SeqInfo.Current;
                int seqLastAfter = gbwk.SeqInfo.Last;
                sbyte seqNextAfter = gbwk.SeqInfo.Next;
                sbyte seqChangeAfter = gbwk.SeqInfo.Change;

                int frame = UnityEngine.Time.frameCount;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] DEFAULTSKILL-HANDLED-REENTRY-EXIT; " +
                    $"frame={frame}; unit={_unitBefore}; stockPtr=0x{_stockPtrBefore:X}; " +
                    $"eventParam={_eventParamBefore}; handled={_handledBefore}; " +
                    $"defSkillResult {_defSkillResultBefore}->{defSkillResultAfter}; " +
                    $"eventNums {_eventNumsBefore}->{eventNumsAfter}; " +
                    $"eventOfs {_eventOfsBefore}->{eventOfsAfter}; " +
                    $"levelUpCnt {_levelUpCntBefore}->{levelUpCntAfter}; " +
                    $"seqCurrent {_seqCurrentBefore}->{seqCurrentAfter}; " +
                    $"seqLast {_seqLastBefore}->{seqLastAfter}; " +
                    $"seqNext {_seqNextBefore}->{seqNextAfter}; " +
                    $"seqChange {_seqChangeBefore}->{seqChangeAfter}; " +
                    $"conclusion={(_defSkillResultBefore == 2 ? "ALREADY-2-AT-ENTRY(innocent-presenter)" : "BECAME-2-DURING-CALL(reclassifier)")}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DefaultSkillHandledReentryTrace postfix failed safely: {ex.Message}");
            }
        }
    }
}
