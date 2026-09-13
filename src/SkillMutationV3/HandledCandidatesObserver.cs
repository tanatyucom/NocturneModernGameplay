using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // OLD QUEUE LOOP SUPPRESSION ARCHAEOLOGY - HandledCandidates read-only
    // observation (step 1 of 2). Never writes any native field. Never
    // suppresses anything yet - this class only ANSWERS the question "would
    // a HandledSlots-style guard, keyed on (stock, candidateSkillId) instead
    // of the old (stock, slotIndex), have recognized this exact recurrence?"
    //
    // Background: the legacy Queue-era implementation (legacy/skillmutation/
    // SkillMutationLearnAsNew.LegacyFinal.cs, HandledSlots at line 116,
    // guard at line 1054-1057, comment at 664-669) fixed a real, shipped bug
    // where a cancelled/declined forget-UI outcome let the SAME slot
    // "re-roll" a fresh Mutation later in the same result lifecycle. Its key
    // was (stock.Pointer, slotIndex) because that bug was about a skill
    // occupying one of the 8 existing slots.
    //
    // Our current symptom is structurally analogous but the candidate
    // (e.g. skill 349, "Breath Armor") is NEVER present in stock.skill[] at
    // the moment of either forget-confirm episode (confirmed this session -
    // candidateSlot was -1->-1 in both the first, native-only decision and
    // the second, post-AddNew-bridge re-offer) - it is an offered curriculum
    // skill, not an occupied slot. So the natural identity key here is
    // (stock.Pointer, candidateSkillId) instead.
    //
    // This class hooks the SAME rstupdate.rstUpdateSeqDestroyConfirm Postfix
    // boundary DestroyConfirmEntryExitTrace already proved is the correct
    // "this forget-confirm episode has just resolved" moment (seqCurrent
    // transitions away from 22, typically to 8) - Harmony supports multiple
    // independent patches on one method, so this adds no new hook target.
    //
    // Release boundary (when HandledCandidates should be cleared) is
    // DELIBERATELY NOT IMPLEMENTED YET - per explicit instruction, this is a
    // read-only recurrence-check step first. rstinit.rstCreateTargetList
    // (confirmed still present as a private static method in the current
    // Il2Cpp Assembly-CSharp reference - see cpp2il_cs/DiffableCs/Assembly-
    // CSharp/rstinit.cs line 37) is the leading candidate for that boundary,
    // matching the old Queue's own "new result lifecycle" signal, but this
    // class does not yet hook it. For now the set simply accumulates for the
    // lifetime of the process (or until MOD reload) - fine for a short,
    // single-session read-only recurrence check.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDestroyConfirm))]
    internal static class HandledCandidatesObserver
    {
        internal static readonly bool Enabled = true;

        private static readonly HashSet<(long Stock, ushort SkillId)> HandledCandidates = new();
        private static readonly Dictionary<(long Stock, ushort SkillId), int> FirstSeenFrame = new();

        // ONE-SHOT CORE CONSUMPTION GUARD (suppression PoC). Keyed the same
        // as HandledCandidates. Initialized to false ONLY the first time a
        // (stock, skill) pair is added to HandledCandidates (i.e. the very
        // first forget-confirm resolution for that candidate) - critically,
        // NEVER reset on subsequent observations of the same key (a
        // re-offered candidate does not get a fresh consumption budget).
        // CoreReentryHandledCheck sets an entry to true exactly once, the
        // first time it allows rstCalcSkillPowerUpCore to run for that
        // candidate; every later Core call for the same candidate then sees
        // consumed=true and suppresses.
        private static readonly Dictionary<(long Stock, ushort SkillId), bool> CoreConsumed = new();

        // Read-only accessor for CoreReentryHandledCheck - does not allow
        // writing, only checking membership from elsewhere.
        internal static bool IsHandled(long stockPtr, ushort skillId) =>
            HandledCandidates.Contains((stockPtr, skillId));

        internal static bool IsCoreConsumed(long stockPtr, ushort skillId) =>
            CoreConsumed.TryGetValue((stockPtr, skillId), out bool consumed) && consumed;

        // Marks the one-shot Core opportunity as used. Only meaningful for a
        // key already present (added via HandledCandidates.Add below); a
        // no-op otherwise (defensive - CoreReentryHandledCheck should only
        // call this after confirming IsHandled==true).
        internal static void MarkCoreConsumed(long stockPtr, ushort skillId)
        {
            CoreConsumed[(stockPtr, skillId)] = true;
        }

        // Read-only accessor for ResultLifecycleBoundaryObserver - reports
        // current set size only, never clears or mutates anything.
        internal static int DebugCount => HandledCandidates.Count;

        // SUPPRESSION POC: clear boundary, wired to rstinit.rstCreateTargetList
        // (see ResultLifecycleBoundaryObserver). Confirmed this session (one
        // real-machine case) NOT to fire mid-sequence during a full
        // decline -> Power-Up -> AddNew -> re-offer cycle, only once at an
        // early, pre-battle-processing point - matching the old Queue's own
        // "new result lifecycle" characterization of this same native call.
        // Still logged every time it actually clears something, since
        // confidence is STRONGLY SUPPORTED rather than exhaustively proven
        // across every possible scenario.
        internal static (int HandledCleared, int ConsumedCleared) ClearForNewLifecycle()
        {
            int handledCleared = HandledCandidates.Count;
            int consumedCleared = CoreConsumed.Count;
            HandledCandidates.Clear();
            FirstSeenFrame.Clear();
            CoreConsumed.Clear();
            return (handledCleared, consumedCleared);
        }

        private static int _seqCurrentBefore = int.MinValue;
        private static long _stockPtrBefore;
        private static ushort _eventParamBefore;
        private static int _unitBefore;
        private static bool _captured;

        // MEASUREMENT-CONTAMINATION FIX (found via real-machine testing):
        // identity MUST be captured at Prefix (entry), not Postfix (exit).
        // A bridge transaction for a DIFFERENT target (e.g. AddNew-ing
        // skill 10 while the true native-pending curriculum skill is 349)
        // restores GBWK.EventParam back to that true pending value (349)
        // INSIDE this same rstUpdateSeqDestroyConfirm call (via
        // FullCapacityAddNewBridgeInsertionInjector's Postfix on the nested
        // rstAddSkill call) - so reading EventParam at Postfix time could
        // read the RESTORED value (349) even though this entire
        // destroy-confirm episode was actually about a completely different
        // candidate (10). Reading it at Prefix time (before any such nested
        // restore can occur) correctly identifies which candidate this
        // episode was ACTUALLY deciding about.
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

                _seqCurrentBefore = gbwk.SeqInfo.Current;
                _stockPtrBefore = stock.Pointer.ToInt64();
                _eventParamBefore = gbwk.EventParam;
                _unitBefore = stock.id;
                _captured = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HandledCandidatesObserver prefix failed safely: {ex.Message}");
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

                int seqCurrentAfter = gbwk.SeqInfo.Current;
                // Only the exact "this forget-confirm episode just resolved"
                // boundary matters here - the same one DestroyConfirmEntryExitTrace
                // established (seqCurrent leaving 22, typically to 8).
                if (_seqCurrentBefore != 22 || seqCurrentAfter == 22) return;

                ushort eventParam = _eventParamBefore;
                if (eventParam == 0) return;

                long stockPtr = _stockPtrBefore;
                var key = (stockPtr, eventParam);

                bool wouldSuppress = HandledCandidates.Contains(key);
                int frame = UnityEngine.Time.frameCount;

                if (!FirstSeenFrame.TryGetValue(key, out int firstSeenFrame))
                {
                    firstSeenFrame = frame;
                    FirstSeenFrame[key] = firstSeenFrame;
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] HANDLEDCANDIDATES-CHECK; " +
                    $"frame={frame}; unit={_unitBefore}; stockPtr=0x{stockPtr:X}; " +
                    $"eventParam={eventParam}; wouldSuppress={wouldSuppress}; " +
                    $"firstSeenFrame={firstSeenFrame}; framesSinceFirstSeen={frame - firstSeenFrame}; " +
                    $"seqCurrentAfter={seqCurrentAfter}; setSize={HandledCandidates.Count}; " +
                    "lifecycleId=NOT-YET-WIRED (rstCreateTargetList boundary not implemented).");

                // Record AFTER logging, so the very first occurrence for
                // this (stock, skill) correctly reports wouldSuppress=False.
                // HashSet.Add returns true only the FIRST time - CoreConsumed
                // is seeded to false ONLY on that first add, and is never
                // touched again here on subsequent (recurrence) observations
                // of the same key, per the one-shot guard's own requirement.
                if (HandledCandidates.Add(key))
                {
                    CoreConsumed[key] = false;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HandledCandidatesObserver postfix failed safely: {ex.Message}");
            }
        }
    }
}
