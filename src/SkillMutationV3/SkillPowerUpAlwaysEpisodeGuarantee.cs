using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // SKILLPOWERUP.CHANCE=ALWAYS PER-EPISODE GUARANTEE (2026-09-19).
    //
    // Official semantics (per explicit user instruction, "SkillPowerUp
    // Always Semantics Fix v2"): SkillPowerUp.Chance=Always means "as long
    // as at least one valid Skill Change (ordinary Power-Up OR genuine
    // Mutation) exists for this unit, this level-up episode grants exactly
    // one." It does NOT mean "native's own internal dil coin-flip always
    // lands on Mutation" - SkillMutationChanceControl's Always patches only
    // affect that coin-flip, and that coin-flip is never reached at all
    // when native's own EARLY exclusion check (see below) bails out first.
    //
    // Root cause this class addresses (CONFIRMED via static disassembly +
    // real-machine ALWAYSDIL-PROBE/OPTION-F-DECISION correlation, unit=91
    // "Kahaku", same save/same conditions, both outcomes observed):
    //   rstCalcSkillPowerUpCore (VA 0x18227E100):
    //     1. rstRndGetPowerUpSkill(pStock, &PUpSkillIndex) -> PUpSkillID
    //        (VA 0x18227E15A/0x18227E15F) - an UNCONDITIONAL, EARLY roll,
    //        scanning stock.skill[] and picking one skill's upgrade target
    //        via cmbGetPowerUpSkill, entirely before dil is ever decided.
    //     2. rstCreateBeforeSkillList(...) (VA 0x18227E261) builds a
    //        "before list" (curriculum entries already due at this unit's
    //        level) - UNCONDITIONALLY, also before dil.
    //     3. If PUpSkillID matches an entry in that list (VA 0x18227E293),
    //        Core returns rawResult=0 IMMEDIATELY - the dil roll (and with
    //        it, every SkillMutation.Chance=Always patch) is never reached.
    //   This is architecturally independent of dil/Mutation entirely - it
    //   is native's own "this candidate is already handled by the ordinary
    //   curriculum path, don't also grant it via Power-Up" guard, colliding
    //   with whichever of the unit's 8 owned skills the early RNG happened
    //   to pick this invocation.
    //
    // Design (Phase A / Phase B, matching the instruction document):
    //   Phase A - Power-Up candidate retry: this unit's own achievable
    //     Power-Up target pool is precomputed deterministically (scan
    //     stock.skill[], call cmbGetPowerUpSkill per owned skill, drop
    //     already-owned targets - the same check DuplicateTargetPowerUpBlock
    //     already applies). rstCalcSkillPowerUpCore is re-invoked (a full,
    //     independent, Harmony-wrapped call - NOT a hand-rolled substitute)
    //     until either it succeeds, or every DISTINCT candidate in the
    //     precomputed pool has been observed and rejected - never a blind
    //     fixed-N-attempts guess, per explicit instruction.
    //   Phase B - Mutation fallback: only once Phase A's pool is proven
    //     empty or exhausted. Tries cmbGetMutationSkill(ownedSkillId, stock)
    //     directly for each of the unit's owned skills (native's own
    //     function already performs its own internal random search per
    //     call - see MutationCandidateDiagnostics's own header comment).
    //     First nonzero result wins: PUpSkillIndex/PUpSkillID are set to
    //     match exactly what native's own dil=1 success tail would have
    //     written (VA 0x18227E597/0x18227E59B - "PUpSkillIndex keeps the
    //     ORIGINAL owned-skill slot, PUpSkillID becomes the mutation
    //     target, __result=2"), and bit6 is deliberately left untouched
    //     (native's own asymmetry: bit6 |= 0x40 only happens on the
    //     ordinary/result=1 tail, VA 0x18227E4E8 - never on Mutation).
    //   Phase C - genuinely no candidate: __result stays 0, unchanged.
    //
    // Harmony recursion safety (audited this session, all patches on
    // rstCalcSkillPowerUpCore): CoreReentryHandledCheck, OptionFRepeatUnlimitedControl,
    // SkillPowerUpChanceControl's SkillPowerUpChanceAlwaysPatch, and
    // MutationDisabledBit6Guard all reset their own per-invocation state in
    // their own Prefix and consume it in their own Postfix - each is safe
    // under a full nested re-invocation (exactly like native calling Core
    // again on a later frame, just compressed into fewer frames).
    // SkillMutationAlways/OptionFF2Diagnostics/PowerUpMutationCfgDiagnostics/
    // SkillWriterBoundaryTrace are all confirmed dead/disabled this session
    // (Enabled=false or never Initialize()'d) and irrelevant here. This
    // class itself uses `_retryInProgress` so its OWN Postfix, triggered
    // again by each nested retry call, never recursively starts a second
    // retry loop - only the OUTERMOST invocation drives the loop, reading
    // each nested call's return value directly (Harmony returns the
    // Postfix-adjusted __result as the method's own normal return value).
    //
    // [HarmonyPriority(Priority.First)]: this Postfix must see the FINAL
    // __result after every other Always-related conversion has already had
    // its chance (SkillPowerUpChanceAlwaysPatch's 2/3->1 conversion,
    // OptionFRepeatUnlimitedControl's R0-C->1 conversion) - it only acts
    // when __result is STILL 0 after all of that.
    //
    // REGRESSION FOUND AND FIXED 2026-09-19 (real-machine: unit=97, native
    // rolled PUpSkillID=0 "Reserve" placeholder name, R0-A, Phase B chose a
    // genuine Mutation fallback target=409 and set __result=2 - but the
    // player then saw "Reserve" (skill 0) actually granted, and seq never
    // reached 10/SkillPowerUp). Root cause: HarmonyLib runs Postfixes in
    // REVERSED priority order relative to Prefixes (a Normal-priority
    // Postfix runs AFTER a Last-priority one, not before - confirmed
    // empirically via MelonLoader log ordering, "SkillPowerUpChance Always
    // priority applied; restoredSkillId=0" logged immediately AFTER this
    // class's own ALWAYS-EPISODE-GUARANTEE line). With the originally-used
    // Priority.Last, THIS class's Postfix ran FIRST among all postfixes on
    // rstCalcSkillPowerUpCore, so SkillPowerUpChanceAlwaysPatch's
    // Normal-priority Postfix then ran AFTER it, saw the freshly-set
    // __result==2 from Phase B, and "helpfully" restored PUpSkillID to the
    // ORIGINAL (pre-fallback) captured candidate - which was 0, since the
    // native roll that triggered Phase B in the first place had no
    // candidate at all - silently corrupting Phase B's chosen Mutation
    // target back to skill ID 0. Priority.First makes this class's own
    // Postfix run LAST (the true final word), matching the intent this
    // comment already described.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    [HarmonyPriority(Priority.First)]
    internal static class SkillPowerUpAlwaysEpisodeGuarantee
    {
        internal static readonly bool Enabled = true;

        // Generous multiplier over the precomputed distinct-candidate pool
        // size, purely as a defensive upper bound against pathological RNG
        // behavior or a transient failure to read PUpSkillID on some
        // attempt - the real stopping condition is "every distinct
        // candidate has been observed", not this cap.
        private const int RetryCapMultiplier = 6;
        private const int RetryCapMinimum = 12;

        private static bool _retryInProgress;

        private static void Postfix(ref sbyte __result)
        {
            if (!Enabled) return;
            if (_retryInProgress) return; // this invocation IS one of our own retries
            if (__result != 0) return; // native (or an earlier Always conversion) already succeeded
            if (SkillPowerUpChanceControl.Mode != NativeChanceMode.Always) return;

            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;

                long stockPtr = stock.Pointer.ToInt64();

                // REGRESSION FOUND AND FIXED 2026-09-19 ("zombie" recurrence,
                // real-machine: unit=92, ALWAYS-EPISODE-GUARANTEE fired 4
                // additional times within the SAME already-latched episode,
                // each producing a genuine extra Skill Change/forget-UI
                // cycle - the exact symptom the episode latch exists to
                // prevent). Root cause: CoreReentryHandledCheck.Prefix
                // correctly SUPPRESSes native re-entry once the episode
                // latch is SET (forcing __result=0 itself, action=
                // SUPPRESS-EPISODE-LATCH) - but this class's own Postfix
                // never checked that latch before treating __result==0 as
                // "genuinely exhausted, try Phase A/B", so it kept
                // constructing brand-new fallback successes on every
                // subsequent per-frame re-invocation while the AddNew/
                // forget-UI screen was still open. Must gate on the SAME
                // latch CoreReentryHandledCheck already respects.
                if (HandledCandidatesObserver.IsEpisodeLatched(stockPtr)) return;

                int unit = stock.id;
                int frame = UnityEngine.Time.frameCount;
                ushort originalPUpSkillId = gbwk.PUpSkillID;
                bool episodeLatchBefore = false;

                var ownedSkills = CollectOwnedSkills(stock);

                // Phase A: precompute this unit's distinct achievable
                // ordinary Power-Up target pool (already-owned targets
                // dropped - same check as DuplicateTargetPowerUpBlock).
                var rawPowerUpCandidates = new HashSet<ushort>();
                var ownedBlocked = new HashSet<ushort>();
                foreach (ushort ownedSkill in ownedSkills)
                {
                    ushort target = rstCalcCore.cmbGetPowerUpSkill(ownedSkill);
                    if (target == 0) continue;
                    if (ownedSkills.Contains(target))
                    {
                        ownedBlocked.Add(target);
                        continue;
                    }
                    rawPowerUpCandidates.Add(target);
                }

                string fallbackType = "none";
                ushort fallbackTarget = 0;
                sbyte finalResult = __result;
                int excludedCount = 0;

                if (rawPowerUpCandidates.Count > 0)
                {
                    var seenCandidates = new HashSet<ushort>();
                    int cap = Math.Max(RetryCapMinimum, rawPowerUpCandidates.Count * RetryCapMultiplier);

                    _retryInProgress = true;
                    try
                    {
                        for (int attempt = 0;
                             attempt < cap && !seenCandidates.IsSupersetOf(rawPowerUpCandidates);
                             attempt++)
                        {
                            sbyte retryResult = rstcalc.rstCalcSkillPowerUpCore();
                            ushort attemptedCandidate = rstinit.GBWK?.PUpSkillID ?? 0;
                            if (attemptedCandidate != 0 && rawPowerUpCandidates.Contains(attemptedCandidate))
                            {
                                seenCandidates.Add(attemptedCandidate);
                            }

                            if (retryResult != 0)
                            {
                                finalResult = retryResult;
                                fallbackType = "powerup-alternate";
                                fallbackTarget = rstinit.GBWK?.PUpSkillID ?? 0;
                                break;
                            }
                        }
                    }
                    finally
                    {
                        _retryInProgress = false;
                    }

                    excludedCount = seenCandidates.Count;
                }

                int mutationCandidateCount = 0;
                if (fallbackType == "none")
                {
                    // Phase B: Power-Up pool empty or fully exhausted -
                    // try genuine Mutation directly, per owned skill, in
                    // slot order. cmbGetMutationSkill already performs its
                    // own internal random search per call.
                    for (int i = 0; i < stock.skillcnt && i < stock.skill.Length; i++)
                    {
                        ushort ownedSkillId = unchecked((ushort)stock.skill[i]);
                        if (ownedSkillId == 0) continue;

                        ushort mutationResult = rstCalcCore.cmbGetMutationSkill(ownedSkillId, stock);
                        if (mutationResult == 0) continue;

                        mutationCandidateCount++;

                        if (fallbackType == "none")
                        {
                            gbwk.PUpSkillIndex = (sbyte)i;
                            gbwk.PUpSkillID = mutationResult;
                            finalResult = 2;
                            fallbackType = "mutation";
                            fallbackTarget = mutationResult;
                            // Keep SkillPowerUpChanceCandidateCapture's own
                            // "last original candidate" field in sync with
                            // OUR fallback target (see its own comment,
                            // 2026-09-19 regression) - if
                            // SkillPowerUpChanceAlwaysPatch's Postfix still
                            // runs after this one despite Priority.First,
                            // its restore becomes a harmless no-op instead
                            // of silently corrupting PUpSkillID back to the
                            // stale, pre-fallback (often 0/"Reserve")
                            // candidate.
                            SkillPowerUpChanceCandidateCapture.SyncFallbackTarget(mutationResult);
                            // Official spec (2026-09-19, User-directed):
                            // Phase B means no valid ordinary Power-Up
                            // candidate existed at all - a genuine Mutation
                            // success reached here must stay labeled as
                            // Mutation (result=2), never get relabeled as
                            // ordinary Power-Up the way a genuine native
                            // dil=1 success does under Always elsewhere.
                            // See SkillPowerUpChanceAlwaysPatch's own
                            // comment for the real-machine evidence
                            // (unit=97, "ジオ→ディア" mislabeled as Power-Up).
                            SkillPowerUpChanceAlwaysPatch.SuppressNextMutationConversion = true;
                            // Deliberately no bit6 write here - native's own
                            // Mutation success tail (VA 0x18227E59B) never
                            // touches bit6 either, only the ordinary/result=1
                            // tail does (VA 0x18227E4E8).
                        }
                    }
                }

                if (fallbackType == "none")
                {
                    fallbackType = "no-valid-candidate";
                }

                __result = finalResult;

                // CoreReentryHandledCheck's own Postfix already ran (default
                // priority, before this Last-priority one) and saw the
                // STALE __result==0 for THIS specific invocation, so its
                // own MarkEpisodeSkillChangeApplied(__result==1||2) call
                // never fired for a Phase B success. Phase A successes are
                // unaffected (each retry is a fully independent nested
                // call, whose OWN CoreReentryHandledCheck Postfix saw that
                // nested call's genuine result correctly). Replicate the
                // missing latch SET here only for the Phase B path.
                if (fallbackType == "mutation" && (finalResult == 1 || finalResult == 2))
                {
                    HandledCandidatesObserver.MarkEpisodeSkillChangeApplied(stockPtr);
                }

                bool episodeLatchAfter = HandledCandidatesObserver.IsEpisodeLatched(stockPtr);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] ALWAYS-EPISODE-GUARANTEE; " +
                    $"frame={frame}; unit={unit}; stockPtr=0x{stockPtr:X}; rawResult=0; " +
                    $"originalPUpSkillId={originalPUpSkillId}; " +
                    $"powerUpCandidateCount={rawPowerUpCandidates.Count}; " +
                    $"validPowerUpCandidateCount={rawPowerUpCandidates.Count}; " +
                    $"excludedPowerUpCandidates={excludedCount}; " +
                    $"ownedPowerUpCandidates={ownedBlocked.Count}; " +
                    $"duplicatePowerUpCandidates={ownedBlocked.Count}; " +
                    $"mutationCandidateCount={mutationCandidateCount}; " +
                    $"fallbackType={fallbackType}; fallbackTarget={fallbackTarget}; " +
                    $"finalResult={finalResult}; " +
                    $"episodeLatchBefore={episodeLatchBefore}; episodeLatchAfter={episodeLatchAfter}.");
            }
            catch (Exception ex)
            {
                _retryInProgress = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillPowerUpAlwaysEpisodeGuarantee postfix failed safely: {ex.Message}");
            }
        }

        private static HashSet<ushort> CollectOwnedSkills(Il2Cppnewdata_H.datUnitWork_t stock)
        {
            var owned = new HashSet<ushort>();
            var array = stock.skill;
            if (array == null) return owned;

            int limit = Math.Min(stock.skillcnt, array.Length);
            for (int i = 0; i < limit; i++)
            {
                ushort skillId = unchecked((ushort)array[i]);
                if (skillId != 0) owned.Add(skillId);
            }
            return owned;
        }
    }
}
