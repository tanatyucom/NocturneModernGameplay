using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // OLD QUEUE LOOP SUPPRESSION ARCHAEOLOGY - ONE-SHOT CORE CONSUMPTION
    // GUARD (suppression PoC).
    //
    // Background: HandledCandidatesObserver confirmed, via the
    // rstUpdateSeqDestroyConfirm boundary, that the SAME (stockPtr,
    // EventParam) candidate genuinely recurs within one result lifecycle
    // (wouldSuppress=True observed for skill 349, ~28s / ~1700 frames after
    // its first decision, same session). CoreReentryHandledCheck's earlier
    // read-only form then confirmed rstcalc.rstCalcSkillPowerUpCore ALSO
    // fires again on that recurrence and produces a fresh, legitimate-
    // looking result (coreResult=2, a brand-new Mutation candidate/PUpSkillID
    // roll) - proving that suppressing only the FullCapacityAddNew bridge
    // would be insufficient; the plain native Mutation/overwrite path could
    // still fire a second time on an already-decided candidate.
    //
    // Guard shape (explicit, per instruction - NOT "handled implies
    // suppress"): every (stock, skill) pair that ever becomes "handled" also
    // gets exactly ONE free Core opportunity, tracked separately
    // (HandledCandidatesObserver.CoreConsumed). The first Core call for a
    // freshly-handled candidate is ALLOWED (this is the ordinary, legitimate
    // Power-Up/Mutation roll that follows every forget decision in the SAME
    // initial pass - not a bug) and marks that opportunity consumed. Only a
    // LATER Core call for the same still-consumed candidate is suppressed.
    // CoreConsumed is never reset except at the rstCreateTargetList lifecycle
    // boundary (ResultLifecycleBoundaryObserver).
    //
    // Suppression mechanism: Prefix sets __result=0 (native's own "no
    // result" code - a value it already produces routinely) and returns
    // false, skipping rstCalcSkillPowerUpCore's native body entirely, rather
    // than letting it run and zeroing __result in Postfix - the native call
    // already writes PUpSkillID/PUpSkillIndex/etc as side effects (confirmed
    // this session: pUpId 4->54 on the very reentrant call under test), and
    // those must not happen at all for a suppressed call, not merely be
    // masked afterward. PUpSkillID/PUpSkillIndex/PUpSkillResult are never
    // touched directly by this class either way - only __result is set on
    // the suppressed path.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class CoreReentryHandledCheck
    {
        internal static readonly bool Enabled = true;

        private static bool _captured;
        private static long _stockPtrBefore;
        private static int _unitBefore;
        private static ushort _eventParamBefore;
        private static string _action = "";
        private static int _seqBefore;
        private static ushort _pUpIdBefore;
        private static sbyte _pUpIndexBefore;

        private static bool Prefix(ref sbyte __result)
        {
            _captured = false;
            if (!Enabled) return true;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return true;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return true;

                long stockPtr = stock.Pointer.ToInt64();
                ushort eventParam = gbwk.EventParam;

                _stockPtrBefore = stockPtr;
                _unitBefore = stock.id;
                _eventParamBefore = eventParam;
                _seqBefore = gbwk.SeqInfo.Current;
                _pUpIdBefore = gbwk.PUpSkillID;
                _pUpIndexBefore = gbwk.PUpSkillIndex;
                _captured = true;

                // EPISODE LATCH (2026-09-17 root-cause fix): a Power-Up or
                // Mutation already succeeded for this unit earlier in the
                // same level-up episode - unconditionally suppress, REGARDLESS
                // of which candidate this call is about. This is the gate
                // that closes the gap ALLOW-FIRST leaves below (see
                // HandledCandidatesObserver's EPISODE-LEVEL SUCCESS LATCH
                // comment for the full real-machine evidence).
                if (HandledCandidatesObserver.IsEpisodeLatched(stockPtr))
                {
                    __result = 0;
                    _action = "SUPPRESS-EPISODE-LATCH";

                    int frameEpisodeSuppress = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] CORE-EPISODE-LATCH; " +
                        $"frame={frameEpisodeSuppress}; unit={_unitBefore}; stockPtr=0x{stockPtr:X}; " +
                        $"candidate={eventParam}; action=SUPPRESS-EPISODE-LATCH.");
                    return false;
                }

                bool handled = HandledCandidatesObserver.IsHandled(stockPtr, eventParam);
                if (!handled)
                {
                    _action = "PASSTHROUGH-NOT-HANDLED";
                    return true; // never seen this candidate reach a forget-confirm resolution - not our concern
                }

                bool consumed = HandledCandidatesObserver.IsCoreConsumed(stockPtr, eventParam);
                if (!consumed)
                {
                    // First legitimate Core opportunity for this candidate -
                    // this is the ordinary roll that follows every forget
                    // decision, not a reentry. Allow it, and mark the
                    // one-shot opportunity used so a LATER call for the same
                    // candidate is suppressed instead.
                    HandledCandidatesObserver.MarkCoreConsumed(stockPtr, eventParam);
                    _action = "ALLOW-FIRST";

                    int frameAllow = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] CORE-CONSUMPTION; " +
                        $"frame={frameAllow}; unit={_unitBefore}; stockPtr=0x{stockPtr:X}; " +
                        $"candidate={eventParam}; action=ALLOW-FIRST.");
                    return true;
                }

                // Already handled AND the one-shot opportunity was already
                // used - this is a genuine reentry. Suppress: skip the
                // native body entirely (do not let it write PUpSkillID/
                // PUpSkillIndex/etc as a side effect), return native's own
                // routine "no result" code.
                __result = 0;
                _action = "SUPPRESS-REENTRY";

                int frameSuppress = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] CORE-CONSUMPTION; " +
                    $"frame={frameSuppress}; unit={_unitBefore}; stockPtr=0x{stockPtr:X}; " +
                    $"candidate={eventParam}; action=SUPPRESS-REENTRY.");
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] CoreReentryHandledCheck prefix failed safely: {ex.Message}");
                return true;
            }
        }

        private static void Postfix(sbyte __result)
        {
            if (!_captured) return;
            _captured = false;
            try
            {
                // coreResult 1 = ordinary Power-Up, 2 = Mutation success (3 =
                // Mutation failure, 0 = no candidate) - either success arms
                // the episode latch so any further Core call for this unit,
                // for ANY candidate, is suppressed until a genuinely new
                // level-up is observed (see HandledCandidatesObserver).
                if (__result == 1 || __result == 2)
                {
                    HandledCandidatesObserver.MarkEpisodeSkillChangeApplied(_stockPtrBefore);
                }

                // Only log the ALLOW/SUPPRESS-relevant cases plus any
                // meaningful (non-zero) result - avoids flooding the log
                // with every ordinary, unrelated Core call.
                if (_action == "PASSTHROUGH-NOT-HANDLED" && __result == 0) return;

                var gbwk = rstinit.GBWK;
                int frame = UnityEngine.Time.frameCount;
                int seqAfter = gbwk?.SeqInfo.Current ?? -1;
                ushort pUpIdAfter = gbwk?.PUpSkillID ?? (ushort)0;
                sbyte pUpIndexAfter = gbwk?.PUpSkillIndex ?? 0;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] CORE-REENTRY-HANDLED-CHECK; " +
                    $"frame={frame}; unit={_unitBefore}; stockPtr=0x{_stockPtrBefore:X}; " +
                    $"eventParam={_eventParamBefore}; action={_action}; " +
                    $"coreResult={__result}; " +
                    $"seq {_seqBefore}->{seqAfter}; " +
                    $"pUpId {_pUpIdBefore}->{pUpIdAfter}; pUpIndex {_pUpIndexBefore}->{pUpIndexAfter}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] CoreReentryHandledCheck postfix failed safely: {ex.Message}");
            }
        }
    }
}
