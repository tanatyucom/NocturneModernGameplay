using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - DEFAULTSKILL ITERATOR ENTRY/EXIT TRACE.
    // Read-only observer only. Never writes any field.
    //
    // Purpose: static disassembly (capstone, VA 0x18227C330, cross-checked
    // against cpp2il ISIL) identified this exact managed method as the ONLY
    // place in the binary that writes GBWK.EventParam from the per-species
    // curriculum table (rstcalc.rstCalcEventInfo(ref ushort pParamBuf) ->
    // ISIL line ~1464 "Move [r12], rax" == capstone VA 0x18227C5FB
    // "mov word ptr [r12], ax"). Its own advance-bookkeeping write is
    // GBWK.EventOfs += 1 (ISIL line ~1473 "[rdx+49]=rax", capstone VA
    // 0x18227C623 "mov byte ptr [rdx+0x31], al").
    //
    // This class exists to turn that static finding into a runtime CONFIRM:
    // log every call to this method with EventParam/EventNums/EventOfs
    // before and after, plus the surrounding state (unit, frame, SeqInfo,
    // DefSkillResult). If a call's before/after shows EventParam changing
    // from the just-consumed Power-Up target (e.g. 16) to the previously
    // forgotten curriculum skill (e.g. 349) during the bridge's own forget
    // episode, that is direct runtime proof that this iterator is being
    // re-invoked (a "refetch") rather than the bridge merely leaving a
    // stale value in place - resolving item #1 of the
    // "DEFAULTSKILL ITERATOR BOOKKEEPING / COUNTER + REFETCH ROOT-CAUSE
    // CONFIRMATION" task.
    //
    // Two confirmed callers exist in the whole binary (whole-.text scan for
    // E8 call opcodes resolving to this VA): 0x18227C6E0 (inside
    // rstCalcEvo(), evolution-event path, NOT relevant to Power-Up/forget)
    // and 0x18227EDC6 (inside a helper NOT directly visible in
    // rstUpdateSeqDefaultSkill's own ISIL body - it must be reached through
    // one of that method's own callees, most likely the classification
    // helper at VA 0x182285B50). This trace does not need to resolve which
    // caller fired - the before/after snapshot plus frame/seq context is
    // enough to tell native's own call apart from the bridge-triggered one
    // by cross-referencing FULLCAP-ADDNEW-* and FORGET-SEQ-CHANGE markers
    // already emitted elsewhere.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcEventInfo))]
    internal static class DefaultSkillIteratorTrace
    {
        // HI-PIXIE STALL HEALTH CHECK (2026-09-13): confirmed the stall was
        // diagnostic-hook overhead, not the AddNew bridge itself - disabled
        // alongside every other high-frequency observer, then re-enabled
        // ONE AT A TIME per investigation need from then on, deliberately
        // avoiding stacking multiple observers on the same hot function
        // (rstupdate.rstUpdate / rstcalc.rstCalc / rstupdate.
        // rstUpdateSeqDefaultSkill). This one hooks rstcalc.
        // rstCalcEventInfo specifically - a distinct, lower-frequency
        // function (only called during actual curriculum classification,
        // not every frame) - re-enabled for the EventOfs-stuck-at-7
        // investigation (why unit=60's 349 checkpoint never advances its
        // table position the way unit=59's own normal fetches did:
        // 0->8->9).
        internal static readonly bool Enabled = true;

        private static bool _captured;
        private static int _unitBefore;
        private static long _stockPtrBefore;
        private static ushort _eventParamBefore;
        private static sbyte _eventNumsBefore;
        private static sbyte _eventOfsBefore;
        private static sbyte _defSkillResultBefore;
        private static int _seqCurrentBefore;
        private static int _seqLastBefore;
        private static sbyte _flagBefore;
        private static short _levelUpCntBefore;
        private static sbyte _pUpResultBefore;

        private static void Prefix()
        {
            _captured = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;

                _unitBefore = stock?.id ?? -1;
                _stockPtrBefore = stock?.Pointer.ToInt64() ?? 0;
                _eventParamBefore = gbwk.EventParam;
                _eventNumsBefore = gbwk.EventNums;
                _eventOfsBefore = gbwk.EventOfs;
                _defSkillResultBefore = gbwk.DefSkillResult;
                _seqCurrentBefore = gbwk.SeqInfo.Current;
                _seqLastBefore = gbwk.SeqInfo.Last;
                _flagBefore = gbwk.Flag;
                _levelUpCntBefore = gbwk.LevelUpCnt;
                _pUpResultBefore = gbwk.PUpSkillResult;
                _captured = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DefaultSkillIteratorTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix(sbyte __result)
        {
            if (!_captured) return;
            _captured = false;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;

                int unitAfter = stock?.id ?? -1;
                long stockPtrAfter = stock?.Pointer.ToInt64() ?? 0;
                ushort eventParamAfter = gbwk.EventParam;
                sbyte eventNumsAfter = gbwk.EventNums;
                sbyte eventOfsAfter = gbwk.EventOfs;
                sbyte defSkillResultAfter = gbwk.DefSkillResult;
                int seqCurrentAfter = gbwk.SeqInfo.Current;
                int seqLastAfter = gbwk.SeqInfo.Last;
                sbyte flagAfter = gbwk.Flag;
                short levelUpCntAfter = gbwk.LevelUpCnt;
                sbyte pUpResultAfter = gbwk.PUpSkillResult;

                // Feeds the episode-latch clear boundary (see
                // HandledCandidatesObserver.OnLevelUpCntObserved) - this is
                // the existing, already-proven sample point for
                // GBWK.LevelUpCnt, reused rather than adding a new hook.
                HandledCandidatesObserver.OnLevelUpCntObserved(stockPtrAfter, levelUpCntAfter);

                int frame = UnityEngine.Time.frameCount;

                // MULTI-LEVEL EPISODE BOUNDARY TRACE PoC: transition-driven
                // only (see MultiLevelStateTransitionTrace) - reuses this
                // already-open gbwk/stock rather than adding a new Harmony
                // patch. Real level (not LevelUpCnt-inferred) is read
                // directly from stock.level (datUnitWork_s+0x24).
                ushort levelNow = stock?.level ?? 0;
                bool bit6Now = stock != null && stock.Pointer != IntPtr.Zero &&
                    (stock.flag & 0x40) != 0;
                MultiLevelStateTransitionTrace.Observe(
                    frame, unitAfter, stockPtrAfter, levelNow, levelUpCntAfter,
                    seqCurrentAfter, seqLastAfter, gbwk.TargetIndex, gbwk.TargetCnt, bit6Now);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] DEFAULTSKILL-ITERATOR-CALL; " +
                    $"frame={frame}; unit={_unitBefore}->{unitAfter}; " +
                    $"stockPtr=0x{_stockPtrBefore:X}; " +
                    $"eventParam {_eventParamBefore}->{eventParamAfter}; " +
                    $"eventNums {_eventNumsBefore}->{eventNumsAfter}; " +
                    $"eventOfs {_eventOfsBefore}->{eventOfsAfter}; " +
                    $"defSkillResult {_defSkillResultBefore}->{defSkillResultAfter}; " +
                    $"seqCurrent {_seqCurrentBefore}->{seqCurrentAfter}; " +
                    $"seqLast {_seqLastBefore}->{seqLastAfter}; " +
                    $"flag {_flagBefore}->{flagAfter}; " +
                    $"levelUpCnt {_levelUpCntBefore}->{levelUpCntAfter}; " +
                    $"pUpResult {_pUpResultBefore}->{pUpResultAfter}; " +
                    $"resultEventType={__result}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DefaultSkillIteratorTrace postfix failed safely: {ex.Message}");
            }
        }
    }

    // Skill Power-Up AddNew V3 - GETDEFAULTSKILL CALL-BOUNDARY TRACE.
    // Read-only observer only. Never writes any field.
    //
    // Purpose: DefaultSkillIteratorTrace (hooked directly on rstcalc.
    // rstCalcEventInfo) shows EventParam staying unchanged across every
    // individual call, even though the GLOBAL value (per
    // EventParamPerFrameWatcher) demonstrably changes from the stale value
    // to the reoffered curriculum skill (e.g. 396/16 -> 349) within the SAME
    // frame that rstCalcEventInfo's own call returns. Full disassembly of
    // the caller (VA 0x18227EDC0-0x18227EE97, identified as rstcalc.
    // rstGetDefaultSkill() - the only ISIL-dump method whose analysis threw
    // an exception, consistent with it never having been captured in
    // rstcalc.txt as plain text) shows this caller REPEATEDLY calls
    // rstCalcEventInfo in a loop (jmp 0x18227edc0 at VA 0x18227EE97),
    // re-deriving its `ref ushort` argument EVERY iteration as
    // "(some GBWK-like pointer reloaded from a static cache) + 0x32" -
    // structurally the same offset as GBWK.EventParam, but reached through
    // rstGetDefaultSkill's OWN internal static-field cache slot, not
    // necessarily the exact code path DefaultSkillIteratorTrace's Prefix/
    // Postfix samples via rstinit.GBWK at the moment of ITS OWN hook firing.
    // The ownership decision itself (VA 0x18227EE4D: reads word[rax+0x32],
    // calls 0x18227F940 for the actual owned/not-owned check, then VA
    // 0x18227EEA7: mov byte[rbx+0x3c],cl writes DefSkillResult) happens
    // AFTER the loop exits - so wrapping the ENTIRE rstGetDefaultSkill()
    // call (not just the inner rstCalcEventInfo sub-call) is the only way
    // to see the true before/after EventParam+DefSkillResult delta for
    // whichever iteration actually "stuck".
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstGetDefaultSkill))]
    internal static class GetDefaultSkillCallBoundaryTrace
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled alongside every other
        // observer in this file - see DefaultSkillIteratorTrace.Enabled.
        internal static readonly bool Enabled = false;

        internal static void LogPatchStatus()
        {
            try
            {
                var method = typeof(rstcalc).GetMethod(nameof(rstcalc.rstGetDefaultSkill));
                if (method == null)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] GETDEFAULTSKILL-PATCH-STATUS; " +
                        "methodInfo=NULL (GetMethod failed to resolve rstcalc.rstGetDefaultSkill).");
                    return;
                }

                var info = HarmonyLib.Harmony.GetPatchInfo(method);
                int prefixes = info?.Prefixes?.Count ?? 0;
                int postfixes = info?.Postfixes?.Count ?? 0;
                IntPtr fnPtr = method.MethodHandle.GetFunctionPointer();
                MelonLogger.Msg(
                    "[NocturneModernGameplay] GETDEFAULTSKILL-PATCH-STATUS; " +
                    $"methodInfo=FOUND; declaringType={method.DeclaringType}; " +
                    $"prefixes={prefixes}; postfixes={postfixes}; " +
                    $"functionPointer=0x{fnPtr.ToInt64():X}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] GetDefaultSkillCallBoundaryTrace.LogPatchStatus failed: {ex}");
            }
        }

        private static bool _captured;
        private static int _unitBefore;
        private static ushort _eventParamBefore;
        private static sbyte _eventNumsBefore;
        private static sbyte _eventOfsBefore;
        private static sbyte _defSkillResultBefore;
        private static int _seqCurrentBefore;
        private static int _seqLastBefore;

        private static void Prefix()
        {
            _captured = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;

                _unitBefore = stock?.id ?? -1;
                _eventParamBefore = gbwk.EventParam;
                _eventNumsBefore = gbwk.EventNums;
                _eventOfsBefore = gbwk.EventOfs;
                _defSkillResultBefore = gbwk.DefSkillResult;
                _seqCurrentBefore = gbwk.SeqInfo.Current;
                _seqLastBefore = gbwk.SeqInfo.Last;
                _captured = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] GetDefaultSkillCallBoundaryTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix(sbyte __result)
        {
            if (!_captured) return;
            _captured = false;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;

                int unitAfter = stock?.id ?? -1;
                ushort eventParamAfter = gbwk.EventParam;
                sbyte eventNumsAfter = gbwk.EventNums;
                sbyte eventOfsAfter = gbwk.EventOfs;
                sbyte defSkillResultAfter = gbwk.DefSkillResult;
                int seqCurrentAfter = gbwk.SeqInfo.Current;
                int seqLastAfter = gbwk.SeqInfo.Last;

                int frame = UnityEngine.Time.frameCount;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] GETDEFAULTSKILL-CALL; " +
                    $"frame={frame}; unit={_unitBefore}->{unitAfter}; " +
                    $"eventParam {_eventParamBefore}->{eventParamAfter}; " +
                    $"eventNums {_eventNumsBefore}->{eventNumsAfter}; " +
                    $"eventOfs {_eventOfsBefore}->{eventOfsAfter}; " +
                    $"defSkillResult {_defSkillResultBefore}->{defSkillResultAfter}; " +
                    $"seqCurrent {_seqCurrentBefore}->{seqCurrentAfter}; " +
                    $"seqLast {_seqLastBefore}->{seqLastAfter}; " +
                    $"result={__result}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] GetDefaultSkillCallBoundaryTrace postfix failed safely: {ex.Message}");
            }
        }
    }
}
