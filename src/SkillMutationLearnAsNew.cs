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
        }

        private static readonly Queue<PendingMutation> Pending = new();
        private static readonly HashSet<string> UiGetterObservations = new();
        // Disabled while the standalone "Always" diagnostic is active. The
        // unfinished queued full-skill route must not alter its result state.
        private static bool _enabled = true;
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
        private static sbyte _settledTargetPos;
        private static sbyte _settledTargetIndex;
        private static sbyte _settledTargetCount;
        private static bool _completeAfterOuterUpdate;
        private static bool _experimentalInlineActive;
        private static Il2Cppnewdata_H.datUnitWork_t? _settledCurrentStock;
        private static Il2Cppnewdata_H.datUnitWork_t? _settledWorkStock;

        internal static bool IsApplying => _applying;
        internal static bool SuppressNestedMutation => _active != null;
        internal static bool CheckHasSkill(Il2Cppnewdata_H.datUnitWork_t stock, ushort skill) => HasSkill(stock, skill);
        internal static ushort ActiveOriginalSkill => _active?.OriginalSkill ?? 0;
        internal static ushort ActiveMutatedSkill => _active?.MutatedSkill ?? 0;
        internal static int ActiveIndex => _active?.Index ?? -1;

        // Read-only exposure for unified telemetry. Adds no behavior; these
        // properties only surface existing private state for logging.
        internal static string ActiveSummary =>
            _active == null
                ? "none"
                : $"unit={_active.Stock.id} index={_active.Index} " +
                  $"original={_active.OriginalSkill} mutated={_active.MutatedSkill} " +
                  $"completionObserved={_completionObserved} completeAfterOuterUpdate={_completeAfterOuterUpdate}";
        internal static int HandledSlotCount => HandledSlots.Count;
        internal static bool IsSlotHandled(IntPtr stock, int index) => HandledSlots.Contains((stock, index));
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
            if (!_enabled || _active != null || Pending.Count == 0) return;
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                if (seq.Current != 11 ||
                    work.TargetIndex != work.TargetCnt + 1) return;
                _settledTargetPos = work.TargetPos;
                _settledTargetIndex = work.TargetIndex;
                _settledTargetCount = work.TargetCnt;
                _settledCurrentStock = work.pCurrentStock;
                _settledWorkStock = work.WorkStock;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION last-demon boundary captured before fade; " +
                    $"target={work.TargetPos}/{work.TargetIndex}/{work.TargetCnt} queue={Pending.Count}.");
                StartNext();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Could not start queued mutations at result boundary: {ex.Message}");
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
                    // Calc is about to process the next scheduled learn. Settle
                    // this mutation synchronously so it cannot suppress that
                    // unrelated capacity check. A later Update-side call sees
                    // no active item and is therefore a no-op.
                    _completeAfterOuterUpdate = false;
                    Log("executed");
                    CompleteActive(continueQueuedLifecycle: false);
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

        internal static void TryConvertReplacementToAddition(Il2Cppnewdata_H.datUnitWork_t stock,
            int index, ushort originalSkill, ushort mutatedSkill)
        {
            if (!_enabled || _applying || stock == null || stock.Pointer == IntPtr.Zero ||
                HandledSlots.Contains((stock.Pointer, index)) ||
                originalSkill == 0 || mutatedSkill == 0 || index < 0 ||
                index >= stock.skill.Length || unchecked((ushort)stock.skill[index]) != mutatedSkill) return;

            _applying = true;
            // Registered up front, before either route below runs, and never
            // removed on cancellation: this (stock, slot) is now considered
            // handled for the remainder of the current result lifecycle.
            HandledSlots.Add((stock.Pointer, index));
            try
            {
                stock.skill[index] = unchecked((short)originalSkill);
                if (stock.skillcnt >= MaxLearnedSkills)
                {
                    var item = new PendingMutation { Stock = stock, Index = index,
                        OriginalSkill = originalSkill, MutatedSkill = mutatedSkill,
                        TargetPos = rstinit.GBWK.TargetPos,
                        TargetIndex = rstinit.GBWK.TargetIndex,
                        TargetCount = rstinit.GBWK.TargetCnt,
                        SkillProgress = CopyStockSkillProgress(stock),
                        ResultSkillProgress = CopyResultSkillProgress() };

                    if (ExperimentalInlineRepro.Enabled)
                    {
                        StartExperimentalInline(item);
                        return;
                    }

                    Pending.Enqueue(item);
                    MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION learn-as-new-full queued; " +
                        $"unit={stock.id} index={index} original={originalSkill} mutated={mutatedSkill} queueDepth={Pending.Count}.");
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

        private static void StartExperimentalInline(PendingMutation item)
        {
            if (_active != null)
                throw new InvalidOperationException(
                    $"inline transaction collision with active [{ActiveSummary}]");

            _active = item;
            _experimentalInlineActive = true;
            _completionObserved = false;
            _completeAfterOuterUpdate = false;
            _activeCancelled = false;
            _cancelExitFrames = 0;
            _lastObservedUiCursor = -1;
            _completionQuietFrames = 0;
            item.WaitFrames = 0;

            MelonLogger.Warning(
                "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO Learn-As-New start; " +
                $"frame={UnityEngine.Time.frameCount} unit={item.Stock.id} " +
                $"candidateKey=0x{item.Stock.Pointer.ToInt64():X}:{item.Index} " +
                $"original={item.OriginalSkill} mutated={item.MutatedSkill} " +
                $"currentUnit={GetCurrentUnitId()} workUnit={GetWorkUnitId()} " +
                $"currentStockPtr=0x{GetCurrentStockPointer().ToInt64():X} " +
                $"workStockPtr=0x{GetWorkStockPointer().ToInt64():X}.");

            // We are running from rstUpdateSeqSkillPowerUp's Postfix, after the
            // native overwrite for this same unit. Reuse the already-established
            // forget-flow initializer immediately instead of enqueueing and later
            // rebinding another unit's WorkStock/current stock.
            StartPreparedForgetFlow();
        }

        private static void StartNext()
        {
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
                rstinit.GBWK.pCurrentStock = item.Stock;
                rstinit.GBWK.WorkStock = item.Stock;
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

        private static void CompleteActive(bool continueQueuedLifecycle = true)
        {
            PendingMutation item = _active!;
            if (_activeCancelled)
            {
                RestoreSkillProgress(item);
                LogHasSkillState("cancel", item);
                MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION queued learn cancelled; " +
                    $"unit={item.Stock.id} original={item.OriginalSkill} " +
                    $"mutated={item.MutatedSkill} remainingQueue={Pending.Count}.");
                FinishActiveAndContinueQueue(continueQueuedLifecycle);
                return;
            }
            bool mutatedPresent = HasSkill(item.Stock, item.MutatedSkill);
            if (!mutatedPresent)
            {
                LogHasSkillState("completion-verification-failed", item);
                FailActiveSafely("completion verification failed (mutated skill was not learned)");
                return;
            }
            RestoreSkillProgress(item);
            LogHasSkillState("completed", item);
            MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION queued-full-route completed; " +
                $"unit={item.Stock.id} original={item.OriginalSkill} mutated={item.MutatedSkill} remainingQueue={Pending.Count}.");
            FinishActiveAndContinueQueue(continueQueuedLifecycle);
        }

        private static void FinishActiveAndContinueQueue(bool continueQueuedLifecycle = true)
        {
            bool completedInlineExperiment = _experimentalInlineActive;
            _active = null;
            _experimentalInlineActive = false;
            _lastObservedUiCursor = -1;
            _activeCancelled = false;
            _completionObserved = false;
            _standardLearnFlowActive = false;
            if (completedInlineExperiment)
            {
                MelonLogger.Msg(
                    "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO Learn-As-New completed inline; " +
                    $"frame={UnityEngine.Time.frameCount} queueDepth={Pending.Count}.");
            }
            if (!continueQueuedLifecycle)
            {
                // rstCalc is already advancing the next native learn event.
                // Leave any remaining queued mutations pending until the
                // established result-boundary starter can safely run them.
                return;
            }
            if (completedInlineExperiment)
            {
                return;
            }
            if (Pending.Count > 0)
            {
                StartNext();
                return;
            }
            ResumeSettledResultBoundary();
        }

        private static void ResumeSettledResultBoundary()
        {
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                work.TargetPos = _settledTargetPos;
                work.TargetIndex = _settledTargetIndex;
                work.TargetCnt = _settledTargetCount;
                if (_settledCurrentStock != null &&
                    _settledCurrentStock.Pointer != IntPtr.Zero)
                    work.pCurrentStock = _settledCurrentStock;
                if (_settledWorkStock != null &&
                    _settledWorkStock.Pointer != IntPtr.Zero)
                    work.WorkStock = _settledWorkStock;
                // Resume the exact last-demon boundary that was intercepted.
                // The native result update can then perform its own fade/exit.
                seq.Current = 11;
                seq.Next = -1;
                seq.Last = 10;
                seq.Change = 1;
                work.SelectSkillID = 0;
                work.PUpSkillID = 0;
                work.PUpSkillIndex = 0;
                work.PUpSkillResult = 0;
                work.DefSkillResult = 0;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION native result boundary resumed; " +
                    $"target={work.TargetPos}/{work.TargetIndex}/{work.TargetCnt}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Could not resume settled result boundary: {ex.Message}");
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
                RestoreSkillProgress(_active);
                RestoreReplacementSafely(_active.Stock, _active.Index, _active.MutatedSkill);
                MelonLogger.Warning("[NocturneModernGameplay] SKILL-MUTATION queued route fallback; " +
                    $"reason={reason} unit={_active.Stock.id} mutated={_active.MutatedSkill}.");
            }
            _active = null;
            _lastObservedUiCursor = -1;
            _activeCancelled = false;
            _standardLearnFlowActive = false;
            _completionObserved = false;
            _completeAfterOuterUpdate = false;
            _experimentalInlineActive = false;
        }

        private static int GetCurrentUnitId()
        {
            try
            {
                var stock = rstinit.GBWK.pCurrentStock;
                return stock == null || stock.Pointer == IntPtr.Zero ? -1 : stock.id;
            }
            catch { return -1; }
        }

        private static int GetWorkUnitId()
        {
            try
            {
                var stock = rstinit.GBWK.WorkStock;
                return stock == null || stock.Pointer == IntPtr.Zero ? -1 : stock.id;
            }
            catch { return -1; }
        }

        private static IntPtr GetCurrentStockPointer()
        {
            try { return rstinit.GBWK.pCurrentStock?.Pointer ?? IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        private static IntPtr GetWorkStockPointer()
        {
            try { return rstinit.GBWK.WorkStock?.Pointer ?? IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        private static void FailAllSafely(string reason)
        {
            FailActiveSafely(reason);
            while (Pending.Count > 0)
            {
                PendingMutation item = Pending.Dequeue();
                RestoreReplacementSafely(item.Stock, item.Index, item.MutatedSkill);
            }
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
        internal static void TryClearHandledSlotsAtLifecycleStart()
        {
            int previousCount = HandledSlots.Count;
            bool eligible = _active == null
                && Pending.Count == 0
                && !_completeAfterOuterUpdate
                && !_applying
                && !_standardLearnFlowActive;

            if (eligible)
            {
                HandledSlots.Clear();
                SkillMutationTelemetry.ClearExperimentalCandidatesAtLifecycleStart();
            }

            MelonLogger.Msg(
                "[NocturneModernGameplay] SKILL-MUTATION handled-slots lifecycle-boundary; " +
                $"frame={UnityEngine.Time.frameCount} previousCount={previousCount} " +
                $"activePresent={_active != null} queueDepth={Pending.Count} " +
                $"completeAfterOuterUpdate={_completeAfterOuterUpdate} applying={_applying} " +
                $"standardLearnFlowActive={_standardLearnFlowActive} " +
                $"clear={(eligible ? "executed" : "skipped")}.");
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
            SkillMutationLearnAsNew.TryClearHandledSlotsAtLifecycleStart();
        }
    }
}
