using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Mutation AddNew - FULL-CAPACITY BRIDGE PoC (2026-09-15, User-approved
    // design candidate B).
    //
    // Zero-base reconstruction of the SAME native pattern already proven in
    // production for ordinary Skill Power-Up
    // (FullCapacityAddNewBridgePoc.cs), reusing its hard-won lessons
    // directly rather than rediscovering them:
    //   - rstUpdateSeqSkillPowerUp's native body is NEVER skipped. Skipping
    //     it (an earlier Power-Up revision's approach) lost the acquisition
    //     sound, left GBWK.EventParam stale, and caused extra seq8/9/10
    //     loops - all three symptoms traced to one root cause (see that
    //     file's own history comment).
    //   - The overwrite is suppressed via a SEPARATE Harmony patch on
    //     rstupdate.rstOverWriteSkill itself: let native write the target
    //     into the slot and run its own internal presentation against the
    //     real value, then restore the original value via the `ref int
    //     pSkill` parameter - not an instruction-level store suppression.
    //   - GBWK.Flag / GBWK.PUpSkillResult are NEVER written by this bridge.
    //     Native's own rstUpdateSeqSkillPowerUp tail sets Flag=2 (mutation,
    //     CONFIRMED this session, investigations/ACQUISITION_LEARNASNEW/
    //     PLAN.md) and PUpSkillResult=0xFF unconditionally, BEFORE this
    //     bridge's own seq21 redirect ever runs (the redirect is deferred
    //     until native's dispatcher has already moved SeqInfo.Current away
    //     from 10 on its own) - so both fields are already native-final by
    //     the time this bridge acts.
    //   - GBWK.DefSkillResult is NEVER written by this bridge either.
    //     rstupdate.rstUpdateSeqDestroyConfirm (seq22, CONFIRMED this
    //     session to directly call rstupdate.rstAddSkill after its own
    //     skill[]-compaction + skillcnt-- - VA 0x182288F69) does not read
    //     DefSkillResult at all; only rstUpdateSeqDefaultSkill's own seq8
    //     branch decision consults it, and this bridge's redirect goes
    //     straight into seq21/22 without ever re-entering seq8's own
    //     decision point for this episode.
    //   - GBWK.EventParam is staged with target ONLY for the bounded
    //     duration of the borrowed seq21/22 UI (native's OWN "hidden 9th
    //     slot" pending-display mechanism reads it - CONFIRMED via the
    //     Power-Up implementation's own runtime testing), then restored to
    //     native's true original value - on BOTH the completion path (at
    //     the exact rstAddSkill/seq22 insertion boundary, before
    //     DefaultSkill's own reclassification can observe it) AND the
    //     cancellation path (a fix over the reference implementation - see
    //     below).
    //
    // KNOWN GAP IN THE REFERENCE IMPLEMENTATION, FIXED HERE (2026-09-15,
    // User-directed): a full source grep of FullCapacityAddNewBridgePoc.cs
    // found exactly three `gbwk.EventParam =` writes (stage-on-enter,
    // idempotent re-stage in the insertion injector's Prefix, restore in
    // its Postfix) - all three reachable only via the COMPLETION path
    // (rstAddSkill actually firing at the seq22 boundary). The CANCELLATION
    // branch in FullCapacityAddNewBridgeMonitor.Postfix (the
    // FULLCAP-ADDNEW-CANCEL log site) has NO EventParam restore at all -
    // if the player backs out of the forget UI without confirming,
    // rstAddSkill never fires, so EventParam is left stuck at target until
    // native's own next Calc-phase cycle happens to overwrite it. This
    // class fixes that for Mutation by restoring EventParam explicitly on
    // BOTH outcomes in MutationFullCapacityAddNewBridgeMonitor.Postfix.
    // Power-Up's own implementation is left unchanged (READY FOR
    // PRODUCTION, not to be touched without separate review).
    //
    // MUTUAL EXCLUSION WITH ORDINARY POWER-UP (User-directed, 2026-09-15):
    // GBWK.PUpSkillResult can only ever hold one value at a time (Core's
    // single return value), so ordinary (==1) and mutation (==2) can never
    // be observed simultaneously in one read. The real risk is across
    // FRAMES: if Core re-evaluates while THIS bridge's episode is still
    // in flight (SeqInfo.Current in 21/22, MutationAddNewBridgeState.Active
    // still true), a later frame producing PUpSkillResult==1 must not let
    // the ordinary bridge (FullCapacityAddNewBridgeTrigger / AddNewEmptySlotPoc)
    // arm on top of it, and vice versa. Both sides now check the other's
    // Active flag before arming (minimal additive guard, Power-Up's own
    // decision logic otherwise untouched - see that file's own comment for
    // the added line).
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
    internal static class MutationFullCapacityAddNewBridgeTrigger
    {
        internal static readonly bool Enabled = true;

        private const int LogicalSkillCapacity = 8;

        private static void Prefix()
        {
            MutationAddNewBridgeArming.Armed = false;
            MutationAddNewBridgeArming.Suppressed = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                // Mutation success only (PUpSkillResult==2). Ordinary
                // Power-Up (==1) is FullCapacityAddNewBridgeTrigger's own
                // domain, untouched here.
                if (gbwk.PUpSkillResult != 2)
                {
                    if (MutationAddNewBridgeState.CompletedLatchActive)
                    {
                        MutationAddNewBridgeState.ResetCompletedLatch();
                    }
                    return;
                }

                if (MutationAddNewBridgeState.Active) return;

                // Mutual exclusion: do not arm while an ordinary Power-Up
                // AddNew episode (full-capacity or empty-slot) is in
                // flight for any unit.
                if (FullCapacityAddNewBridgeState.Active) return;

                ushort target = gbwk.PUpSkillID;
                if (target == 0) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                int skillCnt = stock.skillcnt;
                if (skillCnt != LogicalSkillCapacity) return; // exactly full

                var ownedArray = stock.skill;
                if (ownedArray == null || ownedArray.Length < LogicalSkillCapacity) return;

                int unit = stock.id;

                if (MutationAddNewBridgeState.IsCompletedForSameEvent(unit, target))
                {
                    return;
                }

                // Mutation's own native lifecycle (CONFIRMED this session,
                // investigations/ACQUISITION_LEARNASNEW/PLAN.md, POWERUP_
                // MUTATION_CFG section): PUpSkillIndex is fixed BEFORE the
                // ordinary/mutation branch inside rstCalcSkillPowerUpCore
                // and never re-written on the mutation side - it always
                // points at an existing owned skill's slot (the "ordinary
                // candidate" Mutation piggybacks on), never an empty slot.
                sbyte originalIndex = gbwk.PUpSkillIndex;
                if (originalIndex < 0 || originalIndex >= skillCnt) return;
                int sourceSkillId = ownedArray[originalIndex] & 0xFFFF;
                if (sourceSkillId == 0) return;

                long stockPtr = stock.Pointer.ToInt64();

                // Same DefaultSkill-settledness gate as the ordinary
                // bridge (FullCapacityAddNewGate) - guards against arming
                // while native's own DefaultSkill classification is
                // mid-cycle (a transient DefSkillResult/EventNums dip that
                // is not really "idle"). Kept as a SEPARATE gate instance
                // (not shared with Power-Up's own) per candidate B's state
                // isolation - both watch the same underlying GBWK fields,
                // but track stability independently per bridge.
                if (!MutationFullCapacityAddNewGate.Matches(unit, stockPtr, target))
                {
                    MutationFullCapacityAddNewGate.Arm(unit, stockPtr, target);
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTCAP-ADDNEW-GATE-ARM; " +
                        $"frame={UnityEngine.Time.frameCount}; unit={unit}; target={SkillNameResolver.Format(target)}; " +
                        $"source={SkillNameResolver.Format(sourceSkillId)}; " +
                        $"defSkillResult={gbwk.DefSkillResult}; eventNums={gbwk.EventNums}; " +
                        $"seqCurrent={gbwk.SeqInfo.Current}; seqLast={gbwk.SeqInfo.Last}.");
                }

                if (!MutationFullCapacityAddNewGate.Opened)
                {
                    bool settled = gbwk.DefSkillResult == 0 && gbwk.EventNums == 0;
                    if (!settled)
                    {
                        if (MutationFullCapacityAddNewGate.StableCount != 0)
                        {
                            MelonLogger.Msg(
                                "[NocturneModernGameplay] MUTCAP-ADDNEW-GATE-RESET; " +
                                $"reason=defSkillResult={gbwk.DefSkillResult}-eventNums={gbwk.EventNums}; " +
                                $"oldStableCount={MutationFullCapacityAddNewGate.StableCount}.");
                        }
                        MutationFullCapacityAddNewGate.StableCount = 0;
                        return;
                    }

                    MutationFullCapacityAddNewGate.StableCount++;
                    if (MutationFullCapacityAddNewGate.StableCount < MutationFullCapacityAddNewGate.GateStableFrameThreshold)
                    {
                        return;
                    }

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTCAP-ADDNEW-GATE-OPEN; " +
                        $"stableFrames={MutationFullCapacityAddNewGate.StableCount}; unit={unit}; " +
                        $"target={SkillNameResolver.Format(target)}.");
                    MutationFullCapacityAddNewGate.Opened = true;
                }

                MutationAddNewBridgeArming.Armed = true;
                MutationAddNewBridgeArming.Unit = unit;
                MutationAddNewBridgeArming.StockPtr = stockPtr;
                MutationAddNewBridgeArming.SourceIndex = originalIndex;
                MutationAddNewBridgeArming.SourceSkillId = sourceSkillId;
                MutationAddNewBridgeArming.Target = target;
            }
            catch (Exception ex)
            {
                MutationAddNewBridgeArming.Armed = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityAddNewBridgeTrigger prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                if (!MutationAddNewBridgeArming.Armed) return;
                if (!MutationAddNewBridgeArming.Suppressed)
                {
                    // Armed, but this specific call did not reach the real
                    // overwrite - rstUpdateSeqSkillPowerUp is known to run
                    // many times before native's own write-frame arrives.
                    return;
                }

                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                int unit = MutationAddNewBridgeArming.Unit;
                long stockPtr = MutationAddNewBridgeArming.StockPtr;
                int sourceIndex = MutationAddNewBridgeArming.SourceIndex;
                int sourceSkillId = MutationAddNewBridgeArming.SourceSkillId;
                int target = MutationAddNewBridgeArming.Target;

                ushort originalPending = gbwk.EventParam;
                int seqBefore = gbwk.SeqInfo.Current;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MUTCAP-ADDNEW-BEGIN; " +
                    $"unit={unit}; stockPtr=0x{stockPtr:X}; " +
                    $"sourceIndex={sourceIndex}; source={SkillNameResolver.Format(sourceSkillId)}; " +
                    $"target={SkillNameResolver.Format(target)}; pending32Before={originalPending}; " +
                    $"seqBeforeAtBegin={seqBefore}; " +
                    $"flagAfterNativeTail={gbwk.Flag}; pUpResultAfterNativeTail={gbwk.PUpSkillResult}.");

                // Same "defer the redirect until native's own dispatcher
                // leaves seq10 on its own" design as the ordinary bridge -
                // see FullCapacityAddNewBridgePoc.cs's own comment for why
                // (redirecting immediately, in the same Postfix, was found
                // to cut off native's own multi-frame acquisition
                // message/sound system on the Power-Up side).
                MutationFullCapacityAddNewBridgePendingRedirect.Arm(
                    unit, stockPtr, target, sourceIndex, sourceSkillId, originalPending, seqBefore);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MUTCAP-ADDNEW-REDIRECT-DEFERRED; " +
                    $"seqAtDefer={seqBefore}; target={SkillNameResolver.Format(target)}; " +
                    "waiting for native seq to leave 10 on its own before redirecting to seq21.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityAddNewBridgeTrigger postfix failed safely: {ex.Message}");
            }
            finally
            {
                MutationAddNewBridgeArming.Armed = false;
                MutationAddNewBridgeArming.Suppressed = false;
            }
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstOverWriteSkill))]
    internal static class MutationFullCapacityOverwriteSuppressor
    {
        private static bool _restorePending;

        private static void Prefix(ref int pSkill, ushort NewSkillID)
        {
            _restorePending = false;
            if (!MutationFullCapacityAddNewBridgeTrigger.Enabled) return;
            try
            {
                if (!MutationAddNewBridgeArming.Armed) return;
                if (NewSkillID != MutationAddNewBridgeArming.Target) return;
                if ((pSkill & 0xFFFF) != MutationAddNewBridgeArming.SourceSkillId) return;

                // Let native write target into the slot and run its own
                // internal presentation using the real value - mark a
                // restore owed once it returns.
                _restorePending = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityOverwriteSuppressor prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix(ref int pSkill, ushort NewSkillID)
        {
            if (!_restorePending) return;
            _restorePending = false;
            try
            {
                if ((pSkill & 0xFFFF) == MutationAddNewBridgeArming.Target)
                {
                    pSkill = MutationAddNewBridgeArming.SourceSkillId;
                }
                MutationAddNewBridgeArming.Suppressed = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityOverwriteSuppressor postfix failed safely: {ex.Message}");
            }
        }
    }

    internal static class MutationAddNewBridgeArming
    {
        internal static bool Armed;
        internal static bool Suppressed;
        internal static int Unit;
        internal static long StockPtr;
        internal static int SourceIndex;
        internal static int SourceSkillId;
        internal static int Target;
    }

    internal static class MutationFullCapacityAddNewGate
    {
        internal const int GateStableFrameThreshold = 5;

        private static bool _pending;
        private static int _unit;
        private static long _stockPtr;
        private static int _target;

        internal static int StableCount;
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

    internal static class MutationFullCapacityAddNewBridgePendingRedirect
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

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class MutationFullCapacityAddNewBridgeMonitor
    {
        private static void Postfix()
        {
            if (!MutationFullCapacityAddNewBridgeTrigger.Enabled) return;

            TryPerformDeferredRedirect();

            if (!MutationAddNewBridgeState.Active) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                int seq = gbwk.SeqInfo.Current;
                if (seq == 21 || seq == 22) return; // still in progress

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero ||
                    stock.Pointer.ToInt64() != MutationAddNewBridgeState.WatchedStockPtr)
                {
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] MUTCAP-ADDNEW-LOST-TRACKING; " +
                        $"unit={MutationAddNewBridgeState.WatchedUnit}; " +
                        "reason=pCurrentStock-changed-before-outcome-observed; " +
                        $"seq={seq}.");
                    // Defensive restore even in the lost-tracking case -
                    // EventParam is a GBWK-global field, not per-stock, so
                    // restoring is still correct regardless of which unit
                    // is now current.
                    RestoreOriginalPending();
                    MutationAddNewBridgeState.CompleteAndLatch();
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
                    if (skills[i] == MutationAddNewBridgeState.Target) targetPresent = true;
                    if (skills[i] == MutationAddNewBridgeState.SourceSkillIdAtTrigger) sourcePreserved = true;
                }

                if (targetPresent)
                {
                    // COMPLETION: EventParam was already restored at the
                    // exact rstAddSkill/seq22 boundary by
                    // MutationFullCapacityAddNewBridgeInsertionInjector -
                    // nothing further to do here for that field.
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTCAP-ADDNEW-COMPLETE; " +
                        $"unit={MutationAddNewBridgeState.WatchedUnit}; " +
                        $"finalSkills=[{string.Join(",", skills)}]; sourcePreserved={sourcePreserved}; " +
                        $"targetPresent={targetPresent}; skillcnt={skillCnt}; seq={seq}; " +
                        $"pending32After={gbwk.EventParam}.");
                }
                else
                {
                    // CANCELLATION: rstAddSkill never fired (player backed
                    // out of the forget UI), so the insertion injector's
                    // own restore never ran. FIX over the reference
                    // Power-Up implementation (which has no restore on
                    // this path at all, confirmed by source grep,
                    // 2026-09-15) - restore explicitly here.
                    RestoreOriginalPending();
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTCAP-ADDNEW-CANCEL; " +
                        $"unit={MutationAddNewBridgeState.WatchedUnit}; " +
                        "reason=target-not-present-after-forget-flow-exit; " +
                        $"finalSkills=[{string.Join(",", skills)}]; skillcnt={skillCnt}; seq={seq}; " +
                        $"pending32AfterRestore={gbwk.EventParam}; " +
                        $"restoredToNativeOriginal={MutationAddNewBridgeState.OriginalPending32}.");
                }

                MutationAddNewBridgeState.CompleteAndLatch();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityAddNewBridgeMonitor postfix failed safely: {ex.Message}");
            }
        }

        private static void RestoreOriginalPending()
        {
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                gbwk.EventParam = (ushort)MutationAddNewBridgeState.OriginalPending32;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityAddNewBridgeMonitor restore failed safely: {ex.Message}");
            }
        }

        private static void TryPerformDeferredRedirect()
        {
            if (!MutationFullCapacityAddNewBridgePendingRedirect.Pending) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                int seqNow = gbwk.SeqInfo.Current;
                if (seqNow == MutationFullCapacityAddNewBridgePendingRedirect.SeqAtDefer) return;

                int target = MutationFullCapacityAddNewBridgePendingRedirect.Target;
                int unit = MutationFullCapacityAddNewBridgePendingRedirect.Unit;
                long stockPtr = MutationFullCapacityAddNewBridgePendingRedirect.StockPtr;
                int sourceIndex = MutationFullCapacityAddNewBridgePendingRedirect.SourceIndex;
                int sourceSkillId = MutationFullCapacityAddNewBridgePendingRedirect.SourceSkillId;
                int originalPending = MutationFullCapacityAddNewBridgePendingRedirect.OriginalPending;

                MutationFullCapacityAddNewBridgePendingRedirect.Clear();

                // Bounded EventParam UI window - same design as the
                // ordinary bridge's own (see FullCapacityAddNewBridgePoc.cs
                // "BOUNDED EVENTPARAM UI WINDOW" comment): stage target for
                // the duration of the borrowed seq21/22 UI (native's own
                // hidden-pending-slot display reads EventParam), restore
                // the true original at the insertion boundary (COMPLETE)
                // or here in the Monitor (CANCEL, the fix over the
                // reference implementation).
                gbwk.EventParam = (ushort)target;
                gbwk.SeqInfo.Current = 21;
                int seqAfter = gbwk.SeqInfo.Current;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MUTCAP-ADDNEW-ENTER-FORGET; " +
                    $"seqBefore={seqNow}; seqAfter={seqAfter}; target={SkillNameResolver.Format(target)}; " +
                    $"eventParamStagedForUi={gbwk.EventParam}; originalPendingToRestoreLater={originalPending}.");

                MutationAddNewBridgeState.Activate(
                    unit, stockPtr, target, sourceIndex, sourceSkillId, originalPending);
            }
            catch (Exception ex)
            {
                MutationFullCapacityAddNewBridgePendingRedirect.Clear();
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityAddNewBridgeMonitor deferred redirect failed safely: {ex.Message}");
            }
        }
    }

    // Insertion boundary - mirrors FullCapacityAddNewBridgeInsertionInjector
    // exactly (same two confirmed rstAddSkill call sites, same
    // SeqInfo.Current==22 gate). See that file's own comment for the full
    // reasoning; the only difference here is which bridge's state class is
    // consulted.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstAddSkill))]
    internal static class MutationFullCapacityAddNewBridgeInsertionInjector
    {
        internal static readonly bool Enabled = true;

        private const int ExpectedPostCompactionSkillCnt = 7;

        private static bool _injected;
        private static int _nativeOriginalToRestore;

        private static void Prefix()
        {
            _injected = false;
            if (!Enabled) return;
            try
            {
                if (!MutationAddNewBridgeState.Active) return;

                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                if (gbwk.SeqInfo.Current != 22) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;
                if (stock.Pointer.ToInt64() != MutationAddNewBridgeState.WatchedStockPtr) return;
                if (stock.id != MutationAddNewBridgeState.WatchedUnit) return;
                if (stock.skillcnt != ExpectedPostCompactionSkillCnt) return;

                int target = MutationAddNewBridgeState.Target;
                if (target == 0) return;

                _nativeOriginalToRestore = MutationAddNewBridgeState.OriginalPending32;
                // Idempotent safety net - should already equal target
                // (staged at ENTER-FORGET), but ensure it regardless.
                gbwk.EventParam = (ushort)target;
                _injected = true;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MUTCAP-INJECT; " +
                    $"unit={stock.id}; target={SkillNameResolver.Format(target)}; " +
                    $"skillcnt={stock.skillcnt}; seqCurrent={gbwk.SeqInfo.Current}; " +
                    $"nativeOriginalToRestore={_nativeOriginalToRestore}.");
            }
            catch (Exception ex)
            {
                _injected = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityAddNewBridgeInsertionInjector prefix failed safely: {ex.Message}");
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
                gbwk.EventParam = (ushort)_nativeOriginalToRestore;

                var stock = gbwk.pCurrentStock;
                int target = MutationAddNewBridgeState.Target;
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
                    "[NocturneModernGameplay] MUTCAP-RESTORE; " +
                    $"target={SkillNameResolver.Format(target)}; " +
                    $"eventParamAfterNative={eventParamAfterNative}; " +
                    $"restoredToNativeOriginal={_nativeOriginalToRestore}; " +
                    $"targetPresent={targetPresent}; skillcnt={stock?.skillcnt.ToString() ?? "?"}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationFullCapacityAddNewBridgeInsertionInjector postfix failed safely: {ex.Message}");
            }
        }
    }

    internal static class MutationAddNewBridgeState
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
