using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    internal static class SkillMutationLearnAsNew
    {
        private const int MaxLearnedSkills = 8;
        private const int QuietFramesRequired = 180;
        private const int CompletionQuietFramesRequired = 30;
        private const int MaximumWaitFrames = 18000;

        private sealed class PendingMutation
        {
            internal Il2Cppnewdata_H.datUnitWork_t Stock = null!;
            internal int Index;
            internal ushort OriginalSkill;
            internal ushort MutatedSkill;
            internal int WaitFrames;
            internal sbyte TargetPos;
            internal sbyte TargetIndex;
            internal sbyte TargetCount;
            internal sbyte[] SkillProgress = Array.Empty<sbyte>();
            internal sbyte[] ResultSkillProgress = Array.Empty<sbyte>();
            // Set only when this item originated from Inline Progression
            // Arbitration (Phase 2E), which deliberately re-synced WorkStock
            // to the mutation's own unit before native's forget lifecycle
            // ran. Null for ordinary queued items, which never touch
            // WorkStock this way. Used at completion time to restore
            // WorkStock to what it was before the Inline path started -
            // confirmed missing prior to this fix, and confirmed to leave
            // WorkStock pointing at the wrong unit once native advanced to
            // the next candidate.
            internal Il2Cppnewdata_H.datUnitWork_t? PriorWorkStock;
            // Explicit transaction-origin flag (kept separate from
            // PriorWorkStock, which is WorkStock-restoration bookkeeping,
            // not an origin indicator - conflating the two would blur their
            // meanings later). True only for items created by Inline
            // Progression Arbitration (Phase 2E); false for ordinary queued
            // items.
            internal bool IsInlineTransaction;
        }

        // Phase 2E Inline Progression Arbitration. Deliberately separate
        // from _active/PendingMutation: this tracks only "is native's own
        // rstUpdateSeqDefaultSkill transition to seq=21 still pending",
        // which is a different question from the existing HasSkill-based
        // completion tracking (_active can legitimately be null while this
        // is still awaiting, and vice versa).
        private sealed class InlineAwaitingTransition
        {
            internal Il2Cppnewdata_H.datUnitWork_t Stock = null!;
            internal ushort OriginalSkill;
            internal ushort MutatedSkill;
            internal IntPtr PriorWorkStockPtr;
            internal Il2Cppnewdata_H.datUnitWork_t? PriorWorkStock;
            internal int StartFrame;
            internal int CallCount;
        }

        private static InlineAwaitingTransition? _inlineAwaiting;

        // Safety bound: if native's own DefaultSkill handler has not
        // transitioned to seq=21 within this many rstUpdate invocations,
        // treat this as fail-closed - release the gate, restore WorkStock,
        // and fall back to the existing Queue/Pending path. Chosen generously
        // relative to the largest observed normal-flow call count (~47).
        private const int InlineAwaitingMaxCalls = 120;

        private sealed class NativeContextSnapshot
        {
            internal Il2Cppnewdata_H.datUnitWork_t? CurrentStock;
            internal Il2Cppnewdata_H.datUnitWork_t? WorkStock;
            internal IntPtr CurrentStockPointer;
            internal IntPtr WorkStockPointer;
            internal sbyte TargetPos;
            internal sbyte TargetIndex;
            internal sbyte TargetCount;
            internal byte SeqFlag;
            internal sbyte SeqCurrent;
            internal sbyte SeqNext;
            internal sbyte SeqLast;
            internal sbyte SeqChange;
            internal sbyte SeqMesFlag;
            internal int SeqTimer;
        }

        private static readonly Queue<PendingMutation> Pending = new();
        private static readonly HashSet<string> UiGetterObservations = new();
        // Disabled while the standalone "Always" diagnostic is active. The
        // unfinished queued full-skill route must not alter its result state.
        //
        // V3 baseline verification (temporary): set to false so legacy's
        // entire intervention surface (Pending queue, StartNext, Inline PoC,
        // original-skill restore, WorkStock rebinding, completion handlers)
        // is inert - TryConvertReplacementToAddition's existing _enabled
        // check early-returns before any of that runs. Only V3
        // (SkillMutationV3.cs) is expected to react to native Mutations
        // while this is false. Re-enable only when returning to legacy for
        // comparison purposes.
        private static bool _enabled = false;
        private static bool _applying;
        // Per-(Stock pointer, slot index) handled set. Replaces the previous
        // single-bool _mutationHandled. A slot is registered here the moment
        // Learn-As-New begins acting on it (immediate route or queued-full
        // route) and stays registered through success AND cancellation, so a
        // cancelled forget-UI outcome cannot let the same slot re-roll a new
        // Mutation within the same result lifecycle. Cleared only at the start
        // of a new result lifecycle (see ResultLifecycleStartClearPatch),
        // guarded so an in-flight lifecycle is never cleared out from under
        // itself.
        private static readonly HashSet<(IntPtr Stock, int Slot)> HandledSlots = new();
        // Stage 1 of the overwrite gate. Entries describe writes that a later,
        // separately validated stage may suppress. The current implementation
        // only observes them and never skips native rstOverWriteSkill.
        private static readonly HashSet<(IntPtr Stock, int Slot)> OverwriteSuppressed = new();
        private static bool _standardLearnFlowActive;
        private static bool _completionObserved;
        private static bool _activeCancelled;
        private static int _cancelExitFrames;
        private static int _completionQuietFrames;
        private static int _lastObservedUiCursor = -1;
        private static PendingMutation? _active;
        private static bool _haveLearnSequenceSnapshot;
        private static byte _seqFlag;
        private static sbyte _seqCurrent;
        private static sbyte _seqNext;
        private static sbyte _seqLast;
        private static sbyte _seqChange;
        private static sbyte _seqMesFlag;
        private static int _seqTimer;
        private static sbyte _processStat;
        private static bool _completeAfterOuterUpdate;
        // Phase 2C A/B test flag (see Investigation Archive, Inline
        // Transaction Architecture). When true, StartNext skips the
        // explicit pCurrentStock/WorkStock binding it would otherwise
        // perform, while every other part of the queue/synthetic-forget
        // pipeline (bookkeeping, capture/restore, completion recovery,
        // logging) remains fully active and unchanged. Default false:
        // production behavior is identical to before this flag existed.
        // This exists solely to empirically test whether native's own seq
        // progression keeps pCurrentStock on the transaction's own unit
        // without MOD explicitly forcing it.
        internal static readonly bool ExperimentalDisableQueueOwnershipBinding = false;

        // Phase 2E minimal PoC. Default false: production behavior
        // (Queue/Pending, as always) is unchanged unless explicitly enabled
        // for this experiment. When true, TryInlineImmediateStart is
        // attempted first at the "Window C" point (right after MOD restores
        // the original skill, before Pending.Enqueue would otherwise run).
        internal static readonly bool ExperimentalInlineImmediateStart = true;

        // Attempts to start the forget/learn lifecycle immediately, while
        // native ownership (pCurrentStock/WorkStock) is still confirmed to
        // be the mutation's own unit, instead of deferring to Pending and
        // replaying later via Queue's capture/restore machinery.
        //
        // Absolute safety guarantees:
        //   - Never writes pCurrentStock, WorkStock, SeqInfo (any field),
        //     TargetIndex, or TargetCnt.
        //   - Only calls two native functions whose call chains were
        //     statically confirmed (Phase 2E) to have no SeqInfo/caller-seq
        //     dependency: rstChkAddSkill and rstInitSkillAct.
        //   - Fail-closed: if any guard fails, or rstChkAddSkill does not
        //     return the expected "forget required" result, this returns
        //     false immediately and the caller falls back to the existing,
        //     already-proven Queue/Pending path unchanged.
        //   - Does not touch HandledSlots/OverwriteSuppressed bookkeeping
        //     (already applied by the caller before this is invoked).
        private static bool TryInlineImmediateStart(
            Il2Cppnewdata_H.datUnitWork_t stock, ushort originalSkill, ushort mutatedSkill)
        {
            // Arming-as-commit-point design (Correction): every step that
            // can fail is performed BEFORE _inlineAwaiting is assigned.
            // workSynced/priorWork/priorWorkPtr are tracked here, outside
            // the try block's local scope, so the outer catch can always
            // roll back correctly regardless of exactly which line threw.
            bool workSynced = false;
            Il2Cppnewdata_H.datUnitWork_t? priorWork = null;
            IntPtr priorWorkPtr = IntPtr.Zero;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return false;

                int seqCurrent = gbwk.SeqInfo.Current;
                var liveCurrent = gbwk.pCurrentStock;
                IntPtr liveCurrentPtr = liveCurrent?.Pointer ?? IntPtr.Zero;

                // Ownership guards only - WorkStock is handled separately
                // below as a deliberate, logged, single controlled write,
                // not as a pass/fail guard. Rationale (per investigation):
                // pCurrentStock is already correct ownership and must never
                // be touched; WorkStock is forget/action UI context, not
                // ownership, and syncing it here is not the same kind of
                // operation as Queue's old-unit ownership rebinding.
                bool guardsPass =
                    seqCurrent == 10 &&
                    liveCurrentPtr == stock.Pointer &&
                    mutatedSkill != 0 &&
                    originalSkill != mutatedSkill &&
                    _active == null &&
                    _inlineAwaiting == null &&
                    !PendingContains(stock.Pointer, mutatedSkill);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] INLINE-IMMEDIATE candidate; " +
                    $"frame={UnityEngine.Time.frameCount} unit={stock.id} seqCurrent={seqCurrent} " +
                    $"currentPtr=0x{liveCurrentPtr.ToInt64():X} guardsPass={guardsPass}.");

                if (!guardsPass) return false;

                priorWork = gbwk.WorkStock;
                priorWorkPtr = priorWork?.Pointer ?? IntPtr.Zero;
                workSynced = priorWorkPtr != stock.Pointer;
                if (workSynced)
                {
                    // The one deliberate, logged write this PoC performs
                    // beyond bookkeeping: sync WorkStock (forget/action UI
                    // context) to the mutation's own unit. pCurrentStock is
                    // never touched. Restored on any failure path below.
                    gbwk.WorkStock = stock;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] INLINE-IMMEDIATE work-sync; " +
                        $"frame={UnityEngine.Time.frameCount} " +
                        $"beforeWorkPtr=0x{priorWorkPtr.ToInt64():X} " +
                        $"afterWorkPtr=0x{(gbwk.WorkStock?.Pointer ?? IntPtr.Zero).ToInt64():X} " +
                        $"currentPtr=0x{liveCurrentPtr.ToInt64():X} mutationPtr=0x{stock.Pointer.ToInt64():X}.");
                }

                bool RestoreWorkAndFail(string reason)
                {
                    if (workSynced) gbwk.WorkStock = priorWork;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] INLINE-IMMEDIATE fallback; " +
                        $"frame={UnityEngine.Time.frameCount} unit={stock.id} reason={reason} " +
                        $"restoredWorkPtr=0x{(gbwk.WorkStock?.Pointer ?? IntPtr.Zero).ToInt64():X}.");
                    return false;
                }

                gbwk.SelectSkillID = mutatedSkill;
                gbwk.PUpSkillID = mutatedSkill;

                sbyte capacityResult;
                try { capacityResult = rstcalc.rstChkAddSkill(mutatedSkill); }
                catch (Exception ex) { return RestoreWorkAndFail($"rstChkAddSkill-exception:{ex.Message}"); }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] INLINE-IMMEDIATE capacity; " +
                    $"frame={UnityEngine.Time.frameCount} unit={stock.id} result={capacityResult}.");

                if (capacityResult != 2) return RestoreWorkAndFail($"capacityResult={capacityResult}");

                gbwk.DefSkillResult = 2;
                MelonLogger.Msg("[NocturneModernGameplay] INLINE-IMMEDIATE armed-step-1-defresult-set; " +
                    $"frame={UnityEngine.Time.frameCount}.");

                MelonLogger.Msg(
                    "[NocturneModernGameplay] INLINE-IMMEDIATE init-before; " +
                    $"frame={UnityEngine.Time.frameCount} seqCurrent={gbwk.SeqInfo.Current} " +
                    $"currentUnit={(gbwk.pCurrentStock?.id ?? -1)} workUnit={(gbwk.WorkStock?.id ?? -1)}.");

                try { rstcalc.rstInitSkillAct(8); }
                catch (Exception ex) { return RestoreWorkAndFail($"rstInitSkillAct-exception:{ex.Message}"); }
                MelonLogger.Msg("[NocturneModernGameplay] INLINE-IMMEDIATE armed-step-2-initskillact-done; " +
                    $"frame={UnityEngine.Time.frameCount}.");

                MelonLogger.Msg(
                    "[NocturneModernGameplay] INLINE-IMMEDIATE init-after; " +
                    $"frame={UnityEngine.Time.frameCount} seqCurrent={gbwk.SeqInfo.Current} " +
                    $"currentUnit={(gbwk.pCurrentStock?.id ?? -1)} workUnit={(gbwk.WorkStock?.id ?? -1)}.");

                // Phase 2E Correction: normal native flow (rstCalc's own
                // seq=8 dispatch calling rstUpdateSeqDefaultSkill every
                // rstUpdate) was confirmed to call this function repeatedly
                // (observed 20-47 times) before the transition to seq=21
                // actually completes - a single direct call is therefore
                // known to be insufficient. Rather than call it once here,
                // arm InlineAwaitingTransition and let ProcessInlineAwaiting
                // (invoked once per rstUpdate, matching native's own call
                // cadence) retry it under a progression gate until native
                // itself writes SeqInfo.Current=21, or the safety bound is
                // hit. WorkStock is intentionally NOT restored here - it
                // must remain synced to the mutation's own unit while this
                // is pending.
                //
                // Arming-as-commit-point: everything above this line can
                // fail and roll back cleanly via RestoreWorkAndFail/the
                // outer catch. From this point on, nothing that can throw
                // runs before _inlineAwaiting is assigned - it is built into
                // a fully-formed local object first, then assigned in one
                // step, immediately followed by return true with no further
                // statements that could throw.
                var prepared = new InlineAwaitingTransition
                {
                    Stock = stock,
                    OriginalSkill = originalSkill,
                    MutatedSkill = mutatedSkill,
                    PriorWorkStockPtr = priorWorkPtr,
                    PriorWorkStock = priorWork,
                    StartFrame = UnityEngine.Time.frameCount,
                    CallCount = 0
                };
                MelonLogger.Msg("[NocturneModernGameplay] INLINE-IMMEDIATE armed-step-3-object-built; " +
                    $"frame={UnityEngine.Time.frameCount}.");

                _inlineAwaiting = prepared;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] INLINE-AWAITING armed; " +
                    $"frame={UnityEngine.Time.frameCount} unit={stock.id} mutated={mutatedSkill}.");

                return true;
            }
            catch (Exception ex)
            {
                // Full rollback: this PoC never leaves a dangling armed
                // state or an un-restored WorkStock behind, regardless of
                // exactly where the exception originated.
                _inlineAwaiting = null;
                try
                {
                    if (workSynced)
                    {
                        var gbwkForRestore = rstinit.GBWK;
                        if (gbwkForRestore != null && gbwkForRestore.Pointer != IntPtr.Zero)
                            gbwkForRestore.WorkStock = priorWork;
                    }
                }
                catch (Exception restoreEx)
                {
                    MelonLogger.Warning($"[NocturneModernGameplay] INLINE-IMMEDIATE rollback WorkStock restore also failed: {restoreEx.Message}");
                }
                MelonLogger.Warning($"[NocturneModernGameplay] INLINE-IMMEDIATE failed safely, falling back to Queue: {ex.Message}");
                return false;
            }
        }

        private static bool PendingContains(IntPtr stockPtr, ushort mutatedSkill)
        {
            foreach (var item in Pending)
                if (item.Stock.Pointer == stockPtr && item.MutatedSkill == mutatedSkill) return true;
            return false;
        }

        // Phase 2E Inline Progression Arbitration. Called once per rstUpdate
        // invocation (see wiring in SkillMutationInlineTimelineProbe.cs) -
        // matching native's own observed call cadence for
        // rstUpdateSeqDefaultSkill, rather than a fixed-rate timer. Re-checks
        // every guard on each call (ownership can legitimately still be
        // correct, or can have gone wrong, between calls) and fails closed
        // at the first sign of trouble: restores WorkStock and releases the
        // gate, falling back to whatever native does on its own from there
        // (the existing Queue/Pending path remains fully intact and unaffected).
        internal static void ProcessInlineAwaitingTransition()
        {
            var awaiting = _inlineAwaiting;
            if (awaiting == null) return;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) { ReleaseInlineAwaiting(awaiting, "gbwk-unavailable"); return; }

                var liveCurrent = gbwk.pCurrentStock;
                var liveWork = gbwk.WorkStock;
                IntPtr liveCurrentPtr = liveCurrent?.Pointer ?? IntPtr.Zero;
                IntPtr liveWorkPtr = liveWork?.Pointer ?? IntPtr.Zero;
                int seqCurrent = gbwk.SeqInfo.Current;

                bool guardsStillHold =
                    seqCurrent == 10 &&
                    liveCurrentPtr == awaiting.Stock.Pointer &&
                    liveWorkPtr == awaiting.Stock.Pointer &&
                    gbwk.DefSkillResult == 2;

                if (!guardsStillHold)
                {
                    ReleaseInlineAwaiting(awaiting,
                        $"guard-violated seqCurrent={seqCurrent} currentPtr=0x{liveCurrentPtr.ToInt64():X} " +
                        $"workPtr=0x{liveWorkPtr.ToInt64():X} defSkillResult={gbwk.DefSkillResult}");
                    return;
                }

                if (awaiting.CallCount >= InlineAwaitingMaxCalls)
                {
                    ReleaseInlineAwaiting(awaiting, $"timeout after {awaiting.CallCount} calls");
                    return;
                }

                awaiting.CallCount++;
                try { rstupdate.rstUpdateSeqDefaultSkill(); }
                catch (Exception ex)
                {
                    ReleaseInlineAwaiting(awaiting, $"rstUpdateSeqDefaultSkill-exception:{ex.Message}");
                    return;
                }

                int seqAfter = gbwk.SeqInfo.Current;
                if (seqAfter == 21)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] INLINE-AWAITING transition-succeeded; " +
                        $"frame={UnityEngine.Time.frameCount} unit={awaiting.Stock.id} " +
                        $"callCount={awaiting.CallCount} seqCurrent={seqAfter} seqLast={gbwk.SeqInfo.Last}.");

                    // Native itself has now taken ownership of the seq=21
                    // transition. Register the existing HasSkill-based
                    // completion tracking (_active) so the rest of the
                    // pipeline (forget UI, OverrideQueuedLearnSkill,
                    // cancel/completed) proceeds exactly as it does for a
                    // queued item - the only thing that differed was how we
                    // got to seq=21 in the first place.
                    _active = new PendingMutation
                    {
                        Stock = awaiting.Stock, Index = -1,
                        OriginalSkill = awaiting.OriginalSkill, MutatedSkill = awaiting.MutatedSkill,
                        TargetPos = gbwk.TargetPos, TargetIndex = gbwk.TargetIndex, TargetCount = gbwk.TargetCnt,
                        SkillProgress = CopyStockSkillProgress(awaiting.Stock),
                        ResultSkillProgress = CopyResultSkillProgress(),
                        PriorWorkStock = awaiting.PriorWorkStock,
                        IsInlineTransaction = true
                    };
                    _completionObserved = false;
                    _completeAfterOuterUpdate = false;
                    _activeCancelled = false;
                    _cancelExitFrames = 0;
                    _lastObservedUiCursor = -1;
                    _completionQuietFrames = 0;
                    _active.WaitFrames = 0;

                    _inlineAwaiting = null; // gate released - progression may resume normally from here
                }
                else if (awaiting.CallCount % 10 == 0)
                {
                    // Periodic, low-volume progress log - avoid per-call spam.
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] INLINE-AWAITING still-waiting; " +
                        $"frame={UnityEngine.Time.frameCount} unit={awaiting.Stock.id} " +
                        $"callCount={awaiting.CallCount} seqCurrent={seqAfter}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] INLINE-AWAITING failed safely: {ex.Message}");
                ReleaseInlineAwaiting(awaiting, $"unexpected-exception:{ex.Message}");
            }
        }

        private static void ReleaseInlineAwaiting(InlineAwaitingTransition awaiting, string reason)
        {
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk != null && gbwk.Pointer != IntPtr.Zero && awaiting.PriorWorkStock != null)
                    gbwk.WorkStock = awaiting.PriorWorkStock;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] INLINE-AWAITING released; " +
                    $"frame={UnityEngine.Time.frameCount} unit={awaiting.Stock.id} reason={reason} " +
                    $"callCount={awaiting.CallCount} restoredWorkPtr=0x{awaiting.PriorWorkStockPtr.ToInt64():X}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] INLINE-AWAITING release failed safely: {ex.Message}");
            }
            finally
            {
                _inlineAwaiting = null;
            }
        }

        // Phase 2E Progression Gate. Called from rstCalc's Prefix (see
        // wiring in SkillMutationInlineTimelineProbe.cs). Returns true to
        // allow native's rstCalc to run normally, false to skip it for this
        // frame only - used exclusively to hold the mutation's own unit at
        // seq=10 while InlineAwaitingTransition is pending, so native's own
        // seq=10->11->(next candidate) progression does not race ahead of
        // the DefaultSkill transition. Guards re-checked every call; any
        // mismatch releases the gate (fail-open, not fail-stuck) rather than
        // blocking rstCalc indefinitely.
        internal static bool ShouldAllowRstCalc()
        {
            var awaiting = _inlineAwaiting;
            if (awaiting == null) return true;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return true; // fail-open, not stuck

                var liveCurrent = gbwk.pCurrentStock;
                IntPtr liveCurrentPtr = liveCurrent?.Pointer ?? IntPtr.Zero;
                int seqCurrent = gbwk.SeqInfo.Current;

                // Only gate while still genuinely at seq=10 on the same unit.
                // Once native itself has moved (seq changed, or ownership
                // changed - e.g. the guard-violation path above already
                // released this), allow rstCalc through unconditionally.
                if (seqCurrent != 10 || liveCurrentPtr != awaiting.Stock.Pointer) return true;

                _rstCalcSkipCount++;
                return false; // hold this frame's rstCalc - awaiting.CallCount's own rstUpdateSeqDefaultSkill call takes its place
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] ShouldAllowRstCalc failed safely (allowing rstCalc): {ex.Message}");
                return true; // fail-open
            }
        }

        // Diagnostic-only counter/summary, added specifically to test the
        // hypothesis that Progression Arbitration's rstCalc gate is
        // interfering with native's own seq=8->9->10 advance. Never affects
        // behavior - purely for the INLINE-TIMELINE snapshot to show whether
        // _inlineAwaiting is armed and how many times rstCalc has actually
        // been skipped so far.
        private static int _rstCalcSkipCount;
        internal static string InlineAwaitingSummary =>
            _inlineAwaiting == null
                ? "none"
                : $"unit={_inlineAwaiting.Stock.id} mutated={_inlineAwaiting.MutatedSkill} " +
                  $"callCount={_inlineAwaiting.CallCount} startFrame={_inlineAwaiting.StartFrame} " +
                  $"rstCalcSkipCount={_rstCalcSkipCount}";

        private static NativeContextSnapshot? _drainSnapshot;
        private static bool _drainContinuationRequested;
        private static bool _pendingHandoffRequested;

        // Loop-detection safety net for the "349 zombie" pattern: a normal
        // (non-Mutation) skill candidate that is never actually added (e.g.
        // because it is already effectively treated as present) keeps
        // getting re-offered by native every calc pass, and each pass can
        // roll a fresh Mutation on some other slot. If this keeps happening
        // while MOD holds native context ownership (_drainSnapshot != null)
        // and no transaction is currently mid-flight, the drain never
        // naturally empties. This is a read-mostly counter; it only ever
        // acts by clearing Pending and requesting a deferred restore - it
        // never touches _active, never writes to native fields directly,
        // and never forces a restore from inside rstCalc's own call stack.
        private static ushort _loopDetectSkillId;
        private static int _loopDetectCount;
        private const int LoopDetectThreshold = 5;

        internal static bool IsApplying => _applying;
        internal static bool SuppressNestedMutation => _active != null;
        internal static bool CheckHasSkill(Il2Cppnewdata_H.datUnitWork_t stock, ushort skill) => HasSkill(stock, skill);
        internal static ushort ActiveOriginalSkill => _active?.OriginalSkill ?? 0;
        internal static ushort ActiveMutatedSkill => _active?.MutatedSkill ?? 0;
        internal static int ActiveIndex => _active?.Index ?? -1;

        // Read-only exposure for unified telemetry. Adds no behavior; these
        // properties only surface existing private state for logging.
        //
        // Used only by ExperimentalInlineGateV2 (isolated, single-switch
        // experiment). Read-only: does not create, clear, or modify any
        // transaction state. Scoping the experimental gate to "the active
        // transaction's own stock" is the whole point of this accessor -
        // it must never be widened into a global flag.
        internal static bool HasActiveTransactionFor(IntPtr stockPointer) =>
            _active != null && _active.Stock.Pointer == stockPointer;
        internal static string ActiveSummary =>
            _active == null
                ? "none"
                : $"unit={_active.Stock.id} index={_active.Index} " +
                  $"original={_active.OriginalSkill} mutated={_active.MutatedSkill} " +
                  $"completionObserved={_completionObserved} completeAfterOuterUpdate={_completeAfterOuterUpdate}";
        internal static int HandledSlotCount => HandledSlots.Count;
        internal static bool IsSlotHandled(IntPtr stock, int index) => HandledSlots.Contains((stock, index));
        internal static bool WouldSuppressOverwrite(IntPtr stock, int index) =>
            stock != IntPtr.Zero && index >= 0 && OverwriteSuppressed.Contains((stock, index));
        internal static int QueueDepth => Pending.Count;
        internal static string PendingHeadSummary =>
            Pending.Count == 0 ? "empty" :
            $"unit={Pending.Peek().Stock.id} index={Pending.Peek().Index} " +
            $"original={Pending.Peek().OriginalSkill} mutated={Pending.Peek().MutatedSkill}";

        // Scope flags for read-only telemetry only. Set/cleared synchronously around
        // the native call in Harmony Prefix/Postfix (single-threaded, so a
        // datSkillName.Get call observed while a flag is true genuinely happened
        // during that native method's execution, not merely during the same
        // sequence value). Never read by any behavior-affecting code path.
        internal static bool InsideDestroySkillScope;
        internal static bool InsideDestroyConfirmScope;

        internal static bool ShouldSuppressQueuedRecurrence(ushort skill)
        {
            if (_active == null || _applying) return false;
            MelonLogger.Msg(
                "[NocturneModernGameplay] SKILL-MUTATION queued recurrence suppressed; " +
                $"unit={_active.Stock.id} requested={skill} mutated={_active.MutatedSkill}.");
            return true;
        }

        internal static void OverrideQueuedUiSkillId(ref int skillId, int context = -1)
        {
            if (_active == null || _completionObserved) return;
            try
            {
                IntPtr action = Marshal.ReadIntPtr(rstinit.GBWK.Pointer, 0x88);
                if (action == IntPtr.Zero) return;
                IntPtr actionData = Marshal.ReadIntPtr(action, 0x20);
                if (actionData == IntPtr.Zero ||
                    Marshal.ReadByte(actionData, 0x14) != MaxLearnedSkills) return;
                // Name rendering passes the demon id for list entries and 0
                // for the confirmation message. Ignore calls belonging to a
                // different demon, and never rewrite one of the real 8 slots.
                if (context > 0 && context != _active.Stock.id) return;
                // context 0 is the single confirmation-message lookup. It may
                // receive a cached learned-skill id for the synthetic slot and
                // must always name the mutation skill. List rendering uses the
                // demon id and still protects all real learned entries.
                if (context != 0 &&
                    HasSkill(_active.Stock, unchecked((ushort)skillId))) return;
                skillId = _active.MutatedSkill;
            }
            catch { }
        }

        internal static void ObserveQueuedUiGetter(string getter, int skillId, int argument = -1)
        {
            if (_active == null || _completionObserved) return;
            try
            {
                IntPtr action = Marshal.ReadIntPtr(rstinit.GBWK.Pointer, 0x88);
                if (action == IntPtr.Zero) return;
                IntPtr actionData = Marshal.ReadIntPtr(action, 0x20);
                if (actionData == IntPtr.Zero ||
                    Marshal.ReadByte(actionData, 0x14) != MaxLearnedSkills) return;
                string observation = $"{getter}:{skillId}:{argument}";
                if (!UiGetterObservations.Add(observation)) return;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION UI getter call; " +
                    $"getter={getter} skill={skillId} arg={argument} " +
                    $"mutated={_active.MutatedSkill}.");
            }
            catch { }
        }

        internal static void OverrideQueuedUiHelpSkillId(ref int skillId)
        {
            if (_active == null || _completionObserved) return;
            try
            {
                IntPtr action = Marshal.ReadIntPtr(rstinit.GBWK.Pointer, 0x88);
                if (action == IntPtr.Zero) return;
                IntPtr actionData = Marshal.ReadIntPtr(action, 0x20);
                if (actionData == IntPtr.Zero ||
                    Marshal.ReadByte(actionData, 0x14) != MaxLearnedSkills) return;
                // Unlike names, the help getter renders only the currently
                // selected entry. The native UI may pass a cached learned-skill
                // id for the synthetic ninth slot, so always redirect it.
                skillId = _active.MutatedSkill;
            }
            catch { }
        }

        internal static void BeginMutationCandidate()
        {
            // Per-(stock, slot) handled state is intentionally NOT reset here.
            // Resetting on every candidate discovery is what allowed a
            // cancelled forget-UI outcome to let the same slot re-roll a new
            // Mutation later in the same result lifecycle. HandledSlots is
            // only cleared at a confirmed new-lifecycle boundary; see
            // ResultLifecycleStartClearPatch.
            //
            // Reaching mutation selection proves that any preceding ordinary
            // learn/forget prompt has already left its blocking phase. Some
            // accepted/declined paths do not call rstAddSkill.
            _standardLearnFlowActive = false;
            NotifyResultActivity();
        }

        internal static void NotifyNonForgetSkillAction()
        {
            if (_applying) return;
            _standardLearnFlowActive = false;
        }

        internal static void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) FailAllSafely("feature disabled");
            MelonLogger.Msg($"[NocturneModernGameplay] Skill Mutation: Learn as New {(enabled ? "enabled" : "disabled")}.");
        }

        internal static void NotifyResultActivity()
        {
        }

        internal static void NotifyStandardLearnFlowStarted()
        {
            if (_applying) return;
            _standardLearnFlowActive = true;
        }

        internal static void CaptureStandardLearnSequence()
        {
            if (_applying) return;
            try
            {
                var seq = rstinit.GBWK.SeqInfo;
                _seqFlag = seq.Flag;
                _seqCurrent = seq.Current;
                _seqNext = seq.Next;
                _seqLast = seq.Last;
                _seqChange = seq.Change;
                _seqMesFlag = seq.MesFlag;
                _seqTimer = seq.Timer;
                _processStat = rstinit.gProcessStat;
                _haveLearnSequenceSnapshot = true;
                MelonLogger.Msg("[NocturneModernGameplay] SKILL-LEARN-FLOW sequence captured; " +
                    $"current={_seqCurrent} next={_seqNext} last={_seqLast} change={_seqChange} " +
                    $"flag={_seqFlag} mesFlag={_seqMesFlag} timer={_seqTimer} process={_processStat}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] Could not capture learn sequence: {ex.Message}");
            }
        }

        internal static void NotifyStandardLearnFlowCompleted(string source)
        {
            if (_active == null)
            {
                _standardLearnFlowActive = false;
                return;
            }
            try
            {
                var current = rstinit.GBWK.pCurrentStock;
                if (current == null || current.Pointer == IntPtr.Zero ||
                    current.Pointer != _active.Stock.Pointer ||
                    !HasSkill(_active.Stock, _active.MutatedSkill))
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILL-MUTATION unrelated learn completion ignored; " +
                        $"source={source} activeUnit={_active.Stock.id} " +
                        $"currentUnit={(current == null || current.Pointer == IntPtr.Zero ? -1 : current.id)} " +
                        $"mutated={_active.MutatedSkill}.");
                    return;
                }
            }
            catch { return; }
            _standardLearnFlowActive = false;
            _completionObserved = true;
            _completionQuietFrames = 0;
            MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION forget-flow completion observed; " +
                $"source={source} unit={_active.Stock.id} mutated={_active.MutatedSkill}.");
        }

        internal static void OverrideQueuedLearnSkill(
            ref ushort skill, Il2Cppnewdata_H.datUnitWork_t stock)
        {
            if (_active == null || _completionObserved || stock == null ||
                stock.Pointer == IntPtr.Zero || _active.Stock.Pointer != stock.Pointer) return;
            if (skill == _active.MutatedSkill) return;
            ushort replaced = skill;
            skill = _active.MutatedSkill;
            MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION final-add redirected; " +
                $"unit={stock.id} from={replaced} to={skill}.");
        }

        internal static void Sample()
        {
            if (!_enabled || (_active == null && Pending.Count == 0)) return;

            if (_active != null)
            {
                CorrectQueuedUiSelection();
                // Do not expire while the player is genuinely inside this
                // demon's learn/forget UI. Long inspection sessions are valid.
                if (!IsInsideActiveLearnUi() && ++_active.WaitFrames > MaximumWaitFrames)
                {
                    MarkActiveCancelled("timeout waiting for add confirmation");
                    return;
                }
                ObserveCancelledLearnExit();
                if (!_completionObserved || IsInsideActiveLearnUi() ||
                    ++_completionQuietFrames < CompletionQuietFramesRequired) return;
                CompleteActive();
                return;
            }

            // Queue startup is performed synchronously from rstUpdate's
            // postfix at the last demon boundary, before the result screen
            // begins fading out. Starting later from OnUpdate is too late.
            return;
        }

        internal static void TryStartQueuedAtResultBoundary()
        {
            if (!_enabled || _active != null || Pending.Count == 0 ||
                (!_pendingHandoffRequested && _drainSnapshot == null)) return;
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                if (seq.Current != 11 ||
                    work.TargetIndex != work.TargetCnt + 1) return;
                if (_drainSnapshot == null)
                {
                    SkillMutationTelemetry.ObserveEventLifecycle("synthetic-capture-before");
                    SkillMutationTelemetry.ObservePairedSkillSnapshot("synthetic-capture-before");
                    _drainSnapshot = CaptureNativeContext();
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] NATIVE-CONTEXT captured; " +
                        $"currentStock=0x{_drainSnapshot.CurrentStockPointer.ToInt64():X} " +
                        $"workStock=0x{_drainSnapshot.WorkStockPointer.ToInt64():X} " +
                        $"target={_drainSnapshot.TargetPos}/{_drainSnapshot.TargetIndex}/{_drainSnapshot.TargetCount} " +
                        $"seq={_drainSnapshot.SeqCurrent}/{_drainSnapshot.SeqNext}/" +
                        $"{_drainSnapshot.SeqLast}/{_drainSnapshot.SeqChange} " +
                        $"flag={_drainSnapshot.SeqFlag} mesFlag={_drainSnapshot.SeqMesFlag} " +
                        $"timer={_drainSnapshot.SeqTimer} queue={Pending.Count}.");
                }
                _pendingHandoffRequested = false;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION last-demon boundary captured before fade; " +
                    $"target={work.TargetPos}/{work.TargetIndex}/{work.TargetCnt} queue={Pending.Count}.");
                StartNext();
                SkillMutationTelemetry.ObserveEventLifecycle("synthetic-started-after");
                SkillMutationTelemetry.ObservePairedSkillSnapshot("synthetic-started-after");
            }
            catch (Exception ex)
            {
                FailActiveSafely(
                    $"could not start queued mutations at result boundary: {ex.Message}");
            }
        }

        internal static void CompleteQueuedAtDefaultSkillBoundary()
        {
            if (_active == null) return;
            try
            {
                if (!_completionObserved)
                {
                    _activeCancelled = !HasSkill(_active.Stock, _active.MutatedSkill);
                    _completionObserved = true;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILL-MUTATION default-skill boundary completion; " +
                        $"unit={_active.Stock.id} mutated={_active.MutatedSkill} " +
                        $"cancelled={_activeCancelled}.");
                }
                // This callback runs inside rstUpdate. Starting the next demon
                // here lets the still-running outer update overwrite its UI
                // state on return. Defer the transition to rstUpdate's postfix.
                _completeAfterOuterUpdate = true;
            }
            catch (Exception ex)
            {
                FailActiveSafely($"default-skill boundary completion failed: {ex.Message}");
            }
        }

        internal static void CompleteQueuedAfterOuterUpdate()
        {
            if (!_completeAfterOuterUpdate || _active == null) return;
            _completeAfterOuterUpdate = false;
            try { CompleteActive(); }
            catch (Exception ex)
            {
                FailActiveSafely($"outer-update completion failed: {ex.Message}");
            }
        }

        internal static void ContinueOwnedDrainIfRequested()
        {
            if (_drainSnapshot == null || !_drainContinuationRequested || _active != null) return;
            _drainContinuationRequested = false;
            MelonLogger.Msg(
                "[NocturneModernGameplay] PENDING-SCHEDULER deferred; " +
                $"action=continue-owned-drain queue={Pending.Count}.");
            OnSyntheticItemFinished();
        }

        internal static bool TryCompletePendingQueuedReturn(string caller)
        {
            PendingMutation? item = _active;
            int frame = UnityEngine.Time.frameCount;
            sbyte current = -1;
            sbyte last = -1;
            try
            {
                var seqState = rstinit.GBWK.SeqInfo;
                current = seqState.Current;
                last = seqState.Last;
            }
            catch { }

            void Log(string outcome)
            {
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION queued return completion; " +
                    $"caller={caller} frame={frame} " +
                    $"unit={(item == null ? -1 : item.Stock.id)} " +
                    $"mutated={(item == null ? 0 : item.MutatedSkill)} " +
                    $"seq={current}/{last} completionObserved={_completionObserved} " +
                    $"completeAfterOuterUpdate={_completeAfterOuterUpdate} " +
                    $"completion={outcome}.");
            }

            if (item == null)
            {
                Log("skipped-no-active");
                return false;
            }
            if (_applying)
            {
                Log("skipped-applying");
                return false;
            }
            try
            {
                var seq = rstinit.GBWK.SeqInfo;
                // The native forget confirmation returns to sequence 8. For
                // our synthetic queued learn, running SkillPowerUp again would
                // recalculate scheduled skills and execute result-exit/fade
                // before rstUpdate's postfix can start the next demon.
                if (seq.Current != 8 || seq.Last != 21)
                {
                    Log("skipped-not-return-boundary");
                    return false;
                }

                bool fromCalc = string.Equals(caller, "rstChkAddSkill", StringComparison.Ordinal);
                if (!_completionObserved)
                {
                    _activeCancelled = !HasSkill(item.Stock, item.MutatedSkill);
                    _completionObserved = true;
                }
                _standardLearnFlowActive = false;
                seq.Change = 0;

                if (fromCalc)
                {
                    _completeAfterOuterUpdate = false;
                    Log("executed");
                    CompleteActive(fromCalc: true);
                    return true;
                }

                if (_completeAfterOuterUpdate)
                {
                    Log("skipped-already-deferred");
                    return true;
                }

                // This callback is inside rstUpdate. Preserve the established
                // lifecycle and finish from the outer update postfix.
                _completeAfterOuterUpdate = true;
                Log("executed-deferred");
                return true;
            }
            catch (Exception ex)
            {
                FailActiveSafely($"queued power-up return interception failed: {ex.Message}");
                return false;
            }
        }

        internal static bool InterceptQueuedPowerUpReturn()
        {
            return TryCompletePendingQueuedReturn("rstUpdateSeqSkillPowerUp");
        }

        internal static void RecoverQueuedCompletionAtResultExit()
        {
            if (_active == null) return;
            try
            {
                // Choosing the synthetic ninth entry means "do not learn the
                // new skill". Native code can leave the forget action and
                // jump straight to result-exit sequence 19 without calling
                // rstUpdateSeqDefaultSkill. Waiting for the frame sampler lets
                // the fade advance before the next queued demon is shown,
                // which ultimately blackens that UI. We are already in the
                // outer rstUpdate postfix here, so settle it immediately.
                if (rstinit.GBWK.SeqInfo.Current != 19) return;
                // rstAddSkill marks successful replacement completion before
                // native sequence 8 later reaches 19. That already-observed
                // success must also be settled here immediately; otherwise
                // Sample waits its quiet-frame delay while the exit fade is
                // already advancing over the next queued demon.
                if (!_completionObserved)
                {
                    _activeCancelled =
                        !HasSkill(_active.Stock, _active.MutatedSkill);
                    _completionObserved = true;
                }
                _standardLearnFlowActive = false;
                _cancelExitFrames = 0;
                _completionQuietFrames = 0;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION result-exit completion recovered; " +
                    $"unit={_active.Stock.id} mutated={_active.MutatedSkill} " +
                    $"cancelled={_activeCancelled}.");
                CompleteActive();
            }
            catch (Exception ex)
            {
                FailActiveSafely($"result-exit completion recovery failed: {ex.Message}");
            }
        }

        // True from the moment a loop-abort is requested until the drain is
        // fully restored (RestoreNativeContext succeeds and clears
        // _drainSnapshot). While true, TryConvertReplacementToAddition below
        // refuses to start any *new* Learn-As-New conversion - closing the
        // window the user identified: without this, a new Mutation could
        // still enqueue into Pending while waiting for the in-flight active
        // transaction to finish naturally, undoing the abort. Any Mutation
        // that occurs during this window simply falls through to native
        // overwrite, which is the existing, already-understood fallback
        // behavior (Problem B) - safe, and strictly preferable to
        // accumulating more queued work during an active abort.
        private static bool DrainClosing => _loopAbortRequested || _loopAbortReadyForRestore;

        internal static void TryConvertReplacementToAddition(Il2Cppnewdata_H.datUnitWork_t stock,
            int index, ushort originalSkill, ushort mutatedSkill)
        {
            // Read-only pointer-identity check at the exact entry point where
            // the (stock, index, mutatedSkill) guard below is evaluated. This
            // does not affect the guard itself - it only records, for later
            // comparison, whether "stock" here is the same object as
            // GBWK.WorkStock / GBWK.pCurrentStock at this instant, and where
            // stock.skill[index]'s actual storage address is.
            try
            {
                var gbwk = rstinit.GBWK;
                IntPtr workStockPtr = gbwk?.WorkStock?.Pointer ?? IntPtr.Zero;
                IntPtr currentStockPtr = gbwk?.pCurrentStock?.Pointer ?? IntPtr.Zero;
                IntPtr stockPtr = stock?.Pointer ?? IntPtr.Zero;
                IntPtr skillArrayPtr = stock?.skill?.Pointer ?? IntPtr.Zero;
                IntPtr elementAddr = (skillArrayPtr != IntPtr.Zero && index >= 0)
                    ? new IntPtr(skillArrayPtr.ToInt64() + 0x20 + index * 2) : IntPtr.Zero;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] CONVERT-ENTRY-CHECK; " +
                    $"stockPtr=0x{stockPtr.ToInt64():X} " +
                    $"workStockPtr=0x{workStockPtr.ToInt64():X} sameAsWorkStock={stockPtr == workStockPtr} " +
                    $"currentStockPtr=0x{currentStockPtr.ToInt64():X} sameAsCurrentStock={stockPtr == currentStockPtr} " +
                    $"index={index} elementAddr=0x{elementAddr.ToInt64():X} " +
                    $"originalSkill={originalSkill} mutatedSkill={mutatedSkill}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] CONVERT-ENTRY-CHECK failed safely: {ex.Message}");
            }

            if (!_enabled || _applying || DrainClosing || stock == null || stock.Pointer == IntPtr.Zero ||
                HandledSlots.Contains((stock.Pointer, index)) ||
                originalSkill == 0 || mutatedSkill == 0 || index < 0 ||
                index >= stock.skill.Length || unchecked((ushort)stock.skill[index]) != mutatedSkill) return;

            _applying = true;
            // Registered up front, before either route below runs, and never
            // removed on cancellation: this (stock, slot) is now considered
            // handled for the remainder of the current result lifecycle.
            HandledSlots.Add((stock.Pointer, index));
            OverwriteSuppressed.Add((stock.Pointer, index));
            try
            {
                stock.skill[index] = unchecked((short)originalSkill);
                if (stock.skillcnt >= MaxLearnedSkills)
                {
                    // Phase 2D: read-only, answers the central question -
                    // at the exact instant this mutation is queued, is
                    // native's own pCurrentStock still this transaction's
                    // own unit, or has native already moved on to a
                    // different unit? Never writes anything.
                    var liveCurrent = rstinit.GBWK.pCurrentStock;
                    var liveWork = rstinit.GBWK.WorkStock;
                    IntPtr liveCurrentPtr = liveCurrent?.Pointer ?? IntPtr.Zero;
                    IntPtr liveWorkPtr = liveWork?.Pointer ?? IntPtr.Zero;
                    int liveCurrentUnit = liveCurrent == null || liveCurrentPtr == IntPtr.Zero ? -1 : liveCurrent.id;
                    int liveWorkUnit = liveWork == null || liveWorkPtr == IntPtr.Zero ? -1 : liveWork.id;
                    bool stillOwnUnit = liveCurrentPtr == stock.Pointer;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] INLINE-TIMELINE milestone=pending-enqueue; " +
                        $"frame={UnityEngine.Time.frameCount} mutationUnit={stock.id} mutationStockPtr=0x{stock.Pointer.ToInt64():X} " +
                        $"liveCurrentUnit={liveCurrentUnit} liveCurrentStockPtr=0x{liveCurrentPtr.ToInt64():X} " +
                        $"liveWorkUnit={liveWorkUnit} liveWorkStockPtr=0x{liveWorkPtr.ToInt64():X} " +
                        $"pCurrentStockStillOwnUnit={stillOwnUnit} " +
                        $"seqCurrent={rstinit.GBWK.SeqInfo.Current} targetIndex={rstinit.GBWK.TargetIndex} targetCnt={rstinit.GBWK.TargetCnt}.");

                    // Phase 2E minimal PoC: attempt Window C ("Inline
                    // Immediate Start") before falling back to the existing
                    // Queue/Pending path. Strictly guarded, fail-closed.
                    // Never writes pCurrentStock/WorkStock/SeqInfo/
                    // TargetIndex/TargetCnt - only calls the two native
                    // functions (rstChkAddSkill, rstInitSkillAct) whose
                    // call chains were statically confirmed to have no
                    // SeqInfo/caller-seq dependency.
                    if (ExperimentalInlineImmediateStart &&
                        TryInlineImmediateStart(stock, originalSkill, mutatedSkill))
                    {
                        return; // Inline path taken - do not also enqueue.
                    }

                    Pending.Enqueue(new PendingMutation { Stock = stock, Index = index,
                        OriginalSkill = originalSkill, MutatedSkill = mutatedSkill,
                        TargetPos = rstinit.GBWK.TargetPos,
                        TargetIndex = rstinit.GBWK.TargetIndex,
                        TargetCount = rstinit.GBWK.TargetCnt,
                        SkillProgress = CopyStockSkillProgress(stock),
                        ResultSkillProgress = CopyResultSkillProgress() });
                    _pendingHandoffRequested = true;
                    MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION learn-as-new-full queued; " +
                        $"unit={stock.id} index={index} original={originalSkill} mutated={mutatedSkill} queueDepth={Pending.Count}.");
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] PENDING-SCHEDULER deferred; " +
                        $"reason=waiting-for-verified-seq11-boundary unit={stock.id} " +
                        $"slot={index} queue={Pending.Count}.");
                    return;
                }

                fclCombineCalcCore.cmbAddSkill(mutatedSkill, stock);
                bool restored = unchecked((ushort)stock.skill[index]) == originalSkill;
                bool added = HasSkill(stock, mutatedSkill);
                MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION learn-as-new; " +
                    $"unit={stock.id} index={index} original={originalSkill} mutated={mutatedSkill} " +
                    $"restored={restored} added={added} skillCount={stock.skillcnt}.");
                if (!restored || !added)
                {
                    stock.skill[index] = unchecked((short)mutatedSkill);
                    MelonLogger.Warning("[NocturneModernGameplay] Learn-as-new fallback: native addition failed; normal replacement restored.");
                }
            }
            catch (Exception ex)
            {
                RestoreReplacementSafely(stock, index, mutatedSkill);
                MelonLogger.Error($"[NocturneModernGameplay] Learn-as-new failed safely: {ex}");
            }
            finally { _applying = false; }
        }

        private static void StartNext()
        {
            if (_drainSnapshot == null)
            {
                FailAllSafely("queue drain attempted without owned native context");
                return;
            }
            if (_active != null || Pending.Count == 0) return;
            _active = Pending.Dequeue();
            _completionObserved = false;
            _completeAfterOuterUpdate = false;
            _activeCancelled = false;
            _cancelExitFrames = 0;
            _lastObservedUiCursor = -1;
            _completionQuietFrames = 0;
            _active.WaitFrames = 0;
            _applying = true;
            try
            {
                PendingMutation item = _active;
                if (item.Stock == null || item.Stock.Pointer == IntPtr.Zero || item.Index < 0 ||
                    item.Index >= item.Stock.skill.Length) throw new InvalidOperationException("queued unit is no longer valid");
                item.Stock.skill[item.Index] = unchecked((short)item.OriginalSkill);
                RestoreResultTarget(item);
                // Do not re-enter sequence 6 here. Once the native result
                // cursor has passed a demon, sequence 6 keeps the last native
                // current unit and reruns that unit's scheduled learn/mutation
                // calculation. Bind the queued stock directly and enter only
                // the already-initialized standard forget action.
                //
                // Phase 2C A/B test: this explicit binding is the ONLY thing
                // gated by ExperimentalDisableQueueOwnershipBinding. Every
                // other part of StartNext/the synthetic forget flow below is
                // untouched. Default false - production behavior unchanged.
                if (!ExperimentalDisableQueueOwnershipBinding)
                {
                    rstinit.GBWK.pCurrentStock = item.Stock;
                    rstinit.GBWK.WorkStock = item.Stock;
                }
                else
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] AB-OWNERSHIP-BINDING skipped; " +
                        $"unit={item.Stock.id} stockPtr=0x{item.Stock.Pointer.ToInt64():X} " +
                        $"liveCurrentStockPtr=0x{(rstinit.GBWK.pCurrentStock?.Pointer ?? IntPtr.Zero).ToInt64():X} " +
                        $"liveWorkStockPtr=0x{(rstinit.GBWK.WorkStock?.Pointer ?? IntPtr.Zero).ToInt64():X}.");
                }
                StartPreparedForgetFlow();
            }
            catch (Exception ex) { FailActiveSafely($"could not prepare queued visual: {ex.Message}"); }
            finally { _applying = false; }
        }

        private static void StartPreparedForgetFlow()
        {
            _applying = true;
            try
            {
                PendingMutation item = _active!;
                if (!_haveLearnSequenceSnapshot)
                    throw new InvalidOperationException("no standard learn-sequence snapshot is available");
                rstinit.GBWK.WorkStock = item.Stock;
                var seq = rstinit.GBWK.SeqInfo;

                // EXPERIMENT (①full removal): the previous implementation
                // rewound seq.Flag/Current/Next/Last/Change/MesFlag/Timer and
                // gProcessStat to the CaptureStandardLearnSequence() snapshot
                // here. Native analysis of rstChkAddSkill and rstInitSkillAct
                // found no read of SeqInfo or gProcessStat in either function,
                // and the rewound seq.Current/Last/Change were overwritten a
                // few lines below regardless. The rewind is removed; only the
                // as-is SeqInfo at the moment this queued item starts is
                // logged for comparison.
                MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION forget-flow seq at start; " +
                    $"unit={item.Stock.id} current={seq.Current} next={seq.Next} last={seq.Last} " +
                    $"change={seq.Change} flag={seq.Flag} mesFlag={seq.MesFlag} timer={seq.Timer} " +
                    $"processStat={rstinit.gProcessStat} " +
                    $"snapshotCurrent={_seqCurrent} snapshotLast={_seqLast} snapshotChange={_seqChange}.");
                LogHasSkillState("queued-full-route-start", item);

                // The ordinary full-capacity learn/forget route displays and
                // commits SelectSkillID/DefSkillResult. PUpSkillID belongs to
                // the mutation presentation and is not sufficient here.
                rstinit.GBWK.SelectSkillID = item.MutatedSkill;
                rstinit.GBWK.PUpSkillID = item.MutatedSkill;
                ClearStockSkillProgress(item.Stock);
                ClearResultSkillProgress();
                sbyte capacityResult = rstcalc.rstChkAddSkill(item.MutatedSkill);
                if (capacityResult != 2) throw new InvalidOperationException($"unexpected capacity result {capacityResult}");
                rstinit.GBWK.DefSkillResult = 2;
                _standardLearnFlowActive = true;
                rstcalc.rstInitSkillAct(8);

                MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION forget-flow seq before switch; " +
                    $"unit={item.Stock.id} current={seq.Current} next={seq.Next} last={seq.Last} " +
                    $"change={seq.Change} flag={seq.Flag} mesFlag={seq.MesFlag} timer={seq.Timer} " +
                    $"processStat={rstinit.gProcessStat}.");

                // Sequence 8 is the ordinary level-up learn dispatcher. If it
                // runs again it recalculates the demon's scheduled next skill.
                // The action is already initialized, so enter its forget-prompt
                // child directly and retain the mutation context in PUpSkillID.
                seq.Current = 21;
                seq.Last = 8;
                seq.Change = 1;
                MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION queued-full-route started; " +
                    $"unit={item.Stock.id} index={item.Index} original={item.OriginalSkill} mutated={item.MutatedSkill} remainingQueue={Pending.Count}.");
            }
            catch (Exception ex) { FailActiveSafely($"could not start standard forget flow: {ex.Message}"); }
            finally { _applying = false; }
        }

        private static void LogHasSkillState(string site, PendingMutation item)
        {
            try
            {
                ushort normalLearnSkill = rstinit.GBWK.SelectSkillID;
                bool hasNormal = HasSkill(item.Stock, normalLearnSkill);
                bool hasMutated = HasSkill(item.Stock, item.MutatedSkill);
                var seq = rstinit.GBWK.SeqInfo;
                var workStock = rstinit.GBWK.WorkStock;
                string ws32 = workStock != null && workStock.Pointer != IntPtr.Zero
                    ? unchecked((ushort)Marshal.ReadInt16(workStock.Pointer, 0x32)).ToString() : "n/a";
                string ws34 = workStock != null && workStock.Pointer != IntPtr.Zero
                    ? unchecked((ushort)Marshal.ReadInt16(workStock.Pointer, 0x34)).ToString() : "n/a";
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HASSKILL-TRACE " + site + "; " +
                    $"frame={UnityEngine.Time.frameCount} unit={item.Stock.id} " +
                    $"stockPtr=0x{item.Stock.Pointer.ToInt64():X} slot={item.Index} " +
                    $"normalLearnSkill={normalLearnSkill} hasNormalLearnSkill={hasNormal} " +
                    $"mutatedSkill={item.MutatedSkill} hasMutatedSkill={hasMutated} " +
                    $"workStock+0x32={ws32} workStock+0x34={ws34} " +
                    $"seqCurrent={seq.Current} seqLast={seq.Last}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] HASSKILL-TRACE {site} failed safely: {ex.Message}");
            }
        }

        private static void CompleteActive(bool fromCalc = false)
        {
            PendingMutation item = _active!;
            if (_activeCancelled)
            {
                if (!item.IsInlineTransaction) RestoreSkillProgress(item);
                LogHasSkillState("cancel", item);
                MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION queued learn cancelled; " +
                    $"unit={item.Stock.id} original={item.OriginalSkill} " +
                    $"mutated={item.MutatedSkill} remainingQueue={Pending.Count}.");
                ClearNativeLearnActionStateSafely("cancel");
                // Deliberately NOT calling RestorePriorWorkStockSafely here
                // yet - isolating the DefSkillResult fix for verification
                // first, per investigation discipline (PriorWorkStock's
                // correctness as "the value native expects after completion"
                // is not yet CONFIRMED, only HYPOTHESIS). Re-enable after
                // DefSkillResult alone is confirmed to resolve the forget-loop.
                FinishActiveAndContinueQueue(fromCalc);
                return;
            }
            bool mutatedPresent = HasSkill(item.Stock, item.MutatedSkill);
            if (!mutatedPresent)
            {
                LogHasSkillState("completion-verification-failed", item);
                FailActiveSafely("completion verification failed (mutated skill was not learned)");
                return;
            }
            if (!item.IsInlineTransaction) RestoreSkillProgress(item);
            LogHasSkillState("completed", item);
            MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION queued-full-route completed; " +
                $"unit={item.Stock.id} original={item.OriginalSkill} mutated={item.MutatedSkill} remainingQueue={Pending.Count}.");
            ClearNativeLearnActionStateSafely("completed");
            // Deliberately NOT calling RestorePriorWorkStockSafely here yet -
            // see the matching comment on the cancel path above.
            FinishActiveAndContinueQueue(fromCalc);
        }

        // Confirmed fix: WorkStock was left pointing at this Inline
        // transaction's own unit even after the transaction fully finished,
        // because nothing ever restored it. Once native advanced to the
        // next candidate (pCurrentStock changed), WorkStock remained on the
        // stale unit, contaminating the next unit's processing. Only
        // applies to items that recorded a PriorWorkStock (i.e. only
        // Inline-originated items) - ordinary queued items never touch
        // WorkStock this way and are unaffected.
        private static void RestorePriorWorkStockSafely(PendingMutation item, string context)
        {
            if (item.PriorWorkStock == null) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                IntPtr beforePtr = gbwk.WorkStock?.Pointer ?? IntPtr.Zero;
                gbwk.WorkStock = item.PriorWorkStock;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] INLINE-WORKSTOCK-RESTORE; " +
                    $"context={context} frame={UnityEngine.Time.frameCount} unit={item.Stock.id} " +
                    $"beforeWorkPtr=0x{beforePtr.ToInt64():X} " +
                    $"afterWorkPtr=0x{(gbwk.WorkStock?.Pointer ?? IntPtr.Zero).ToInt64():X}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] INLINE-WORKSTOCK-RESTORE failed safely: {ex.Message}");
            }
        }

        // Confirmed fix: rstUpdateSeqDefaultSkill's own seq=8 dispatch
        // re-checks DefSkillResult != 0 on every single call (not just once),
        // and re-enters the native forget-action initializer whenever it
        // finds it still set. Prior to this fix, nothing ever cleared
        // DefSkillResult after a transaction (queued or Inline) genuinely
        // finished, so native kept re-triggering the forget UI for the same
        // unit indefinitely. This mirrors what the existing Queue path
        // never needed to do only because it wrote SeqInfo.Current directly
        // and thus never routed back through native's own seq=8 DefSkillResult
        // check in the same way. Read/write scope here is deliberately
        // limited to DefSkillResult alone - PUpSkillID/SelectSkillID/
        // PUpSkillIndex are left untouched, since they are confirmed to be
        // freely overwritten by native's own next candidate-selection pass
        // and clearing them here would be an unverified, unnecessary write.
        private static void ClearNativeLearnActionStateSafely(string context)
        {
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                if (gbwk.DefSkillResult == 0) return; // already clear, nothing to do
                int before = gbwk.DefSkillResult;
                gbwk.DefSkillResult = 0;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] NATIVE-LEARN-STATE-CLEAR; " +
                    $"context={context} frame={UnityEngine.Time.frameCount} " +
                    $"defSkillResultBefore={before} defSkillResultAfter={gbwk.DefSkillResult}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] NATIVE-LEARN-STATE-CLEAR failed safely: {ex.Message}");
            }
        }

        private static void FinishActiveAndContinueQueue(bool fromCalc = false)
        {
            _active = null;
            _lastObservedUiCursor = -1;
            _activeCancelled = false;
            _completionObserved = false;
            _standardLearnFlowActive = false;
            if (fromCalc)
            {
                _drainContinuationRequested = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] PENDING-SCHEDULER drain-continuation-deferred; " +
                    $"queue={Pending.Count} ownershipPresent={_drainSnapshot != null}.");
                return;
            }
            OnSyntheticItemFinished();
        }

        private static void OnSyntheticItemFinished()
        {
            if (Pending.Count > 0)
            {
                StartNext();
                return;
            }
            RestoreNativeContext();
        }

        // Called from rstChkAddSkill's Prefix (existing hook, no new Harmony
        // patch) with the normal-skill candidate id native is currently
        // offering. Only acts while MOD owns native context and no
        // transaction is mid-flight - an active transaction is never
        // disturbed by this. If the same candidate id keeps reappearing
        // while Pending sits non-empty (the "349 zombie" pattern), this
        // discards the stale queue and requests a deferred restore through
        // the same rstCalc-safe path already used for fromCalc completions,
        // rather than forcing a restore from whatever call stack this is
        // invoked from.
        // Loop-detection safety net for the "349 zombie" pattern. Confirmed
        // via real-machine logs: recurrence happens across separate rstCalc
        // frames (~7.6s / ~1089 frames apart in the observed case), each
        // producing a fresh seq6->7->8->9->10 cycle and a fresh Mutation on
        // some other slot, while _active persists (holding the *previous*
        // cycle's Learn-As-New transaction) the entire time. Detection must
        // therefore continue regardless of _active - it is NOT reset by an
        // active transaction, only by the drain no longer being owned or
        // Pending going empty.
        //
        // On threshold, this does NOT touch _active, Pending, or native
        // fields directly, and does NOT call FailActiveSafely (that would
        // wrongly roll back an already-succeeded Mutation result). It only
        // requests a dedicated abort, consumed later at a confirmed-safe
        // native boundary (see RequestDrainAbort / FinalizeDrainAbortIfReady
        // / rstCalc's own return, statically confirmed at 0x18227f767-778).
        internal static void ObserveNormalCandidateRecurrence(ushort skillId)
        {
            if (_drainSnapshot == null || Pending.Count == 0)
            {
                _loopDetectSkillId = 0;
                _loopDetectCount = 0;
                return;
            }

            if (skillId == _loopDetectSkillId)
            {
                _loopDetectCount++;
            }
            else
            {
                _loopDetectSkillId = skillId;
                _loopDetectCount = 1;
            }

            if (_loopDetectCount < LoopDetectThreshold) return;

            MelonLogger.Error(
                "[NocturneModernGameplay] PENDING-SCHEDULER loop-detected-abort-requested; " +
                $"skillId={skillId} recurrenceCount={_loopDetectCount} queueDepth={Pending.Count} " +
                $"activeAtDetection=[{ActiveSummary}] " +
                "(fail-closed: native repeatedly re-offers this normal candidate across separate " +
                "calc cycles; requesting a drain abort at the next confirmed-safe rstCalc-return boundary. " +
                "The currently active transaction, if any, is left untouched.)");

            _loopDetectSkillId = 0;
            _loopDetectCount = 0;
            _loopAbortRequested = true;
        }

        // Set only by rstCalc's own Postfix (the confirmed native calc-return
        // boundary, 0x18227f767-778), once _loopAbortRequested has been seen.
        // This does not itself touch any state - it only marks that the
        // current rstCalc call has fully returned since the abort was
        // requested, so it is now safe for rstUpdate's Postfix (a different,
        // already-existing safe checkpoint) to discard Pending and defer to
        // restore, without ever interrupting an in-flight native calc call
        // or the currently active transaction.
        //
        // IMPORTANT: _loopAbortReadyForRestore (and therefore DrainClosing)
        // must stay true for the ENTIRE window until the drain is actually
        // restored - not just until Pending is discarded. If it cleared as
        // soon as Pending.Clear() ran, a still-in-flight active transaction
        // would leave a window, while it finishes naturally, during which a
        // fresh Mutation could enqueue right back into Pending - silently
        // undoing the abort. So this flag is only cleared once _drainSnapshot
        // itself has become null (confirmed restore), checked on every call.
        private static bool _loopAbortRequested;
        private static bool _loopAbortReadyForRestore;
        private static bool _loopAbortPendingDiscarded;

        internal static void MarkDrainAbortReadyAfterCalc()
        {
            if (!_loopAbortRequested) return;
            _loopAbortRequested = false;
            _loopAbortReadyForRestore = true;
        }

        // Consumed from rstUpdate's Postfix (existing safe checkpoint,
        // outside rstCalc's call stack). Discards Pending only once - never
        // touches _active, never rolls back an already-succeeded Mutation,
        // never calls RestoreNativeContext directly from here (that still
        // goes through the existing fromCalc-safe _drainContinuationRequested
        // path once _active naturally becomes null). Runs every frame while
        // an abort is pending, so it can detect the moment the drain is
        // actually restored and only then reopen DrainClosing.
        internal static void FinalizeDrainAbortIfReady()
        {
            if (!_loopAbortReadyForRestore) return;

            if (!_loopAbortPendingDiscarded)
            {
                int discarded = Pending.Count;
                Pending.Clear();
                _loopAbortPendingDiscarded = true;
                MelonLogger.Error(
                    "[NocturneModernGameplay] PENDING-SCHEDULER loop-detected-abort-finalized; " +
                    $"discardedQueueDepth={discarded} activeStillInFlight={_active != null} " +
                    "(Pending discarded at confirmed native calc-return boundary; " +
                    "the in-flight active transaction, if any, will complete and hand off to restore normally. " +
                    "No new Learn-As-New conversion will start until the drain is fully restored.)");
            }

            if (_active == null && _drainSnapshot != null)
            {
                // No transaction in flight and nothing left queued: proceed
                // straight to restore via the same safe path as an empty
                // drain would.
                OnSyntheticItemFinished();
            }
            // else: a transaction is still in flight. Do not disturb it. Its
            // own normal completion path (FinishActiveAndContinueQueue) will
            // find Pending empty and naturally proceed to restore.

            if (_drainSnapshot == null)
            {
                // Restore has actually happened (either just now, or on an
                // earlier call once the active transaction finished
                // naturally). Only now is it safe to reopen DrainClosing.
                _loopAbortReadyForRestore = false;
                _loopAbortPendingDiscarded = false;
            }
        }

        private static NativeContextSnapshot CaptureNativeContext()
        {
            var work = rstinit.GBWK;
            var seq = work.SeqInfo;
            var current = work.pCurrentStock;
            var workStock = work.WorkStock;
            return new NativeContextSnapshot
            {
                CurrentStock = current,
                WorkStock = workStock,
                CurrentStockPointer = current == null ? IntPtr.Zero : current.Pointer,
                WorkStockPointer = workStock == null ? IntPtr.Zero : workStock.Pointer,
                TargetPos = work.TargetPos,
                TargetIndex = work.TargetIndex,
                TargetCount = work.TargetCnt,
                SeqFlag = seq.Flag,
                SeqCurrent = seq.Current,
                SeqNext = seq.Next,
                SeqLast = seq.Last,
                SeqChange = seq.Change,
                SeqMesFlag = seq.MesFlag,
                SeqTimer = seq.Timer
            };
        }

        private static void RestoreNativeContext()
        {
            NativeContextSnapshot? snapshot = _drainSnapshot;
            if (snapshot == null) return;
            try
            {
                SkillMutationTelemetry.ObserveEventLifecycle("restore-before");
                SkillMutationTelemetry.ObservePairedSkillSnapshot("restore-before");
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;

                // Restore ownership first, then target cursor, then sequence.
                work.pCurrentStock = snapshot.CurrentStock;
                work.WorkStock = snapshot.WorkStock;

                work.TargetPos = snapshot.TargetPos;
                work.TargetIndex = snapshot.TargetIndex;
                work.TargetCnt = snapshot.TargetCount;

                seq.Flag = snapshot.SeqFlag;
                seq.Current = snapshot.SeqCurrent;
                seq.Next = snapshot.SeqNext;
                seq.Last = snapshot.SeqLast;
                seq.Change = snapshot.SeqChange;
                seq.MesFlag = snapshot.SeqMesFlag;
                seq.Timer = snapshot.SeqTimer;

                SkillMutationTelemetry.ObserveEventLifecycle("restore-after");
                SkillMutationTelemetry.ObservePairedSkillSnapshot("restore-after");

                IntPtr currentPointer = work.pCurrentStock == null
                    ? IntPtr.Zero : work.pCurrentStock.Pointer;
                IntPtr workPointer = work.WorkStock == null
                    ? IntPtr.Zero : work.WorkStock.Pointer;
                bool stockMatch = currentPointer == snapshot.CurrentStockPointer &&
                    workPointer == snapshot.WorkStockPointer;
                bool targetMatch = work.TargetPos == snapshot.TargetPos &&
                    work.TargetIndex == snapshot.TargetIndex &&
                    work.TargetCnt == snapshot.TargetCount;
                bool seqMatch = seq.Flag == snapshot.SeqFlag &&
                    seq.Current == snapshot.SeqCurrent &&
                    seq.Next == snapshot.SeqNext &&
                    seq.Last == snapshot.SeqLast &&
                    seq.Change == snapshot.SeqChange &&
                    seq.MesFlag == snapshot.SeqMesFlag &&
                    seq.Timer == snapshot.SeqTimer;
                bool match = stockMatch && targetMatch && seqMatch;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] NATIVE-CONTEXT restored; " +
                    $"stockMatch={stockMatch} targetMatch={targetMatch} " +
                    $"seqMatch={seqMatch} match={match}.");
                if (!match)
                {
                    MelonLogger.Error(
                        "[NocturneModernGameplay] NATIVE-CONTEXT restore mismatch; " +
                        $"stockMatch={stockMatch} targetMatch={targetMatch} " +
                        $"seqMatch={seqMatch}; native resume blocked.");
                    return;
                }

                _drainSnapshot = null;
                _drainContinuationRequested = false;
                _pendingHandoffRequested = false;
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] NATIVE-CONTEXT restore mismatch; " +
                    $"exception={ex.Message}; native resume blocked.");
            }
        }

        private static void ObserveCancelledLearnExit()
        {
            if (_active == null || _completionObserved || !_standardLearnFlowActive) return;
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                bool sameStock = work.pCurrentStock != null &&
                    work.pCurrentStock.Pointer == _active.Stock.Pointer;
                bool inside = seq.Current == 8 || seq.Current == 21 || seq.Current == 22;
                if (sameStock && inside)
                {
                    _cancelExitFrames = 0;
                    return;
                }
                if (++_cancelExitFrames >= CompletionQuietFramesRequired)
                    MarkActiveCancelled("learn flow exited without adding the mutation skill");
            }
            catch { }
        }

        private static bool IsInsideActiveLearnUi()
        {
            if (_active == null) return false;
            try
            {
                var work = rstinit.GBWK;
                var current = work.pCurrentStock;
                if (current == null || current.Pointer == IntPtr.Zero ||
                    current.Pointer != _active.Stock.Pointer) return false;
                sbyte seq = work.SeqInfo.Current;
                return seq == 8 || seq == 21 || seq == 22;
            }
            catch { return false; }
        }

        private static void MarkActiveCancelled(string source)
        {
            if (_active == null || _completionObserved) return;
            _activeCancelled = true;
            _completionObserved = true;
            _completionQuietFrames = 0;
            _standardLearnFlowActive = false;
            MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION queued learn cancellation observed; " +
                $"source={source} unit={_active.Stock.id} mutated={_active.MutatedSkill}.");
        }

        internal static void CorrectQueuedUiSelection()
        {
            if (_active == null || _completionObserved) return;
            try
            {
                IntPtr action = Marshal.ReadIntPtr(rstinit.GBWK.Pointer, 0x88);
                if (action == IntPtr.Zero) return;
                IntPtr actionData = Marshal.ReadIntPtr(action, 0x20);
                if (actionData == IntPtr.Zero) return;
                int cursor = Marshal.ReadByte(actionData, 0x14);
                if (cursor != _lastObservedUiCursor)
                {
                    _lastObservedUiCursor = cursor;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILL-MUTATION forget UI cursor; " +
                        $"unit={_active.Stock.id} cursor={cursor} " +
                        $"selected={rstinit.GBWK.SelectSkillID} mutated={_active.MutatedSkill}.");
                }
                if (cursor == MaxLearnedSkills)
                    rstinit.GBWK.SelectSkillID = _active.MutatedSkill;
                else if (cursor >= 0 && cursor < MaxLearnedSkills &&
                    cursor < _active.Stock.skillcnt && cursor < _active.Stock.skill.Length)
                    rstinit.GBWK.SelectSkillID =
                        unchecked((ushort)_active.Stock.skill[cursor]);
            }
            catch { }
        }

        private static bool HasSkill(Il2Cppnewdata_H.datUnitWork_t stock, ushort skill)
        {
            int count = Math.Min(stock.skillcnt, stock.skill.Length);
            for (int i = 0; i < count; i++) if (unchecked((ushort)stock.skill[i]) == skill) return true;
            return false;
        }

        private static sbyte[] CopyResultSkillProgress()
        {
            const int count = 6;
            var copy = new sbyte[count];
            IntPtr array = Marshal.ReadIntPtr(rstinit.GBWK.Pointer, 0x80);
            if (array == IntPtr.Zero) return copy;
            for (int i = 0; i < count; i++)
                copy[i] = unchecked((sbyte)Marshal.ReadByte(array, 0x20 + i));
            return copy;
        }

        private static sbyte[] CopyStockSkillProgress(
            Il2Cppnewdata_H.datUnitWork_t stock)
        {
            var copy = new sbyte[stock.skillparam.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = stock.skillparam[i];
            return copy;
        }

        private static void ClearStockSkillProgress(
            Il2Cppnewdata_H.datUnitWork_t stock)
        {
            for (int i = 0; i < stock.skillparam.Length; i++) stock.skillparam[i] = 0;
        }

        private static void ClearResultSkillProgress()
        {
            IntPtr array = Marshal.ReadIntPtr(rstinit.GBWK.Pointer, 0x80);
            if (array == IntPtr.Zero)
                throw new InvalidOperationException("result skill-progress array is unavailable");
            for (int i = 0; i < 6; i++) Marshal.WriteByte(array, 0x20 + i, 0);
            Marshal.WriteByte(rstinit.GBWK.Pointer, 0x7E, 0);
        }

        private static void RestoreSkillProgress(PendingMutation item)
        {
            int stockCount = Math.Min(item.SkillProgress.Length, item.Stock.skillparam.Length);
            for (int i = 0; i < stockCount; i++)
                item.Stock.skillparam[i] = item.SkillProgress[i];
            IntPtr array = Marshal.ReadIntPtr(rstinit.GBWK.Pointer, 0x80);
            if (array == IntPtr.Zero) return;
            int count = Math.Min(item.ResultSkillProgress.Length, 6);
            for (int i = 0; i < count; i++)
                Marshal.WriteByte(array, 0x20 + i,
                    unchecked((byte)item.ResultSkillProgress[i]));
            MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION learn progression restored; " +
                $"unit={item.Stock.id} stockEntries={stockCount} resultEntries={count}.");
        }

        private static void RestoreResultTarget(PendingMutation item)
        {
            rstinit.GBWK.TargetPos = item.TargetPos;
            rstinit.GBWK.TargetIndex = item.TargetIndex;
            rstinit.GBWK.TargetCnt = item.TargetCount;
            MelonLogger.Msg(
                "[NocturneModernGameplay] SKILL-MUTATION result target restored; " +
                $"unit={item.Stock.id} target={item.TargetPos}/{item.TargetIndex}/{item.TargetCount}.");
        }

        private static void FailActiveSafely(string reason)
        {
            if (_active != null)
            {
                if (!_active.IsInlineTransaction) RestoreSkillProgress(_active);
                RestoreReplacementSafely(_active.Stock, _active.Index, _active.MutatedSkill);
                MelonLogger.Warning("[NocturneModernGameplay] SKILL-MUTATION queued route fallback; " +
                    $"reason={reason} unit={_active.Stock.id} mutated={_active.MutatedSkill}.");
                ClearNativeLearnActionStateSafely("fail-safely");
                // Deliberately NOT calling RestorePriorWorkStockSafely here
                // yet - see the matching comment in CompleteActive.
            }
            _active = null;
            _lastObservedUiCursor = -1;
            _activeCancelled = false;
            _standardLearnFlowActive = false;
            _completionObserved = false;
            _completeAfterOuterUpdate = false;
            _drainContinuationRequested = false;

            int discarded = 0;
            while (Pending.Count > 0)
            {
                PendingMutation pending = Pending.Dequeue();
                RestoreReplacementSafely(pending.Stock, pending.Index, pending.MutatedSkill);
                discarded++;
            }
            _pendingHandoffRequested = false;
            MelonLogger.Warning(
                "[NocturneModernGameplay] PENDING-SCHEDULER discarded-after-failure; " +
                $"reason={reason} discarded={discarded} ownershipPresent={_drainSnapshot != null}.");
            RestoreNativeContext();
        }

        private static void FailAllSafely(string reason)
        {
            FailActiveSafely(reason);
        }

        private static void RestoreReplacementSafely(Il2Cppnewdata_H.datUnitWork_t stock, int index, ushort mutatedSkill)
        {
            try
            {
                if (stock != null && stock.Pointer != IntPtr.Zero && index >= 0 && index < stock.skill.Length)
                    stock.skill[index] = unchecked((short)mutatedSkill);
            }
            catch { }
        }

        // Called only from ResultLifecycleStartClearPatch (rstinit.rstCreateTargetList
        // Prefix). rstCreateTargetList rebuilding the target list is the clearest
        // available signal that a brand-new result lifecycle is starting, which is
        // why it is used as the clear trigger instead of any single-unit (A->B) or
        // mid-lifecycle sequence boundary (seq=19 is also used by queued-lifecycle
        // recovery and is not proof that nothing is still in flight). The guard
        // below additionally requires that no Learn-As-New state is currently
        // in flight, so a lifecycle that is still being processed is never cleared
        // out from under itself.
        internal static bool TryClearHandledSlotsAtLifecycleStart()
        {
            int previousCount = HandledSlots.Count;
            bool eligible = _active == null
                && Pending.Count == 0
                && !_completeAfterOuterUpdate
                && _drainSnapshot == null
                && !_drainContinuationRequested
                && !_applying
                && !_standardLearnFlowActive;

            if (eligible)
            {
                HandledSlots.Clear();
                OverwriteSuppressed.Clear();
            }

            MelonLogger.Msg(
                "[NocturneModernGameplay] SKILL-MUTATION handled-slots lifecycle-boundary; " +
                $"frame={UnityEngine.Time.frameCount} previousCount={previousCount} " +
                $"activePresent={_active != null} queueDepth={Pending.Count} " +
                $"completeAfterOuterUpdate={_completeAfterOuterUpdate} applying={_applying} " +
                $"ownershipPresent={_drainSnapshot != null} " +
                $"drainContinuationRequested={_drainContinuationRequested} " +
                $"standardLearnFlowActive={_standardLearnFlowActive} " +
                $"clear={(eligible ? "executed" : "skipped")}.");
            return eligible;
        }
    }

    [HarmonyPatch(typeof(datSkillName), nameof(datSkillName.Get),
        new[] { typeof(int), typeof(int) })]
    internal static class MutationQueuedSkillNamePatch
    {
        private static void Prefix(ref int __0, int __1)
        {
            SkillMutationLearnAsNew.ObserveQueuedUiGetter("name", __0, __1);
            SkillMutationLearnAsNew.OverrideQueuedUiSkillId(ref __0, __1);
        }
    }

    [HarmonyPatch(typeof(datSkillHelp_msg), nameof(datSkillHelp_msg.Get),
        new[] { typeof(int) })]
    internal static class MutationQueuedSkillHelpPatch
    {
        private static void Prefix(ref int __0)
        {
            SkillMutationLearnAsNew.ObserveQueuedUiGetter("help", __0);
            SkillMutationLearnAsNew.OverrideQueuedUiHelpSkillId(ref __0);
        }
    }

    // New-lifecycle boundary for HandledSlots. rstCreateTargetList is called to
    // (re)build the target list for a fresh batch of level-up/result processing,
    // which is the most direct confirmed signal available that a new result
    // lifecycle is beginning. The actual clear is guarded (see
    // TryClearHandledSlotsAtLifecycleStart) so it only takes effect when no
    // Learn-As-New state is in flight.
    [HarmonyPatch(typeof(rstinit), nameof(rstinit.rstCreateTargetList))]
    internal static class ResultLifecycleStartClearPatch
    {
        private static void Prefix()
        {
            if (SkillMutationLearnAsNew.TryClearHandledSlotsAtLifecycleStart())
                SkillMutationTelemetry.ClearCandidatesAtLifecycleStart();
        }
    }
}
