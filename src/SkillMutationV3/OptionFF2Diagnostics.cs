using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Repeat=Unlimited investigation, Option F / F2 feasibility diagnostic
    // ONLY (investigations/REPEAT_UNLIMITED/PLAN.md). Read-only observer -
    // NOT the Option F result-conversion logic itself. Does not implement
    // Repeat=Unlimited's final behavior: no __result mutation, no bit6
    // write, no GBWK/pCurrentStock/save-data write of any kind. No GUI, no
    // settings.json entry, not registered with GameplayFeatureRegistry.
    //
    // ===== Scope correction (2026-09-05, actual-game test finding) =====
    //
    // Runtime telemetry from the previous round found that
    // rstcalc.rstCreateBeforeSkillList (VA 0x182280460) fires far more
    // often than just from inside rstCalcSkillPowerUpCore's one call site
    // (0x18227E261) - many ExclusionObserved lines were logged both before
    // Core's own invocation=1 ever started, and continuing to log against
    // that same stale invocation id long after Core's own
    // CorePostfixConsume had already run. Tagging the exclusion-list
    // Postfix with "whatever CurrentInvocationId currently is" was
    // therefore too broad a correlation - it could attribute an unrelated,
    // out-of-Core call to a Core invocation that had already finished (or
    // not started yet).
    //
    // Fix: a [ThreadStatic] _coreActive flag, set true only for the
    // duration of Core's own Prefix..Postfix window (reset to false in a
    // finally block so an exception inside Core's Postfix cannot leave it
    // stuck true), gates OptionFF2ExclusionListObserver's Postfix entirely
    // - a rstCreateBeforeSkillList call observed while _coreActive is
    // false now produces NO log line at all from this diagnostic (it is
    // certainly not the call this investigation cares about, per this
    // round's finding that such calls happen independently of Core).
    // CurrentInvocationId is only meaningful while CoreActive is true.
    //
    // Purpose (unchanged from the previous round): confirm at runtime that
    // a Harmony Postfix on rstCreateBeforeSkillList can bind to its 4th
    // argument (Il2Cppresult2_H.rstSkillInfo_t, the exclusion-list output
    // object Core allocates and passes in) via Harmony's positional "__3"
    // convention, and that object's SkillCnt (+0x10)/SkillID (+0x20, a
    // reference to a separate ushort[] array object, NOT an inline buffer
    // - confirmed by disassembly: [info+0x20] holds an IL2CPP array
    // reference, whose own length lives at [thatArray+0x18] and whose
    // ushort element data starts at [thatArray+0x20]) can be read
    // read-only and correlated with GBWK.PUpSkillID for the SAME Core
    // invocation that triggered it - now correctly scoped to ONLY the
    // call that happens while that specific Core invocation is active.
    //
    // See investigations/REPEAT_UNLIMITED/PLAN.md "Option F: F2 vs F4比較"
    // for why F2 (observe an existing call) was selected over F4
    // (re-invoke rstCreateBeforeSkillList from a Postfix) - F2 never
    // re-executes anything, so it inherits none of the risk from
    // rstCreateBeforeSkillList's own not-fully-analyzed callees
    // (cmbChkSkillOwner, 0x181717dd0, 0x18203f220). The full return0-path
    // CFG this diagnostic's classification is based on (R0-A: PUpSkillID
    // ==0, R0-B: exclusion-list match, R0-C: bit6 gate - the only three
    // return0 causes, CONFIRMED static) is documented there.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class OptionFF2CoreDiagnostics
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why.
        internal static readonly bool Enabled = false;

        [ThreadStatic] private static bool _coreActive;
        [ThreadStatic] private static bool _exclusionObservedThisCore;
        [ThreadStatic] private static bool _exclusionMatchedThisCore;
        [ThreadStatic] private static ushort[]? _exclusionSkillIdsThisCore;
        [ThreadStatic] private static long _invocationCounter;

        // Only meaningful while CoreActive is true - see "Scope
        // correction" above. OptionFF2ExclusionListObserver checks
        // CoreActive itself before ever reading this.
        internal static bool CoreActive => _coreActive;
        internal static long CurrentInvocationId => _invocationCounter;

        internal static void MarkExclusionListObserved()
        {
            _exclusionObservedThisCore = true;
        }

        internal static void MarkExclusionMatched()
        {
            _exclusionMatchedThisCore = true;
        }

        // Snapshot of the exclusion list actually built for this Core
        // invocation (all entries, not just a match) so Core's own
        // Postfix can log "exclusionSkillIDs vs pUpSkillID" evidence even
        // for the non-match (R0-C-CANDIDATE) case - lets a reader directly
        // see "candidate != exclusion list" rather than only a boolean.
        internal static void SetExclusionSnapshot(ushort[] ids)
        {
            _exclusionSkillIdsThisCore = ids;
        }

        // Harmony __state for THIS method's own Prefix/Postfix pair only.
        private sealed class CorePrefixState
        {
            internal bool Bit6WasSet;
            internal long InvocationId;
        }

        private static void Prefix(out CorePrefixState? __state)
        {
            __state = null;
            if (!Enabled) return;
            try
            {
                _coreActive = true;
                _exclusionObservedThisCore = false;
                _exclusionMatchedThisCore = false;
                _exclusionSkillIdsThisCore = null;
                _invocationCounter++;

                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                bool bit6WasSet = stock != null && stock.Pointer != IntPtr.Zero
                    && (stock.flag & 0x40) != 0;

                __state = new CorePrefixState
                {
                    Bit6WasSet = bit6WasSet,
                    InvocationId = _invocationCounter,
                };

                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-OPTIONF-F2-LIFECYCLE; " +
                    $"stage=CorePrefixReset; invocation={_invocationCounter}.");
            }
            catch (Exception ex)
            {
                // Fail-safe: if anything above throws after _coreActive
                // was already set true, still clear it so a broken Prefix
                // can never leave CoreActive stuck true forever (which
                // would make every subsequent, unrelated
                // rstCreateBeforeSkillList call misattribute itself to a
                // Core invocation that never properly started).
                _coreActive = false;
                __state = null;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] OptionFF2CoreDiagnostics prefix failed safely: {ex.Message}");
            }
        }

        // Read-only. Does not touch __result (no ref/out on it) and writes
        // nothing to native memory - observation only. The outer
        // try/finally guarantees _coreActive is cleared even if logging or
        // a GBWK read throws, so a failure here cannot leave CoreActive
        // stuck true (same fail-safe intent as the Prefix's catch above).
        private static void Postfix(sbyte __result, CorePrefixState? __state)
        {
            try
            {
                if (!Enabled || __state == null) return;
                try
                {
                    int rawResult = __result;
                    bool bit6WasSet = __state.Bit6WasSet;
                    bool exclusionObserved = _exclusionObservedThisCore;
                    bool exclusionMatched = _exclusionMatchedThisCore;
                    ushort[] exclusionSkillIds = _exclusionSkillIdsThisCore ?? Array.Empty<ushort>();

                    var gbwk = rstinit.GBWK;
                    int pUpSkillID = gbwk != null ? gbwk.PUpSkillID : -1;

                    // Classification never claims more than the static CFG
                    // (investigations/REPEAT_UNLIMITED/PLAN.md "全return0
                    // path一覧") supports. R0-A/R0-B are direct reads of
                    // runtime signals that CFG-necessarily imply that
                    // path; R0-C is elimination-by-exclusion ("not A, not
                    // B, bit6 was set") and is therefore named
                    // "-CANDIDATE", never asserted as CONFIRMED runtime by
                    // this diagnostic alone.
                    string classification;
                    if (rawResult != 0)
                        classification = "NONZERO";
                    else if (pUpSkillID == 0)
                        classification = "R0-A";
                    else if (exclusionObserved && exclusionMatched)
                        classification = "R0-B";
                    else if (bit6WasSet && exclusionObserved && !exclusionMatched)
                        classification = "R0-C-CANDIDATE";
                    else
                        classification = "UNKNOWN";

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-OPTIONF-F2-LIFECYCLE; " +
                        $"stage=CorePostfixConsume; invocation={__state.InvocationId}.");

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-OPTIONF-F2; " +
                        $"invocation={__state.InvocationId}; rawResult={rawResult}; " +
                        $"bit6WasSet={bit6WasSet}; pUpSkillID={pUpSkillID}; " +
                        $"exclusionObserved={exclusionObserved}; exclusionMatched={exclusionMatched}; " +
                        $"classification={classification}.");

                    // rawResult==0 detail dump - lets a reader directly
                    // compare pUpSkillID against the exclusion list this
                    // Core invocation actually built, for every return0
                    // case (R0-A/R0-B/R0-C-CANDIDATE alike), not only the
                    // match case.
                    if (rawResult == 0)
                    {
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] V3-OPTIONF-F2-R0; " +
                            $"invocation={__state.InvocationId}; bit6WasSet={bit6WasSet}; " +
                            $"pUpSkillID={pUpSkillID}; skillCnt={exclusionSkillIds.Length}; " +
                            $"exclusionMatched={exclusionMatched}; " +
                            $"exclusionSkillIDs=[{string.Join(",", exclusionSkillIds)}].");
                    }

                    // Unconditional (every Core invocation, independent of
                    // rawResult/classification/match) exclusion-list dump.
                    // Uses a name distinct from the existing
                    // V3-OPTIONF-F2-EXCLUSION tag below (match-only, richer
                    // fields: pUpSkillID/matchedIndex/matchedSkillID) so the
                    // two line shapes never collide under one tag name -
                    // that existing tag's meaning is left unchanged.
                    // exclusionSkillIds/exclusionObserved are read-only
                    // snapshots of what OptionFF2ExclusionListObserver's own
                    // Postfix already captured this invocation (see
                    // SetExclusionSnapshot/MarkExclusionListObserved) - no
                    // new native call, no array/state mutation.
                    // Named representation, additive only - existing raw
                    // exclusionSkillIds/skillIDs field above is unchanged.
                    // See SkillNameResolver.cs.
                    var exclusionSkillIdsNamed = new string[exclusionSkillIds.Length];
                    for (int i = 0; i < exclusionSkillIds.Length; i++)
                        exclusionSkillIdsNamed[i] = SkillNameResolver.Format(exclusionSkillIds[i]);

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-OPTIONF-F2-EXCLUSION-ALL; " +
                        $"invocation={__state.InvocationId}; observed={exclusionObserved}; " +
                        $"skillCnt={exclusionSkillIds.Length}; " +
                        $"skillIDs=[{string.Join(",", exclusionSkillIds)}]; " +
                        $"skillIDsNamed=[{string.Join(",", exclusionSkillIdsNamed)}].");

                    // Lifecycle note: this clears the snapshot fields right
                    // after logging them, for this invocation's own data.
                    // This does NOT claim exception-proof clearing - if
                    // something above in this same try block throws before
                    // reaching this point, these fields are left as-is
                    // until the NEXT Prefix unconditionally resets them
                    // (see Prefix's own reset block above), which already
                    // prevents any stale cross-invocation leak either way.
                    _exclusionObservedThisCore = false;
                    _exclusionMatchedThisCore = false;
                    _exclusionSkillIdsThisCore = null;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] OptionFF2CoreDiagnostics postfix failed safely: {ex.Message}");
                }
            }
            finally
            {
                _coreActive = false;
            }
        }
    }

    // Observes the exclusion list rstCreateBeforeSkillList just built,
    // scoped to ONLY the call that happens while
    // OptionFF2CoreDiagnostics.CoreActive is true (see "Scope correction"
    // above) - i.e. the call nested inside the currently-running Core
    // invocation. Never writes to the rstSkillInfo_t object, its arrays,
    // GBWK, or pCurrentStock - pure read via
    // Marshal.ReadByte/ReadIntPtr/ReadInt32/ReadInt16 on
    // disassembly-confirmed offsets (investigations/REPEAT_UNLIMITED/
    // PLAN.md):
    //   info(+0x10)      = SkillCnt (sbyte, count of valid entries)
    //   info(+0x20)      = reference to a separate ushort[] array object
    //     (NOT an inline buffer) - that array's own length is at
    //     [arrayPtr+0x18] and its element data starts at [arrayPtr+0x20]
    //     (standard IL2CPP array layout), read up to min(SkillCnt, array
    //     length, 24) elements - never more than the 24-slot cap Core's
    //     own allocation uses.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCreateBeforeSkillList))]
    internal static class OptionFF2ExclusionListObserver
    {
        private const int SkillCntOffset = 0x10;
        private const int SkillIdArrayFieldOffset = 0x20;
        private const int ArrayLengthOffset = 0x18;
        private const int ArrayDataOffset = 0x20;
        private const int MaxEntries = 24;

        // Positional original-argument binding (Harmony "__N" convention)
        // rather than by name: the interop assembly's real parameter names
        // for this native-heavy method are not confirmed, so binding by
        // position (4th parameter, 0-indexed = __3) is the reliable
        // option regardless of what that name turns out to be.
        private static void Postfix(Il2Cppresult2_H.rstSkillInfo_t __3)
        {
            if (!OptionFF2CoreDiagnostics.Enabled) return;
            // Out-of-Core calls (CONFIRMED runtime this session:
            // rstCreateBeforeSkillList fires far more often than just from
            // Core) are not this investigation's concern and must not be
            // logged as if they were - silently skip entirely.
            if (!OptionFF2CoreDiagnostics.CoreActive) return;

            long invocation = OptionFF2CoreDiagnostics.CurrentInvocationId;
            try
            {
                OptionFF2CoreDiagnostics.MarkExclusionListObserved();

                if (__3 == null || __3.Pointer == IntPtr.Zero)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-OPTIONF-F2-LIFECYCLE; " +
                        $"stage=ExclusionObserved; invocation={invocation}; infoNull=True.");
                    return;
                }
                IntPtr infoPtr = __3.Pointer;

                int skillCnt = Marshal.ReadByte(infoPtr, SkillCntOffset);
                skillCnt = Math.Max(0, Math.Min(skillCnt, MaxEntries));

                IntPtr skillIdArrayPtr = Marshal.ReadIntPtr(infoPtr, SkillIdArrayFieldOffset);
                if (skillIdArrayPtr == IntPtr.Zero)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-OPTIONF-F2-LIFECYCLE; " +
                        $"stage=ExclusionObserved; invocation={invocation}; skillCnt={skillCnt}; arrayNull=True.");
                    OptionFF2CoreDiagnostics.SetExclusionSnapshot(Array.Empty<ushort>());
                    return;
                }

                int arrayLength = Marshal.ReadInt32(skillIdArrayPtr, ArrayLengthOffset);
                int readCount = Math.Max(0, Math.Min(skillCnt, Math.Min(arrayLength, MaxEntries)));

                var gbwk = rstinit.GBWK;
                if (gbwk == null)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-OPTIONF-F2-LIFECYCLE; " +
                        $"stage=ExclusionObserved; invocation={invocation}; skillCnt={skillCnt}; gbwkNull=True.");
                    return;
                }
                ushort pUpSkillID = gbwk.PUpSkillID;

                int matchedIndex = -1;
                ushort matchedSkillID = 0;
                var candidateIds = new ushort[readCount];
                for (int i = 0; i < readCount; i++)
                {
                    ushort candidateId = (ushort)Marshal.ReadInt16(skillIdArrayPtr, ArrayDataOffset + i * 2);
                    candidateIds[i] = candidateId;
                    if (matchedIndex < 0 && candidateId == pUpSkillID)
                    {
                        matchedIndex = i;
                        matchedSkillID = candidateId;
                    }
                }

                OptionFF2CoreDiagnostics.SetExclusionSnapshot(candidateIds);

                bool matched = matchedIndex >= 0;
                if (matched) OptionFF2CoreDiagnostics.MarkExclusionMatched();

                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-OPTIONF-F2-LIFECYCLE; " +
                    $"stage=ExclusionObserved; invocation={invocation}; skillCnt={skillCnt}; " +
                    $"exclusionMatched={matched}.");

                // Detailed dump only for the positive (match) case - the
                // non-match case is already fully covered by the
                // lightweight lifecycle line above plus Core's own
                // V3-OPTIONF-F2 / V3-OPTIONF-F2-R0 lines.
                if (matched)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-OPTIONF-F2-EXCLUSION; " +
                        $"invocation={invocation}; pUpSkillID={pUpSkillID}; skillCnt={skillCnt}; " +
                        $"matchedIndex={matchedIndex}; matchedSkillID={matchedSkillID}; " +
                        $"candidateSkillIDs=[{string.Join(",", candidateIds)}].");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] OptionFF2ExclusionListObserver postfix failed safely: {ex.Message}");
            }
        }
    }
}
