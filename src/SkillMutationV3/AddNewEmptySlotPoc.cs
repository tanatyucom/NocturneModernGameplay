using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - EMPTY-SLOT ONLY runtime diagnostic PoC.
    // Full static safety investigation this session (see
    // investigations/ addnew-related report exchanges; not yet written to
    // PLAN.md/01_CURRENT_STATE.md per that investigation's own scope) found:
    //
    //   - rstUpdateSeqSkillPowerUp (VA 0x18228C770) reads GBWK.PUpSkillIndex
    //     exactly twice: once to compute &WorkStock.skill[PUpSkillIndex] for
    //     rstOverWriteSkill's ownership write, and once more later to pass
    //     as an argument into presentation dispatch (0x182280a50). Both
    //     reads happen AFTER this method is entered, so redirecting
    //     GBWK.PUpSkillIndex in a Prefix (before the native body runs) makes
    //     both reads consistently see the redirected value.
    //   - rstOverWriteSkill (managed rstupdate.rstOverWriteSkill) itself is
    //     a trivial *pSkill = NewSkillID write; the destination address is
    //     entirely the caller's responsibility (rstUpdateSeqSkillPowerUp
    //     computes it from PUpSkillIndex), so redirecting the index alone
    //     is sufficient to make ownership land in a different slot - no
    //     additional native call or state write is needed.
    //   - The two staged fields written by presentation dispatch
    //     (0x182280a50) that carry PUpSkillIndex onward - ACTION+0x28 and
    //     source-object+0xc8 - were traced to two independent, statically
    //     confirmed consumer chains this session:
    //       (a) ACTION+0x28 -> 0x1822D93F0: PUpSkillIndex value used purely
    //           for glyph/tile atlas coordinate math (/4, %4, then
    //           multiplied by fixed tile dimensions), writing into a vertex
    //           buffer - no WorkStock.skill[]/PUpSkillID/skill-name
    //           reference anywhere in the traced body.
    //       (b) source-object+0xc8 gates (as a zero/nonzero check only -
    //           the literal value 0x1e is never itself consumed) a call
    //           chain reached from a SeqInfo.Current-driven switch dispatch
    //           that re-reads GBWK.PUpSkillIndex directly ([rdx+0x4c]) and
    //           forwards it through 0x1822dbc30 (pure arg-marshaling
    //           wrapper) into 0x1822db3a0, where it is added to a base
    //           screen position (edx += r12d) before four glyph-draw calls
    //           - again no WorkStock.skill[]/PUpSkillID/skill-name
    //           reference in the traced body.
    //   - The remaining open item, the runtime-registered handler for
    //     message code 0x2b0003 (reached via 0x18230cd10 ->
    //     0x1826D5250's listener-table lookup), could not be resolved
    //     statically: its function-pointer slot lives in a
    //     VirtualSize>RawSize (BSS-like) region of .debug, populated only
    //     at runtime. This is a limitation of static analysis, not
    //     evidence of an unsafe consumer - and it is a SEPARATE dispatch
    //     path from the SeqInfo.Current switch above, which IS statically
    //     traceable end-to-end and shown safe.
    //
    // Final static classification: RUNTIME DIAGNOSTIC REQUIRED (not yet
    // SAFE CANDIDATE, since the 0x2b0003 handler remains unresolved; NOT
    // UNSAFE, since no consumer requiring the redirected index to still
    // identify the source skill was found in any traced chain).
    //
    // This class is exactly that runtime diagnostic: it performs the
    // redirect ONLY when a genuine empty slot exists, ONLY writes
    // GBWK.PUpSkillIndex (no other native/game state), and logs before/after
    // skill-slot snapshots so a human can directly verify ownership
    // correctness and presentation sanity in-game. It is diagnostic-only:
    // Enabled is a hardcoded internal switch, never exposed through
    // GameplayFeatureRegistry, Settings GUI, or settings.json, so a normal
    // user cannot enable it by accident. Full-capacity (no empty slot) is
    // explicitly never handled here - native processing is left completely
    // untouched in that case (ADDNEW-POC-SKIP is logged, nothing else).
    //
    // Phase 1 runtime result (real machine, unit=103, 九十九針(111) ->
    // 毒針(113)): the redirect + native rstOverWriteSkill write succeeded -
    // skill[emptySlot] genuinely became 113 while skill[originalIndex]
    // stayed 111 (sourcePreserved=True, targetAtEmptySlot=True). But the
    // game did not show the new skill as owned, because
    // datUnitWork_s.skillcnt (+0x48 Int32) was never incremented - it
    // stayed at its pre-PowerUp value. This session's instruction-aligned
    // full-.text scan found the native invariant directly: both
    // fclRagCalc.ragCreateDevilData (creation-time: skillcnt reset to 0,
    // then incremented once per nonzero skill[] entry while copying a
    // species template) and a symmetric deletion-direction native routine
    // (skill[] slot cleared + skillcnt decremented together) confirm
    // skillcnt is meant to equal the count of nonzero skill[] entries -
    // and, in both native routines observed, skill[] is always fully
    // packed at the front (indices [0, skillcnt) nonzero, everything from
    // skillcnt onward zero). Correction of an earlier (wrong) hypothesis:
    // rstupdate.rstAddSkill (VA 0x182285A40) - despite its metadata name -
    // was fully disassembled and does NOT touch skill[]/skillcnt at all;
    // it tail-jumps into rstcalc.rstSetMaxHpMp and is unrelated to skill
    // acquisition. Name alone was not trusted as evidence of behavior.
    //
    // Phase 2 (this revision) adds exactly one more state write -
    // datUnitWork_s.skillcnt - under a much narrower set of conditions
    // than "just increment it":
    //   - emptySlot is no longer "first zero found by scanning the whole
    //     array" - it is FIXED to skillCnt itself, and only used at all if
    //     the array is independently verified to be fully packed
    //     ([0, skillCnt) all nonzero, skillCnt itself zero) - this matches
    //     the exact shape the native invariant above always produces, so
    //     the PoC never has to guess at a layout native wouldn't create
    //     itself.
    //   - skillcnt is committed (+1) ONLY in Postfix, ONLY once native has
    //     genuinely performed the ownership write this frame
    //     (skillsAfter[emptySlot]==target AND skillsAfter[originalIndex]
    //     still==source AND skillcnt is still exactly what Prefix
    //     captured) - never unconditionally, and never in Prefix.
    //   - rstUpdateSeqSkillPowerUp is confirmed (Phase 1 runtime log) to
    //     run many times per single Power-Up before the real
    //     rstOverWriteSkill write actually happens on one specific frame -
    //     so a simple per-call "always commit" would increment skillcnt
    //     repeatedly for the same event. Double-commit is prevented by
    //     latching the (unit, originalIndex, sourceSkillId, targetSkillId)
    //     identity of the event once committed and refusing to touch
    //     PUpSkillIndex at all for any further frame matching that same
    //     identity - not by re-deriving emptySlot from a now-advanced
    //     skillcnt (which would otherwise walk into the next slot every
    //     remaining frame of the same Power-Up).
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
    internal static class AddNewEmptySlotPoc
    {
        // Diagnostic-only switch. Must be false before any public/release
        // build - this PoC intentionally bypasses no safety checks, but it
        // is not intended for general players and is not wired into any
        // user-facing setting.
        //
        // Re-enabled 2026-09-12 for a TARGETED capacity-boundary runtime
        // test only, after the 2026-09-12 incident (unit=60, skillCnt=8,
        // emptySlot=8, IndexOutOfRangeException, game appeared to freeze)
        // was traced (STRONGLY SUPPORTED, not yet independently CONFIRMED
        // against the specific consumer that threw) to this PoC's old
        // capacity check comparing skillCnt against the managed backing
        // array's own allocated Length (24) instead of the true native
        // logical skill capacity (8, confirmed via full disassembly of
        // fclRagCalc.ragCreateDevilData) - letting skillCnt==8 redirect
        // PUpSkillIndex to slot 8, a value native itself never produces.
        // See LogicalSkillCapacity below for the fix. Code-reviewed before
        // this re-enable: the capacity check now returns before any
        // redirect or array[skillCnt] read whenever
        // skillCnt >= LogicalSkillCapacity, so slot 8 can no longer be
        // reached this way.
        internal static readonly bool Enabled = true;

        // Logical skill capacity, CONFIRMED via full disassembly of
        // fclRagCalc.ragCreateDevilData this session: its skill[]-rebuild
        // loop iterates exactly 8 times (cmp ecx, 8 / jl), regardless of
        // the MANAGED BACKING ARRAY's own allocated Length (observed as 24
        // for every unit checked this session - an implementation detail
        // of however IL2CPP/native chose to allocate the array, unrelated
        // to how many of those slots gameplay actually treats as valid).
        // PUpSkillIndex values native itself ever produces are also always
        // < skillcnt <= this capacity (rstRndGetPowerUpSkill only ever
        // selects from already-owned slots) - so a redirect to slot 8 (as
        // this PoC's Phase 2 capacity guard incorrectly allowed once
        // skillCnt reached 8, by comparing against ownedArray.Length
        // instead of this constant) hands PUpSkillIndex a value native
        // itself would never produce. The 2026-09-12 incident (unit=60,
        // skillCnt=8, emptySlot=8, IndexOutOfRangeException) is consistent
        // with some OTHER consumer of PUpSkillIndex/skill[] (native or
        // another mod's own Harmony patch on the same method) assuming
        // this exact 8-slot bound and indexing a fixed-size-8 structure of
        // its own out of bounds - not with slot 8 being a genuine skill
        // slot. This is STRONGLY SUPPORTED by the ragCreateDevilData
        // evidence, not yet independently CONFIRMED against the specific
        // consumer that threw.
        private const int LogicalSkillCapacity = 8;

        private static bool _active;
        private static bool _restored;
        private static int _invocation;
        private static int _unit;
        private static int _originalIndex;
        private static int _emptySlot;
        private static int _sourceSkillId;
        private static int _targetSkillId;
        private static int _originalSkillCnt;
        private static int[] _skillsBefore = Array.Empty<int>();

        private static int _invocationCounter;

        // Latches the identity of the one event this PoC has already
        // committed a skillcnt increment for, so repeated frames of the
        // SAME Power-Up (confirmed to occur - Phase 1) are left untouched
        // instead of being redirected/committed again.
        private static bool _eventCommitted;
        private static int _committedUnit;
        private static int _committedOriginalIndex;
        private static int _committedSourceSkillId;
        private static int _committedTargetSkillId;

        // Temporary diagnostic-only dedup guard (event-latch runtime
        // validation): ensures ADDNEW-LATCH-BLOCK is logged once per latch
        // lifetime instead of once per blocked frame. Carries no semantic
        // weight of its own - always kept in sync with _eventCommitted.
        private static bool _blockLoggedForCurrentLatch;

        private static void Prefix()
        {
            _active = false;
            _restored = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                // Only ordinary-success outcomes (native raw success or an
                // already-approved Option F R0-C conversion, both of which
                // converge on PUpSkillResult==1 before this method runs).
                if (gbwk.PUpSkillResult != 1)
                {
                    // Native has moved past the "ordinary success" decision
                    // this PoC acts on. Whatever event _eventCommitted (if
                    // set) was latched against is no longer the live one -
                    // clearing it here means a LATER, genuinely new
                    // Power-Up that happens to reuse the exact same
                    // (unit, originalIndex, sourceSkillId, targetSkillId)
                    // tuple (e.g. deliberately repeating the same skill
                    // under Repeat=Unlimited) is not permanently blocked.
                    // PUpSkillResult is the same field this whole PoC
                    // already gates on, not a newly invented signal, and
                    // rstCalcSkillPowerUpCore recomputes it fresh for every
                    // Power-Up opportunity - it does not stay 1 across two
                    // logically distinct events.
                    if (_eventCommitted)
                    {
                        // Temporary diagnostic (event-latch runtime
                        // validation, not yet promoted to a permanent
                        // semantic) - only logged on the true->false
                        // transition, never per-frame.
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] ADDNEW-LATCH-RESET; " +
                            $"unit={_committedUnit}; pUpSkillResult={gbwk.PUpSkillResult}.");
                    }
                    _eventCommitted = false;
                    _blockLoggedForCurrentLatch = false;
                    return;
                }

                sbyte originalIndex = gbwk.PUpSkillIndex;
                if (originalIndex < 0) return;

                ushort targetSkillId = gbwk.PUpSkillID;
                if (targetSkillId == 0) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                var ownedArray = stock.skill;
                if (ownedArray == null) return;

                int skillCnt = stock.skillcnt;
                int arrayLength = ownedArray.Length;
                int unit = stock.id;

                if (originalIndex >= skillCnt) return; // defensive, shouldn't happen for a valid PUpSkillIndex

                int sourceSkillId = ownedArray[originalIndex] & 0xFFFF;
                if (sourceSkillId == 0) return; // defensive only, should not happen for a valid PUpSkillIndex

                // rstUpdateSeqSkillPowerUp runs many times per single
                // Power-Up (Phase 1 runtime log), but the real
                // rstOverWriteSkill write - and this PoC's skillcnt commit
                // - only happen once. Once committed for this exact event
                // identity, do nothing at all on any later frame that
                // still matches it: no redirect, no re-commit, no log
                // spam. This is the entire double-commit safeguard.
                bool sameAsCommittedEvent = _eventCommitted &&
                    unit == _committedUnit &&
                    originalIndex == _committedOriginalIndex &&
                    sourceSkillId == _committedSourceSkillId &&
                    targetSkillId == _committedTargetSkillId;
                if (sameAsCommittedEvent)
                {
                    if (!_blockLoggedForCurrentLatch)
                    {
                        // Temporary diagnostic (event-latch runtime
                        // validation) - logged once per latch lifetime
                        // (not per frame) via _blockLoggedForCurrentLatch,
                        // reset alongside _eventCommitted above.
                        _blockLoggedForCurrentLatch = true;
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] ADDNEW-LATCH-BLOCK; " +
                            $"unit={unit}; originalIndex={originalIndex}; " +
                            $"source={SkillNameResolver.Format(sourceSkillId)}; " +
                            $"target={SkillNameResolver.Format(targetSkillId)}; " +
                            "reason=same-committed-event.");
                    }
                    return;
                }

                int invocation = ++_invocationCounter;

                // Capacity is judged against the LOGICAL skill capacity
                // (8, confirmed native-side - see LogicalSkillCapacity),
                // never against the managed backing array's own allocated
                // Length. arrayLength is used only as a memory-safety
                // bound (can we even read index LogicalSkillCapacity-1
                // safely), never as the capacity itself - conflating the
                // two was the root cause of the 2026-09-12 incident.
                if (skillCnt < 0 || skillCnt >= LogicalSkillCapacity || arrayLength < LogicalSkillCapacity)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-POC-SKIP; " +
                        $"invocation={invocation}; unit={unit}; reason=no-capacity.");
                    return; // full capacity: native left completely untouched
                }

                // Packed-layout invariant this PoC requires: owned skills
                // occupy exactly [0, skillCnt) with no gaps, and slot
                // skillCnt itself is empty. This is the exact shape the
                // confirmed native invariant (skillcnt == count of nonzero
                // skill[] entries, as established by
                // fclRagCalc.ragCreateDevilData and its deletion-direction
                // counterpart) always produces, so emptySlot can be fixed
                // to skillCnt directly instead of scanning for "any" zero
                // slot.
                bool packed = true;
                for (int i = 0; i < skillCnt; i++)
                {
                    if ((ownedArray[i] & 0xFFFF) == 0) { packed = false; break; }
                }
                if (packed && (ownedArray[skillCnt] & 0xFFFF) != 0) packed = false;

                if (!packed)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-POC-SKIP; " +
                        $"invocation={invocation}; unit={unit}; reason=non-packed-skill-layout.");
                    return;
                }

                int emptySlot = skillCnt;

                var skillsBefore = new int[arrayLength];
                for (int i = 0; i < arrayLength; i++) skillsBefore[i] = ownedArray[i] & 0xFFFF;

                _invocation = invocation;
                _unit = unit;
                _originalIndex = originalIndex;
                _emptySlot = emptySlot;
                _sourceSkillId = sourceSkillId;
                _targetSkillId = targetSkillId;
                _originalSkillCnt = skillCnt;
                _skillsBefore = skillsBefore;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] ADDNEW-POC-BEGIN; " +
                    $"invocation={_invocation}; unit={_unit}; " +
                    $"originalIndex={_originalIndex}; emptySlot={_emptySlot}; " +
                    $"skillCnt={_originalSkillCnt}; " +
                    $"source={SkillNameResolver.Format(_sourceSkillId)}; " +
                    $"target={SkillNameResolver.Format(_targetSkillId)}; " +
                    $"skillsBefore=[{string.Join(",", _skillsBefore)}].");

                // The only state write performed here in Prefix: redirect
                // the ownership-write target slot. rstOverWriteSkill's own
                // destination address is computed by the caller
                // (rstUpdateSeqSkillPowerUp) from this field, so this alone
                // is sufficient to move the write to the empty slot.
                // skillcnt is deliberately NOT touched here - see Postfix.
                gbwk.PUpSkillIndex = (sbyte)emptySlot;
                _active = true;
            }
            catch (Exception ex)
            {
                _active = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] AddNewEmptySlotPoc prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_active) return;
            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;

                var ownedArray = stock.skill;
                if (ownedArray == null) return;

                int count = _skillsBefore.Length;
                var skillsAfter = new List<int>(count);
                for (int i = 0; i < count && i < ownedArray.Length; i++)
                    skillsAfter.Add(ownedArray[i] & 0xFFFF);

                bool sourcePreserved = _originalIndex < skillsAfter.Count &&
                                       skillsAfter[_originalIndex] == _sourceSkillId;
                bool targetAtEmptySlot = _emptySlot < skillsAfter.Count &&
                                         skillsAfter[_emptySlot] == _targetSkillId;
                int currentSkillCnt = stock.skillcnt;
                // Read BEFORE the restore below runs (see finally), while
                // GBWK.PUpSkillIndex still holds the redirected value
                // native itself finished with - confirms native did not
                // already reset it on its own.
                sbyte pUpIndexBeforeRestore = gbwk.PUpSkillIndex;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] ADDNEW-POC-END; " +
                    $"invocation={_invocation}; unit={_unit}; " +
                    $"skillsAfter=[{string.Join(",", skillsAfter)}]; " +
                    $"sourcePreserved={sourcePreserved}; targetAtEmptySlot={targetAtEmptySlot}; " +
                    $"skillCntCurrent={currentSkillCnt}; " +
                    $"pUpSkillIndexBeforeRestore={pUpIndexBeforeRestore}.");

                // skillcnt commit: only when native has genuinely performed
                // the ownership write THIS frame (most frames it has not -
                // Phase 1 showed the real rstOverWriteSkill write landing
                // on exactly one out of ~10 repeated calls), the source
                // slot is still intact, and skillcnt itself is still
                // exactly what Prefix captured (nothing else touched it in
                // between). Never unconditional, never more than once per
                // event (see the _eventCommitted latch in Prefix).
                if (!targetAtEmptySlot)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-POC-NO-COMMIT; " +
                        $"invocation={_invocation}; unit={_unit}; reason=target-not-written.");
                }
                else if (!sourcePreserved)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-POC-NO-COMMIT; " +
                        $"invocation={_invocation}; unit={_unit}; reason=source-not-preserved.");
                }
                else if (currentSkillCnt != _originalSkillCnt)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-POC-NO-COMMIT; " +
                        $"invocation={_invocation}; unit={_unit}; reason=skillcnt-changed-unexpectedly.");
                }
                else
                {
                    stock.skillcnt = _originalSkillCnt + 1;
                    _eventCommitted = true;
                    _blockLoggedForCurrentLatch = false;
                    _committedUnit = _unit;
                    _committedOriginalIndex = _originalIndex;
                    _committedSourceSkillId = _sourceSkillId;
                    _committedTargetSkillId = _targetSkillId;

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-LATCH-SET; " +
                        $"unit={_unit}; originalIndex={_originalIndex}; " +
                        $"source={SkillNameResolver.Format(_sourceSkillId)}; " +
                        $"target={SkillNameResolver.Format(_targetSkillId)}.");

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] ADDNEW-POC-COMMIT; " +
                        $"invocation={_invocation}; unit={_unit}; " +
                        $"sourceSlot={_originalIndex}; targetSlot={_emptySlot}; " +
                        $"source={SkillNameResolver.Format(_sourceSkillId)}; " +
                        $"target={SkillNameResolver.Format(_targetSkillId)}; " +
                        $"skillCntBefore={_originalSkillCnt}; skillCntAfter={stock.skillcnt}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] AddNewEmptySlotPoc postfix failed safely: {ex.Message}");
            }
            finally
            {
                // The redirect written in Prefix is a transient steering
                // value only, used purely to make rstOverWriteSkill's
                // caller-computed destination address land on the empty
                // slot for the duration of this one native call. It is not
                // state this PoC intends to leave changed, so it is always
                // restored here - regardless of whether the logging above
                // succeeded. Restoring GBWK.PUpSkillIndex does NOT undo the
                // ownership write itself: WorkStock.skill[emptySlot] keeps
                // whatever rstOverWriteSkill already wrote into it,
                // independent of this field's later value.
                RestoreOriginalIndex();
            }
        }

        // Harmony always runs a Finalizer, in both the normal and the
        // exception-thrown path (unlike Postfix, which Harmony skips when
        // the original method throws) - so this is the only reliable place
        // to guarantee the restore still happens if
        // rstUpdateSeqSkillPowerUp (or another Harmony patch stacked on
        // the same method) throws before Postfix would otherwise run. Does
        // not swallow the exception: always returns it unchanged.
        private static Exception? Finalizer(Exception? __exception)
        {
            if (_active && !_restored)
            {
                if (__exception != null)
                {
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] ADDNEW-POC-EXCEPTION; " +
                        $"invocation={_invocation}; unit={_unit}; " +
                        $"restoring PUpSkillIndex after an exception: {__exception.Message}");
                }
                RestoreOriginalIndex();
            }
            _active = false;
            return __exception;
        }

        private static void RestoreOriginalIndex()
        {
            if (_restored) return;
            _restored = true;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk != null) gbwk.PUpSkillIndex = (sbyte)_originalIndex;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] AddNewEmptySlotPoc restore failed: {ex.Message}");
            }
        }
    }
}
