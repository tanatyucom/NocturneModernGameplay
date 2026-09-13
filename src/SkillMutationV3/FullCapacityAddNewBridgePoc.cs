using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - FULL-CAPACITY BRIDGE PoC, SEQ10-PRESERVING
    // REDESIGN.
    //
    // Revision history / why this changed: the prior revision of this file
    // returned false from rstUpdateSeqSkillPowerUp's own Prefix, skipping
    // native's ENTIRE seq10 body and jumping straight to
    // GBWK.SeqInfo.Current=21. Runtime testing found three symptoms:
    //   - Power-Up acquisition sound never played for the full-capacity
    //     case (it does play for the empty-slot case).
    //   - GBWK.EventParam stayed stuck at a stale value (an earlier,
    //     already-finished DefaultSkill event) and got re-classified by
    //     native later, causing a spurious second forget-prompt for that
    //     same old skill.
    //   - The same level-up episode was observed to loop back through
    //     seq8/9/10 an extra time it would not have taken natively,
    //     including one occurrence of a fresh (legitimate) Mutation roll
    //     immediately after the AddNew completed.
    //
    // A full disassembly of rstUpdateSeqSkillPowerUp (VA 0x18228C770, cross-
    // checked against .analysis/cpp2il/IsilDump/Assembly-CSharp/rstupdate.txt,
    // a decompiled ISIL dump of the real assembly) found that native's own
    // tail - AFTER the ownership write - runs:
    //   rstOverWriteSkill(ref stock.skill[PUpSkillIndex], PUpSkillID)
    //     -> the actual *pSkill = NewSkillID write, PLUS (confirmed this
    //        session, VA 0x182285FF0 fully disassembled) an internal
    //        PUpSkillIndex-keyed presentation sub-call chain
    //        (0x182281880 -> ... -> 0x1815583e0 -> the same
    //        UnityPlayer.dll string-format thunk found earlier at
    //        0x1816f9470) - this looks like slot-position UI glyph work,
    //        not the "you learned it" audio/message.
    //   0x182281A80(category)   - species+category indexed message/pose
    //                             lookup (presentation)
    //   0x18227C080(0)          - copies GBWK.MotionReq* fields into a
    //                             presentation object (animation staging)
    //   0x182280A50(PUpSkillIndex, 0) - the main acquisition presentation
    //                             dispatch (most likely the missing sound)
    //   GBWK.Flag = 1 (ordinary) or 4 (mutation)
    //   GBWK.PUpSkillResult = -1 (0xFF)  - marks this Power-Up event
    //                             consumed; the prior revision never
    //                             reached this because it returned false
    //                             before the native body ever ran
    //
    // Skipping the WHOLE method skipped all of the above, not just the
    // ownership write - which plausibly explains all three symptoms as one
    // root cause (STRONGLY SUPPORTED, not proven with an isolated control
    // test yet).
    //
    // New design: let rstUpdateSeqSkillPowerUp run to completion natively
    // (this class's own Prefix ALWAYS returns true now). A SEPARATE Harmony
    // patch on rstupdate.rstOverWriteSkill itself - a clean, real managed
    // method, signature `void rstOverWriteSkill(ref int pSkill, ushort
    // NewSkillID)` per the ISIL dump - suppresses ONLY the specific call
    // that would overwrite this bridge's recorded source slot with this
    // bridge's recorded target, for this one Power-Up event. Every other
    // call to rstOverWriteSkill (any other unit, any other Power-Up,
    // Mutation, or a full-capacity case this bridge did not arm for)
    // proceeds completely unmodified.
    //
    // Sequencing across the two patches, coordinated via
    // FullCapacityAddNewBridgeArming (a small piece of shared state, NOT
    // FullCapacityAddNewBridgeState - that one still tracks the in-flight
    // forget-episode only):
    //   rstUpdateSeqSkillPowerUp Prefix (Trigger)
    //     - evaluate guard conditions; if met, arm (record unit/stock/
    //       source/target) but do NOT touch EventParam/SeqInfo yet and do
    //       NOT return false - let native run.
    //   rstOverWriteSkill Prefix (Suppressor)
    //     - only if armed AND NewSkillID/pSkill match the armed target/
    //       source exactly: mark "suppressed this call" and return false.
    //       Otherwise return true (unrelated call - do nothing).
    //   rstUpdateSeqSkillPowerUp Postfix (Trigger)
    //     - only if armed AND the suppressor actually fired this call
    //       (native reached the real write-frame, not one of the many
    //       "not yet" frames rstUpdateSeqSkillPowerUp is known to run
    //       before the real write): NOW stage EventParam=target and
    //       redirect SeqInfo.Current=21 into the forget UI. Native has
    //       already run its own presentation/consume tail for this event
    //       by this point.
    //
    // Completion of the forget episode is still observed asynchronously by
    // FullCapacityAddNewBridgeMonitor exactly as before.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
    internal static class FullCapacityAddNewBridgeTrigger
    {
        internal static readonly bool Enabled = true;

        private const int LogicalSkillCapacity = 8;

        private static void Prefix()
        {
            FullCapacityAddNewBridgeArming.Armed = false;
            FullCapacityAddNewBridgeArming.Suppressed = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                // Only ordinary Power-Up success, same convention as
                // AddNewEmptySlotPoc and every other PUpSkillResult-gated
                // patch in this mod. Mutation results are excluded.
                if (gbwk.PUpSkillResult != 1)
                {
                    if (FullCapacityAddNewBridgeState.CompletedLatchActive)
                    {
                        FullCapacityAddNewBridgeState.ResetCompletedLatch();
                    }
                    return;
                }

                if (FullCapacityAddNewBridgeState.Active) return;

                ushort target = gbwk.PUpSkillID;
                if (target == 0) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                int skillCnt = stock.skillcnt;
                if (skillCnt != LogicalSkillCapacity) return; // exactly full, per the guard list

                var ownedArray = stock.skill;
                if (ownedArray == null || ownedArray.Length < LogicalSkillCapacity) return;

                int unit = stock.id;

                if (FullCapacityAddNewBridgeState.IsCompletedForSameEvent(unit, target))
                {
                    return;
                }

                sbyte originalIndex = gbwk.PUpSkillIndex;
                if (originalIndex < 0 || originalIndex >= skillCnt) return;
                int sourceSkillId = ownedArray[originalIndex] & 0xFFFF;
                if (sourceSkillId == 0) return;

                long stockPtr = stock.Pointer.ToInt64();

                // NATIVE DEFAULTSKILL COMPLETION GATE (PoC).
                //
                // POSTCOMPLETE-REENTRY-WATCH runtime evidence (this
                // session) showed the 349-reoffer was never a bookkeeping
                // corruption bug - it was this bridge triggering during a
                // MOMENTARY DefSkillResult==0 reading that was NOT native's
                // own DefaultSkill episode actually finishing, just a
                // transient value passed through mid-cycle. Native's own
                // classification continued running AFTER this bridge's own
                // episode completed, all the way through its own
                // seq8->21->22->8->10->11 cycle, reaching DefSkillResult=0
                // for real only at the end of THAT cycle. This bridge had
                // cut in front of native's own still-in-progress business.
                //
                // This gate delays arming until DefSkillResult==0 AND
                // EventNums==0 (the earliest observed signal of "iterator
                // about to rebuild/still has something") have both held
                // stable across GateStableFrameThreshold CONSECUTIVE calls
                // to this same Prefix (i.e. consecutive seq10 frames for
                // the SAME unit/target) - not a claim that N frames is
                // itself the correct native completion condition (it is
                // NOT - see the class-level investigation notes), only a
                // PoC probe to confirm/refute that deferring arming past
                // native's own transient dip resolves the reoffer at all.
                // If it does, the next step is identifying the actual
                // native completion condition instead of this frame count.
                if (!FullCapacityAddNewGate.Matches(unit, stockPtr, target))
                {
                    FullCapacityAddNewGate.Arm(unit, stockPtr, target);
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] PUP-BRIDGE-GATE-ARM; " +
                        $"frame={UnityEngine.Time.frameCount}; unit={unit}; target={SkillNameResolver.Format(target)}; " +
                        $"defSkillResult={gbwk.DefSkillResult}; eventNums={gbwk.EventNums}; eventOfs={gbwk.EventOfs}; " +
                        $"seqCurrent={gbwk.SeqInfo.Current}; seqLast={gbwk.SeqInfo.Last}.");
                }

                if (!FullCapacityAddNewGate.Opened)
                {
                    bool settled = gbwk.DefSkillResult == 0 && gbwk.EventNums == 0;
                    if (!settled)
                    {
                        if (FullCapacityAddNewGate.StableCount != 0)
                        {
                            MelonLogger.Msg(
                                "[NocturneModernGameplay] PUP-BRIDGE-GATE-RESET; " +
                                $"reason=defSkillResult={gbwk.DefSkillResult}-eventNums={gbwk.EventNums}; " +
                                $"oldStableCount={FullCapacityAddNewGate.StableCount}.");
                        }
                        FullCapacityAddNewGate.StableCount = 0;
                        return;
                    }

                    FullCapacityAddNewGate.StableCount++;
                    if (FullCapacityAddNewGate.StableCount < FullCapacityAddNewGate.GateStableFrameThreshold)
                    {
                        return; // not yet settled long enough - keep waiting, do not arm this frame
                    }

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] PUP-BRIDGE-GATE-OPEN; " +
                        $"stableFrames={FullCapacityAddNewGate.StableCount}; unit={unit}; " +
                        $"target={SkillNameResolver.Format(target)}.");
                    // Stay open from here on for this SAME candidate - do
                    // NOT Clear() here. rstOverWriteSkill fires on only one
                    // of many subsequent Prefix calls, so arming must
                    // continue every frame (like the pre-gate design) until
                    // the Suppressor actually catches that call and the
                    // bridge episode activates (which naturally makes this
                    // candidate stop recurring).
                    FullCapacityAddNewGate.Opened = true;
                }

                // Arm only - do not touch EventParam/SeqInfo, do not skip
                // the native call. rstOverWriteSkill's own Prefix decides
                // per-call whether THIS is the actual overwrite frame.
                FullCapacityAddNewBridgeArming.Armed = true;
                FullCapacityAddNewBridgeArming.Unit = unit;
                FullCapacityAddNewBridgeArming.StockPtr = stockPtr;
                FullCapacityAddNewBridgeArming.SourceIndex = originalIndex;
                FullCapacityAddNewBridgeArming.SourceSkillId = sourceSkillId;
                FullCapacityAddNewBridgeArming.Target = target;
            }
            catch (Exception ex)
            {
                FullCapacityAddNewBridgeArming.Armed = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityAddNewBridgeTrigger prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                if (!FullCapacityAddNewBridgeArming.Armed) return;
                if (!FullCapacityAddNewBridgeArming.Suppressed)
                {
                    // Armed, but this specific call did not reach the real
                    // overwrite (rstUpdateSeqSkillPowerUp is known to run
                    // many times before native's own write-frame arrives -
                    // same pattern AddNewEmptySlotPoc already established).
                    // Nothing to do yet; a later call will re-arm fresh.
                    return;
                }

                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                int unit = FullCapacityAddNewBridgeArming.Unit;
                long stockPtr = FullCapacityAddNewBridgeArming.StockPtr;
                int sourceIndex = FullCapacityAddNewBridgeArming.SourceIndex;
                int sourceSkillId = FullCapacityAddNewBridgeArming.SourceSkillId;
                int target = FullCapacityAddNewBridgeArming.Target;

                ushort originalPending = gbwk.EventParam;
                int seqBefore = gbwk.SeqInfo.Current;
                int seqLastAtBegin = gbwk.SeqInfo.Last;
                sbyte defSkillResultAtBegin = gbwk.DefSkillResult;
                sbyte eventNumsAtBegin = gbwk.EventNums;
                sbyte eventOfsAtBegin = gbwk.EventOfs;
                sbyte flagAfterNativeTail = gbwk.Flag;
                sbyte pUpResultAfterNativeTail = gbwk.PUpSkillResult;
                short levelUpCntAtBegin = gbwk.LevelUpCnt;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] FULLCAP-ADDNEW-BEGIN; " +
                    $"unit={unit}; stockPtr=0x{stockPtr:X}; " +
                    $"sourceIndex={sourceIndex}; source={SkillNameResolver.Format(sourceSkillId)}; " +
                    $"target={SkillNameResolver.Format(target)}; pending32Before={originalPending}; " +
                    $"defSkillResultAtBegin={defSkillResultAtBegin}; " +
                    $"seqBeforeAtBegin={seqBefore}; seqLastAtBegin={seqLastAtBegin}; " +
                    $"eventNumsAtBegin={eventNumsAtBegin}; eventOfsAtBegin={eventOfsAtBegin}; " +
                    $"levelUpCntAtBegin={levelUpCntAtBegin}; " +
                    $"flagAfterNativeTail={flagAfterNativeTail}; pUpResultAfterNativeTail={pUpResultAfterNativeTail}.");

                // Phase 3 revision: do NOT redirect to seq21 in this same
                // Postfix. Runtime testing showed the "you learned X" text
                // still never appeared even with rstOverWriteSkill's own
                // internal presentation running against the real
                // (temporarily written, then restored) value - so the text
                // is not generated by anything in this call chain at all.
                // Next hypothesis: immediately overwriting SeqInfo.Current
                // away from 10 (to 21) cuts off whatever separate,
                // multi-frame UI/message system needs to observe the
                // native seq10->11 progression (Flag=1, PUpSkillResult=-1)
                // to actually render the message. Test this by letting
                // native's OWN dispatcher advance seq away from 10 on its
                // own first (FullCapacityAddNewBridgeMonitor below performs
                // the actual seq21 redirect once that is observed), rather
                // than redirecting from within this same call.
                FullCapacityAddNewBridgePendingRedirect.Arm(
                    unit, stockPtr, target, sourceIndex, sourceSkillId, originalPending, seqBefore);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] FULLCAP-ADDNEW-REDIRECT-DEFERRED; " +
                    $"seqAtDefer={seqBefore}; target={SkillNameResolver.Format(target)}; " +
                    "waiting for native seq to leave 10 on its own before redirecting to seq21.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityAddNewBridgeTrigger postfix failed safely: {ex.Message}");
            }
            finally
            {
                FullCapacityAddNewBridgeArming.Armed = false;
                FullCapacityAddNewBridgeArming.Suppressed = false;
            }
        }
    }

    // Phase 2 revision: rstOverWriteSkill (VA 0x182285FF0) was found, on
    // full disassembly, to NOT be a trivial *pSkill=NewSkillID store. Right
    // after that single store instruction (VA 0x18228601B: `mov [rdi],
    // ebx`), it also runs its OWN internal presentation sub-call
    // (0x182281880, then a PUpSkillIndex-keyed lookup through
    // 0x1815583e0 tail-jumping into the same UnityPlayer.dll string-format
    // thunk found earlier at 0x1816f9470). The prior revision's Prefix
    // returning false skipped ALL of that, not just the store - runtime
    // testing showed the acquisition SOUND came back once seq10's OUTER
    // tail was preserved, but the "you learned X" text still never
    // appeared, consistent with also having skipped this INNER
    // presentation chain.
    //
    // Full disassembly of both rstOverWriteSkill's internal presentation
    // chain AND the outer rstUpdateSeqSkillPowerUp tail's three calls
    // (0x182281A80: species+category message/pose lookup; 0x18227C080:
    // GBWK.MotionReq* staging; 0x182280A50: CONFIRMED this round to write
    // ACTION+0x28=PUpSkillIndex and sourceObj+0xC8=0x1e then dispatch
    // message 0x2B0003 - the SAME glyph/tile-position mechanism this
    // investigation confirmed clean at its very start, unrelated to
    // stock.skill[]/skill names) shows NONE of these five calls read back
    // stock.skill[]/pSkill's value at all - they operate on stock.id,
    // PUpSkillIndex, and GBWK.MotionReq* fields only. So restoring the
    // source value immediately after rstOverWriteSkill's own native body
    // returns cannot corrupt anything downstream, in either function.
    //
    // Given that, this class no longer suppresses the write at all - it
    // lets rstOverWriteSkill run completely natively (including its own
    // internal presentation sub-call, using the real, now-overwritten
    // value, exactly as an ordinary Power-Up would), then restores the
    // original source skill ID via the SAME `ref int pSkill` parameter in
    // Postfix - the framework-provided by-ref slot, not a hand-computed
    // raw offset. This is deliberately "let native write, then put it
    // back" (the approach previously flagged as last-resort) rather than
    // an instruction-level store suppression, because the concrete
    // disassembly evidence above shows nothing observes the write in
    // between, making the simpler restore-after approach equally safe.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstOverWriteSkill))]
    internal static class FullCapacityOverwriteSuppressor
    {
        private static bool _restorePending;

        private static void Prefix(ref int pSkill, ushort NewSkillID)
        {
            _restorePending = false;
            if (!FullCapacityAddNewBridgeTrigger.Enabled) return;
            try
            {
                if (!FullCapacityAddNewBridgeArming.Armed) return;
                if (NewSkillID != FullCapacityAddNewBridgeArming.Target) return;
                if ((pSkill & 0xFFFF) != FullCapacityAddNewBridgeArming.SourceSkillId) return;

                // Let native write target into the slot and run its own
                // internal presentation using that real value - only mark
                // that a restore is owed once it returns.
                _restorePending = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityOverwriteSuppressor prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix(ref int pSkill, ushort NewSkillID)
        {
            if (!_restorePending) return;
            _restorePending = false;
            try
            {
                // Confirm native actually wrote what we expect before
                // touching anything - defensive, in case some other patch
                // or native branch changed the outcome.
                if ((pSkill & 0xFFFF) == FullCapacityAddNewBridgeArming.Target)
                {
                    pSkill = FullCapacityAddNewBridgeArming.SourceSkillId;
                }
                FullCapacityAddNewBridgeArming.Suppressed = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityOverwriteSuppressor postfix failed safely: {ex.Message}");
            }
        }
    }

    // Shared arming state between the trigger (rstUpdateSeqSkillPowerUp)
    // and the suppressor (rstOverWriteSkill), both of which fire within
    // the same native call chain on the same thread. Separate from
    // FullCapacityAddNewBridgeState, which tracks the (much longer-lived)
    // in-flight forget episode.
    internal static class FullCapacityAddNewBridgeArming
    {
        internal static bool Armed;
        internal static bool Suppressed;
        internal static int Unit;
        internal static long StockPtr;
        internal static int SourceIndex;
        internal static int SourceSkillId;
        internal static int Target;
    }

    // NATIVE DEFAULTSKILL COMPLETION GATE (PoC) - tracks how many
    // consecutive rstUpdateSeqSkillPowerUp Prefix calls have observed a
    // "settled" native state (DefSkillResult==0 && EventNums==0) for the
    // SAME candidate (unit/stock/target), before FullCapacityAddNewBridgeTrigger
    // is allowed to actually arm. This is explicitly a PoC probe, not a
    // production completion condition - see FullCapacityAddNewBridgeTrigger's
    // class-level comment on the gate for why a fixed frame count is not
    // being treated as the final answer.
    internal static class FullCapacityAddNewGate
    {
        internal const int GateStableFrameThreshold = 5;

        private static bool _pending;
        private static int _unit;
        private static long _stockPtr;
        private static int _target;

        internal static int StableCount;

        // Once true for the current candidate, the stable-frame check is
        // bypassed entirely and every subsequent Prefix call re-arms
        // FullCapacityAddNewBridgeArming unconditionally (matching the
        // ORIGINAL, pre-gate design) - this is essential: rstOverWriteSkill
        // is known to fire on only ONE of many rstUpdateSeqSkillPowerUp
        // calls, so the gate must stay "open" across all of them once
        // native's own DefaultSkill episode has settled, not re-close and
        // force another multi-frame wait after the very first opened
        // frame. An earlier revision called Clear() immediately on
        // opening, which reset _pending every frame and meant Arming was
        // only ever true on a single frame out of every
        // GateStableFrameThreshold - almost always missing the real write
        // frame and letting native's un-suppressed ordinary overwrite run
        // instead (observed: "スキルパワーアップが上書きになりました").
        internal static bool Opened;

        internal static bool Matches(int unit, long stockPtr, int target) =>
            _pending && _unit == unit && _stockPtr == stockPtr && _target == target;

        internal static void Arm(int unit, long stockPtr, int target)
        {
            _pending = true;
            _unit = unit;
            _stockPtr = stockPtr;
            _target = target;
            StableCount = 0;
            Opened = false;
        }

        internal static void Clear()
        {
            _pending = false;
            StableCount = 0;
            Opened = false;
        }
    }

    // Holds a bridge event whose ownership-side work is already done
    // (native ran seq10's full tail against the real, temporarily-written
    // target value) but whose seq21 redirect is deliberately deferred by
    // one or more frames - see the class-level comment on
    // FullCapacityAddNewBridgeTrigger.Postfix for why. Consumed by
    // FullCapacityAddNewBridgeMonitor once native's own dispatcher has
    // moved SeqInfo.Current away from 10 on its own.
    internal static class FullCapacityAddNewBridgePendingRedirect
    {
        internal static bool Pending;
        internal static int Unit;
        internal static long StockPtr;
        internal static int Target;
        internal static int SourceIndex;
        internal static int SourceSkillId;
        internal static int OriginalPending;
        internal static int SeqAtDefer;

        internal static void Arm(
            int unit, long stockPtr, int target, int sourceIndex, int sourceSkillId,
            int originalPending, int seqAtDefer)
        {
            Pending = true;
            Unit = unit;
            StockPtr = stockPtr;
            Target = target;
            SourceIndex = sourceIndex;
            SourceSkillId = sourceSkillId;
            OriginalPending = originalPending;
            SeqAtDefer = seqAtDefer;
        }

        internal static void Clear() => Pending = false;
    }

    // Watches for the bridge episode's outcome across the many subsequent
    // frames of player interaction the native forget UI takes. Read-only
    // beyond the two Prefix writes above - never writes skill[]/skillcnt/
    // EventParam itself.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class FullCapacityAddNewBridgeMonitor
    {
        // POST-BRIDGE DEFAULTSKILL REENTRY CONTROL-FLOW INVESTIGATION.
        // Narrow, self-expiring trace window - NOT a permanent high-
        // frequency observer. Armed only once per FULLCAP-ADDNEW-COMPLETE/
        // CANCEL, for a fixed number of subsequent rstUpdate calls, tracking
        // only the SAME unit this bridge just finished with. Logs on CHANGE
        // only (matching this mod's established discipline), so a quiet
        // window produces zero extra log lines. Purpose: catch the exact
        // frame/field-state where native re-enters DefaultSkill
        // classification for this unit after the bridge episode already
        // completed (Test B's 349 reoffer) versus where it does not (Test
        // A) - without adding any new always-on hook.
        private const int PostCompleteWatchFrames = 400;

        private static bool _postCompleteArmed;
        private static int _postCompleteUnit;
        private static long _postCompleteStockPtr;
        private static int _postCompleteFramesRemaining;
        private static bool _postCompleteHasLast;
        private static int _lastSeq;
        private static sbyte _lastFlag;
        private static sbyte _lastDefSkillResult;
        private static sbyte _lastEventNums;
        private static sbyte _lastEventOfs;
        private static ushort _lastEventParam;
        private static short _lastLevelUpCnt;

        private static void ArmPostCompleteWatch(int unit, long stockPtr)
        {
            _postCompleteArmed = true;
            _postCompleteUnit = unit;
            _postCompleteStockPtr = stockPtr;
            _postCompleteFramesRemaining = PostCompleteWatchFrames;
            _postCompleteHasLast = false;
        }

        private static void RunPostCompleteWatch()
        {
            if (!_postCompleteArmed) return;
            try
            {
                if (_postCompleteFramesRemaining-- <= 0)
                {
                    _postCompleteArmed = false;
                    return;
                }

                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                if (stock.id != _postCompleteUnit || stock.Pointer.ToInt64() != _postCompleteStockPtr)
                {
                    // Native moved on to a different unit entirely - nothing
                    // more to watch for THIS episode's reentry question.
                    _postCompleteArmed = false;
                    return;
                }

                int seq = gbwk.SeqInfo.Current;
                sbyte flag = gbwk.Flag;
                sbyte defSkillResult = gbwk.DefSkillResult;
                sbyte eventNums = gbwk.EventNums;
                sbyte eventOfs = gbwk.EventOfs;
                ushort eventParam = gbwk.EventParam;
                short levelUpCnt = gbwk.LevelUpCnt;

                if (!_postCompleteHasLast)
                {
                    _postCompleteHasLast = true;
                    _lastSeq = seq; _lastFlag = flag; _lastDefSkillResult = defSkillResult;
                    _lastEventNums = eventNums; _lastEventOfs = eventOfs; _lastEventParam = eventParam;
                    _lastLevelUpCnt = levelUpCnt;
                    return;
                }

                bool changed = seq != _lastSeq || flag != _lastFlag || defSkillResult != _lastDefSkillResult ||
                               eventNums != _lastEventNums || eventOfs != _lastEventOfs ||
                               eventParam != _lastEventParam || levelUpCnt != _lastLevelUpCnt;
                if (changed)
                {
                    int frame = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] POSTCOMPLETE-REENTRY-WATCH; " +
                        $"frame={frame}; unit={_postCompleteUnit}; framesLeft={_postCompleteFramesRemaining}; " +
                        $"seq {_lastSeq}->{seq}; flag {_lastFlag}->{flag}; " +
                        $"defSkillResult {_lastDefSkillResult}->{defSkillResult}; " +
                        $"eventNums {_lastEventNums}->{eventNums}; eventOfs {_lastEventOfs}->{eventOfs}; " +
                        $"eventParam {_lastEventParam}->{eventParam}; levelUpCnt {_lastLevelUpCnt}->{levelUpCnt}.");

                    _lastSeq = seq; _lastFlag = flag; _lastDefSkillResult = defSkillResult;
                    _lastEventNums = eventNums; _lastEventOfs = eventOfs; _lastEventParam = eventParam;
                    _lastLevelUpCnt = levelUpCnt;
                }
            }
            catch (Exception ex)
            {
                _postCompleteArmed = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityAddNewBridgeMonitor post-complete watch failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!FullCapacityAddNewBridgeTrigger.Enabled) return;

            TryPerformDeferredRedirect();
            RunPostCompleteWatch();

            if (!FullCapacityAddNewBridgeState.Active) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                int seq = gbwk.SeqInfo.Current;
                if (seq == 21 || seq == 22) return; // still in progress

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero ||
                    stock.Pointer.ToInt64() != FullCapacityAddNewBridgeState.WatchedStockPtr)
                {
                    // pCurrentStock no longer points at the unit this
                    // bridge started for - cannot reliably inspect its
                    // skill[] via the wrapper. Report what is known and
                    // stop watching rather than guess.
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] FULLCAP-ADDNEW-LOST-TRACKING; " +
                        $"unit={FullCapacityAddNewBridgeState.WatchedUnit}; " +
                        "reason=pCurrentStock-changed-before-outcome-observed; " +
                        $"seq={seq}.");
                    FullCapacityAddNewBridgeState.CompleteAndLatch();
                    ArmPostCompleteWatch(FullCapacityAddNewBridgeState.WatchedUnit, FullCapacityAddNewBridgeState.WatchedStockPtr);
                    return;
                }

                var ownedArray = stock.skill;
                int len = ownedArray?.Length ?? 0;
                var skills = new int[len];
                for (int i = 0; i < len; i++) skills[i] = ownedArray![i] & 0xFFFF;
                int skillCnt = stock.skillcnt;

                bool targetPresent = false;
                bool sourcePreserved = false;
                for (int i = 0; i < skills.Length; i++)
                {
                    if (skills[i] == FullCapacityAddNewBridgeState.Target) targetPresent = true;
                    if (skills[i] == FullCapacityAddNewBridgeState.SourceSkillIdAtTrigger) sourcePreserved = true;
                }

                sbyte eventNumsAtEnd = gbwk.EventNums;
                sbyte eventOfsAtEnd = gbwk.EventOfs;
                sbyte defSkillResultAtEnd = gbwk.DefSkillResult;
                short levelUpCntAtEnd = gbwk.LevelUpCnt;

                if (targetPresent)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] FULLCAP-ADDNEW-COMPLETE; " +
                        $"unit={FullCapacityAddNewBridgeState.WatchedUnit}; " +
                        $"finalSkills=[{string.Join(",", skills)}]; sourcePreserved={sourcePreserved}; " +
                        $"targetPresent={targetPresent}; skillcnt={skillCnt}; seq={seq}; " +
                        $"pending32After={gbwk.EventParam}; eventNumsAtEnd={eventNumsAtEnd}; " +
                        $"eventOfsAtEnd={eventOfsAtEnd}; defSkillResultAtEnd={defSkillResultAtEnd}; " +
                        $"levelUpCntAtEnd={levelUpCntAtEnd}.");
                }
                else
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] FULLCAP-ADDNEW-CANCEL; " +
                        $"unit={FullCapacityAddNewBridgeState.WatchedUnit}; " +
                        "reason=target-not-present-after-forget-flow-exit; " +
                        $"finalSkills=[{string.Join(",", skills)}]; skillcnt={skillCnt}; seq={seq}; " +
                        $"pending32After={gbwk.EventParam}; eventNumsAtEnd={eventNumsAtEnd}; " +
                        $"eventOfsAtEnd={eventOfsAtEnd}; defSkillResultAtEnd={defSkillResultAtEnd}; " +
                        $"levelUpCntAtEnd={levelUpCntAtEnd}.");
                }

                FullCapacityAddNewBridgeState.CompleteAndLatch();
                ArmPostCompleteWatch(FullCapacityAddNewBridgeState.WatchedUnit, FullCapacityAddNewBridgeState.WatchedStockPtr);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityAddNewBridgeMonitor postfix failed safely: {ex.Message}");
            }
        }

        // Consumes FullCapacityAddNewBridgePendingRedirect once native's
        // OWN dispatcher has moved SeqInfo.Current away from the seq it
        // was at when the bridge deferred (10), letting at least one
        // native-driven frame of normal seq10->(whatever native goes to
        // next, e.g. 11) progression happen first - see the comment on
        // FullCapacityAddNewBridgeTrigger.Postfix for why this is being
        // tried. Never redirects on the very same frame the event was
        // armed (rstUpdate's own Postfix runs after
        // rstUpdateSeqSkillPowerUp within the same frame, so seq is still
        // 10 at that point regardless).
        private static void TryPerformDeferredRedirect()
        {
            if (!FullCapacityAddNewBridgePendingRedirect.Pending) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                int seqNow = gbwk.SeqInfo.Current;
                if (seqNow == FullCapacityAddNewBridgePendingRedirect.SeqAtDefer) return; // still hasn't moved on its own

                int target = FullCapacityAddNewBridgePendingRedirect.Target;
                int unit = FullCapacityAddNewBridgePendingRedirect.Unit;
                long stockPtr = FullCapacityAddNewBridgePendingRedirect.StockPtr;
                int sourceIndex = FullCapacityAddNewBridgePendingRedirect.SourceIndex;
                int sourceSkillId = FullCapacityAddNewBridgePendingRedirect.SourceSkillId;
                int originalPending = FullCapacityAddNewBridgePendingRedirect.OriginalPending;

                FullCapacityAddNewBridgePendingRedirect.Clear();

                // BOUNDED EVENTPARAM UI WINDOW (revision after Candidate C2
                // runtime testing).
                //
                // Candidate C2 left EventParam untouched throughout seq21/22
                // on the theory that neither seq21 (rstUpdateSeqDestroySkill,
                // full 357-instruction disassembly) nor seq22
                // (rstUpdateSeqDestroyConfirm, full disassembly) reads
                // GBWK.EventParam(+0x32) anywhere - which is still true for
                // THOSE two functions' own bodies. But real-machine testing
                // found the borrowed forget UI's separate "hidden pending
                // skill" slot (a 3rd, always-present column showing
                // whatever curriculum skill is still pending to be taught
                // this level-up - a CONFIRMED native mechanism, not a bug)
                // reads EventParam too, from some OTHER function not yet
                // identified - and leaving native's own stale value (e.g.
                // 349, a curriculum skill from earlier this same level-up)
                // sitting there for the whole UI let the player select THAT
                // phantom slot instead of one of their real 8 skills,
                // producing a FULLCAP-ADDNEW-CANCEL (no real slot freed,
                // Power-Up target never inserted).
                //
                // Fix: stage EventParam=target for the DURATION of this
                // borrowed UI (seq21 through seq22), so the hidden slot
                // shows the Power-Up target instead of a confusing,
                // unrelated stale value - then restore the TRUE native
                // original (captured back in FullCapacityAddNewBridgeTrigger.
                // Postfix, before this UI ever started, carried here via
                // FullCapacityAddNewBridgePendingRedirect.OriginalPending ->
                // FullCapacityAddNewBridgeState.OriginalPending32) at the
                // exact rstAddSkill insertion boundary in
                // FullCapacityAddNewBridgeInsertionInjector.Postfix - BEFORE
                // control ever returns to DefaultSkill's own reclassification.
                // This never lets DefaultSkill's OWN classification observe
                // target sitting in EventParam (avoiding Candidate C2's
                // original bookkeeping-pollution failure mode), while also
                // never exposing native's own stale pending value to the
                // player during this bridge's own UI (avoiding the phantom-
                // slot CANCEL failure mode just found).
                // SEQ TRANSITION FIELD PARITY CHECK (read-only, step 1):
                // full disassembly of rstUpdateSeqDefaultSkill's own
                // forget-needed branch (VA 0x182288A1E-0x182288AE8) shows
                // native ALWAYS writes SeqInfo.Change=1 (VA 0x182288A10, the
                // shared tail both branches funnel into) in the SAME
                // transition that writes SeqInfo.Current=21 (VA
                // 0x182288AE4) - plus three auxiliary calls (message
                // dispatch code=7, two GBWK+0x88-chain object inits) this
                // bridge does not perform. Capture Next/Last/Change here,
                // before and after our OWN Current=21 write, to confirm
                // whether Change is left at something other than native's
                // "1" - NOT yet writing Change ourselves.
                sbyte nextBefore = gbwk.SeqInfo.Next;
                sbyte lastBefore = gbwk.SeqInfo.Last;
                sbyte changeBefore = gbwk.SeqInfo.Change;

                gbwk.EventParam = (ushort)target;
                gbwk.SeqInfo.Current = 21;
                int seqAfter = gbwk.SeqInfo.Current;
                sbyte nextAfter = gbwk.SeqInfo.Next;
                sbyte lastAfter = gbwk.SeqInfo.Last;
                sbyte changeAfter = gbwk.SeqInfo.Change;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] FULLCAP-ADDNEW-ENTER-FORGET; " +
                    $"seqBefore={seqNow}; seqAfter={seqAfter}; target={SkillNameResolver.Format(target)}; " +
                    $"eventParamStagedForUi={gbwk.EventParam}; originalPendingToRestoreLater={originalPending}; " +
                    $"next {nextBefore}->{nextAfter}; last {lastBefore}->{lastAfter}; " +
                    $"change {changeBefore}->{changeAfter}; " +
                    $"deferredFramesObserved=true.");

                FullCapacityAddNewBridgeState.Activate(
                    unit, stockPtr, target, sourceIndex, sourceSkillId, originalPending);
            }
            catch (Exception ex)
            {
                FullCapacityAddNewBridgePendingRedirect.Clear();
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityAddNewBridgeMonitor deferred redirect failed safely: {ex.Message}");
            }
        }
    }

    // BOUNDED EVENTPARAM UI WINDOW - insertion boundary.
    //
    // rstupdate.rstAddSkill() (VA 0x182285A40, exact ISIL-name match) is the
    // ONLY confirmed reader of GBWK.EventParam in the entire forget+insert
    // path (VA 0x182285A92: movzx ebx, word ptr [rax+0x32], feeding the
    // generic insertion helper via CX). It has exactly two static callers in
    // the whole binary: VA 0x1822889E4 (rstUpdateSeqDefaultSkill's own
    // "room available, insert directly" branch - runs with SeqInfo.Current
    // still 8) and VA 0x182288F69 (rstUpdateSeqDestroyConfirm's
    // delete-confirmed branch, immediately after the skill[]-compaction
    // write at VA 0x182288F66 - runs with SeqInfo.Current still 22, since
    // that branch does not itself write SeqInfo.Current). Gating on
    // SeqInfo.Current==22 therefore cleanly separates "this bridge's own
    // borrowed forget episode is about to insert" from every other call to
    // rstAddSkill (native's own ordinary room-available grants, or another
    // unit's forget episode happening to run rstAddSkill this same frame).
    //
    // By this point EventParam already equals target (staged all the way
    // back in FullCapacityAddNewBridgeMonitor.TryPerformDeferredRedirect,
    // for the borrowed UI's own "hidden pending skill" display - see that
    // method's comment for why). This class's job is no longer to inject
    // target here (that already happened) - it is to restore native's TRUE
    // original pending value (FullCapacityAddNewBridgeState.OriginalPending32,
    // captured back in FullCapacityAddNewBridgeTrigger.Postfix before this
    // whole UI ever started) at the exact moment insertion completes -
    // BEFORE control ever returns to DefaultSkill's own reclassification.
    // This is the boundary that keeps target from ever being visible to
    // DefaultSkill's own classification pass (avoiding Candidate C2's
    // original bookkeeping-pollution failure mode), while the UI itself saw
    // target the whole time it was open (avoiding the phantom-slot CANCEL
    // failure mode found when EventParam was left untouched throughout).
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstAddSkill))]
    internal static class FullCapacityAddNewBridgeInsertionInjector
    {
        internal static readonly bool Enabled = true;

        private const int ExpectedPostCompactionSkillCnt = 7;

        private static bool _injected;
        private static ushort _eventParamBeforeCall;
        private static int _nativeOriginalToRestore;

        private static void Prefix()
        {
            _injected = false;
            if (!Enabled) return;
            try
            {
                if (!FullCapacityAddNewBridgeState.Active) return;

                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                if (gbwk.SeqInfo.Current != 22) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;
                if (stock.Pointer.ToInt64() != FullCapacityAddNewBridgeState.WatchedStockPtr) return;
                if (stock.id != FullCapacityAddNewBridgeState.WatchedUnit) return;
                if (stock.skillcnt != ExpectedPostCompactionSkillCnt) return;

                int target = FullCapacityAddNewBridgeState.Target;
                if (target == 0) return;

                _eventParamBeforeCall = gbwk.EventParam;
                _nativeOriginalToRestore = FullCapacityAddNewBridgeState.OriginalPending32;
                // Idempotent safety net - should already equal target
                // (staged at ENTER-FORGET), but ensure it regardless in
                // case some other path touched it in between.
                gbwk.EventParam = (ushort)target;
                _injected = true;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] C2-INJECT; " +
                    $"unit={stock.id}; eventParamBeforeCall={_eventParamBeforeCall}; " +
                    $"target={SkillNameResolver.Format(target)}; skillcnt={stock.skillcnt}; " +
                    $"seqCurrent={gbwk.SeqInfo.Current}; seqLast={gbwk.SeqInfo.Last}; " +
                    $"nativeOriginalToRestore={_nativeOriginalToRestore}.");
            }
            catch (Exception ex)
            {
                _injected = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityAddNewBridgeInsertionInjector prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_injected) return;
            _injected = false;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                ushort eventParamAfterNative = gbwk.EventParam;

                // Restore native's TRUE original pending value (NOT
                // whatever EventParam held entering this call, which was
                // already target per the bounded UI window design) -
                // this is the exact boundary before DefaultSkill's own
                // reclassification can next run.
                gbwk.EventParam = (ushort)_nativeOriginalToRestore;

                var stock = gbwk.pCurrentStock;
                int target = FullCapacityAddNewBridgeState.Target;
                bool targetPresent = false;
                if (stock != null && stock.Pointer != IntPtr.Zero)
                {
                    var ownedArray = stock.skill;
                    if (ownedArray != null)
                    {
                        for (int i = 0; i < ownedArray.Length; i++)
                        {
                            if ((ownedArray[i] & 0xFFFF) == target) { targetPresent = true; break; }
                        }
                    }
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] C2-RESTORE; " +
                    $"target={SkillNameResolver.Format(target)}; " +
                    $"eventParamAfterNative={eventParamAfterNative}; " +
                    $"restoredToNativeOriginal={_nativeOriginalToRestore}; " +
                    $"targetPresent={targetPresent}; skillcnt={stock?.skillcnt.ToString() ?? "?"}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FullCapacityAddNewBridgeInsertionInjector postfix failed safely: {ex.Message}");
            }
        }
    }

    internal static class FullCapacityAddNewBridgeState
    {
        internal static bool Active;
        internal static int WatchedUnit;
        internal static long WatchedStockPtr;
        internal static int Target;
        internal static int SourceIndexAtTrigger;
        internal static int SourceSkillIdAtTrigger;
        internal static int OriginalPending32;

        private static bool _completedLatch;
        private static int _completedUnit;
        private static int _completedTarget;

        internal static bool CompletedLatchActive => _completedLatch;

        internal static void ResetCompletedLatch() => _completedLatch = false;

        internal static bool IsCompletedForSameEvent(int unit, int target) =>
            _completedLatch && unit == _completedUnit && target == _completedTarget;

        internal static void Activate(
            int unit, long stockPtr, int target, int sourceIndex, int sourceSkillId, int originalPending)
        {
            Active = true;
            WatchedUnit = unit;
            WatchedStockPtr = stockPtr;
            Target = target;
            SourceIndexAtTrigger = sourceIndex;
            SourceSkillIdAtTrigger = sourceSkillId;
            OriginalPending32 = originalPending;
        }

        internal static void CompleteAndLatch()
        {
            _completedLatch = true;
            _completedUnit = WatchedUnit;
            _completedTarget = Target;
            Active = false;
        }
    }
}
