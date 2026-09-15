using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Option F result conversion - INITIAL CANDIDATE implementation
    // (investigations/REPEAT_UNLIMITED/PLAN.md). Converts
    // rstCalcSkillPowerUpCore's return0 R0-C case (bit6 gate: "already
    // power-upped this cycle", native rawResult==0) to ordinary Power-Up
    // success (__result=1), ONLY when [SkillPowerUp] Repeat==Unlimited.
    // NEVER converts R0-A (no candidate, PUpSkillID==0) or R0-B (native
    // exclusion match, rstCreateBeforeSkillList) - see PLAN.md's "R0-B
    // runtime positive確認(2026-09-12)" (unit136/Mouryou, skill 19:"ザン"->
    // 22:"マハザン" blocked by exclusion) for the runtime evidence this
    // discriminator design depends on.
    //
    // NOT production-confirmed yet. This is explicitly a diagnostic-heavy
    // candidate for real-machine R0-A/B/C behavior verification before any
    // production sign-off.
    //
    // Independent observer (deliberate design choice, per explicit
    // requirement): this class runs its OWN Prefix/Postfix pair on
    // rstCalcSkillPowerUpCore and its OWN Postfix on
    // rstCreateBeforeSkillList, rather than reading
    // OptionFF2Diagnostics.OptionFF2CoreDiagnostics's internal ThreadStatic
    // state. That diagnostic's own Prefix/Postfix bodies are gated by its
    // own `Enabled` flag - if a future build ever turns that flag off (or
    // otherwise disables that diagnostic), this production class must keep
    // working unaffected. Sharing its internal observer would make this
    // production feature's correctness depend on an unrelated diagnostic's
    // own enabled state, which is exactly the coupling this design avoids.
    // The small cost is that the exclusion-list byte-offset read (SkillCnt/
    // SkillID array, offsets confirmed byte-exact in PLAN.md) is duplicated
    // here rather than shared - deliberate, to keep this file fully
    // self-contained and to avoid mixing production semantics into the
    // diagnostic-only OptionFF2Diagnostics.cs file.
    //
    // Known limitation (2026-09-12 settings-interaction review, UNRESOLVED -
    // NOT proven impossible): SkillMutation.Chance=Disabled + SkillPowerUp.
    // Repeat=Unlimited does not currently coexist as ideally desired.
    // MutationDisabledBit6Guard (Mode==Disabled, corrects a native dil=1
    // RNG-bypass artifact that can independently produce rawResult==1 while
    // bit6 was already SET) and this class (Mode==Disabled, would otherwise
    // want to convert a genuine bit6-SET R0-C's rawResult==0 to 1) share the
    // same mutable rstCalcSkillPowerUpCore __result with no explicit Harmony
    // priority on either patch. Confirmed by direct analysis: once bit6 was
    // already SET before Core ran, native memory offers NO secondary signal
    // (byte-exact Core disassembly, PLAN.md) distinguishing "genuine R0-C"
    // from "dil=1 bypass artifact" - both are entirely legitimate depending
    // on which one actually ran, and picking either fixed Harmony priority
    // makes exactly ONE of the two scenarios wrong (verified via explicit
    // case analysis, not merely "postfix competition is inconvenient"). This
    // class defensively never converts under Mode==Disabled instead of
    // guessing an execution order this session did not verify. Resolving
    // this for real requires either (a) an empirical, in-game verification
    // of this exact HarmonyLib build's postfix ordering for these two
    // specific patches (a safe, diagnostic-only marker-based test - not
    // performed this session), or (b) consolidating both corrections into a
    // single Postfix that reads the pristine __result exactly once. Neither
    // has been done. SkillPowerUp.Chance=Disabled (a different, PowerUp-side
    // raw byte patch at 0x18227E4E8/0x18227E4EC) has NO such conflict - see
    // the Postfix's "Unknown" branch comment below for why that interaction
    // is already safe by construction.
    internal static class OptionFRepeatUnlimitedControl
    {
        private const int SkillCntOffset = 0x10;
        private const int SkillIdArrayFieldOffset = 0x20;
        private const int ArrayLengthOffset = 0x18;
        private const int ArrayDataOffset = 0x20;
        private const int MaxEntries = 24;

        [ThreadStatic] private static bool _coreActive;
        [ThreadStatic] private static bool _exclusionObservedThisCore;
        [ThreadStatic] private static bool _exclusionMatchedThisCore;
        [ThreadStatic] private static long _invocationCounter;

        private enum Classification { R0A, R0B, R0C, Unknown }
        private enum ConversionAction { Converted, NotConverted, AbortedInconsistentObserver }

        internal sealed class CorePrefixState
        {
            internal bool Bit6WasSet;
            internal int UnitId;
            internal int Level;
            internal long InvocationId;
        }

        internal static CorePrefixState? OnCorePrefix()
        {
            try
            {
                _coreActive = true;
                _exclusionObservedThisCore = false;
                _exclusionMatchedThisCore = false;
                _invocationCounter++;
                _diagLoggedThisCoreWindow = false; // TEMPORARY DIAGNOSTIC, see OnExclusionListObserved

                var stock = rstinit.GBWK?.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero)
                {
                    return new CorePrefixState
                    {
                        Bit6WasSet = false,
                        UnitId = -1,
                        Level = -1,
                        InvocationId = _invocationCounter
                    };
                }

                return new CorePrefixState
                {
                    Bit6WasSet = (stock.flag & 0x40) != 0,
                    UnitId = stock.id,
                    Level = stock.level,
                    InvocationId = _invocationCounter
                };
            }
            catch (Exception ex)
            {
                _coreActive = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] OptionFRepeatUnlimitedControl prefix failed safely: {ex.Message}");
                return null;
            }
        }

        // Reads the exclusion-list output object rstCreateBeforeSkillList
        // just built for THIS Core invocation (guarded by _coreActive,
        // mirroring OptionFF2ExclusionListObserver's own scoping
        // discipline - see OptionFF2Diagnostics.cs for the original
        // "Scope correction" rationale). Read-only: never writes to the
        // info object, its arrays, GBWK, or pCurrentStock.
        // TEMPORARY DIAGNOSTIC (2026-09-15, User-directed investigation into
        // OPTION-F-DECISION consistently showing exclusionObserved=False /
        // ABORTED_INCONSISTENT_OBSERVER, unrelated to today's Mutation
        // AddNew work - possible regression vs the 2026-09-12 CONFIRMED
        // success in investigations/REPEAT_UNLIMITED/PLAN.md). Read-only,
        // does not affect any decision - logs unconditionally every call
        // (both _coreActive branches) so the actual call frequency/timing
        // of rstCreateBeforeSkillList relative to rstCalcSkillPowerUpCore's
        // own active window can be observed directly, rather than inferred.
        // Remove once the root cause is determined.
        private static int _diagCallCounter;
        private static bool _diagLoggedThisCoreWindow;

        internal static void OnExclusionListObserved(Il2Cppresult2_H.rstSkillInfo_t info)
        {
            _diagCallCounter++;
            if (_diagCallCounter % 500 == 0)
            {
                try
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] OPTIONF-DIAG-TOTAL-CALLS; " +
                        $"totalCalls={_diagCallCounter}; frame={UnityEngine.Time.frameCount}.");
                }
                catch { /* diagnostic only */ }
            }
            if (_coreActive && !_diagLoggedThisCoreWindow)
            {
                _diagLoggedThisCoreWindow = true;
                try
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] OPTIONF-DIAG-EXCLUSION-HIT; " +
                        $"totalCalls={_diagCallCounter}; coreActive=True; frame={UnityEngine.Time.frameCount}.");
                }
                catch { /* diagnostic only */ }
            }
            if (!_coreActive) return;
            try
            {
                _exclusionObservedThisCore = true;
                if (info == null || info.Pointer == IntPtr.Zero) return;
                IntPtr infoPtr = info.Pointer;

                int skillCnt = Marshal.ReadByte(infoPtr, SkillCntOffset);
                skillCnt = Math.Max(0, Math.Min(skillCnt, MaxEntries));

                IntPtr arrayPtr = Marshal.ReadIntPtr(infoPtr, SkillIdArrayFieldOffset);
                if (arrayPtr == IntPtr.Zero) return;

                int arrayLength = Marshal.ReadInt32(arrayPtr, ArrayLengthOffset);
                int readCount = Math.Max(0, Math.Min(skillCnt, Math.Min(arrayLength, MaxEntries)));

                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                ushort pUpSkillId = gbwk.PUpSkillID;

                for (int i = 0; i < readCount; i++)
                {
                    ushort candidateId = (ushort)Marshal.ReadInt16(arrayPtr, ArrayDataOffset + i * 2);
                    if (candidateId == pUpSkillId)
                    {
                        _exclusionMatchedThisCore = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    "[NocturneModernGameplay] OptionFRepeatUnlimitedControl exclusion observer failed safely: " +
                    $"{ex.Message}");
            }
        }

        // Core Postfix logic. ref sbyte so a decision to convert can
        // actually change what rstCalc's own native caller subsequently
        // reads (the same mechanism already proven in production by
        // SkillPowerUpChanceAlwaysPatch's own __result conversion, which
        // this session observed firing correctly in real gameplay).
        internal static void OnCorePostfix(ref sbyte __result, CorePrefixState? state)
        {
            try
            {
                if (state == null) return;
                // Repeat==Native: never touch native result at all - this
                // entire feature is inert unless the user explicitly
                // enabled Repeat=Unlimited.
                if (GameplaySettingsService.Repeat != "Unlimited") return;

                int rawResult = __result;
                // Scope: Option F only ever judges rawResult==0 cases
                // (R0-A/B/C). A non-zero result (ordinary success, genuine
                // Mutation success/failure) is never touched and never
                // logged here - see OPTION-F-DECISION's own field list,
                // which is documented as covering "each rawResult==0 case".
                if (rawResult != 0) return;

                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                int pUpSkillId = gbwk.PUpSkillID;

                Classification classification;
                if (pUpSkillId == 0)
                {
                    classification = Classification.R0A;
                }
                else if (_exclusionObservedThisCore && _exclusionMatchedThisCore)
                {
                    classification = Classification.R0B;
                }
                else if (_exclusionObservedThisCore && !_exclusionMatchedThisCore && state.Bit6WasSet)
                {
                    classification = Classification.R0C;
                }
                else
                {
                    classification = Classification.Unknown;
                }

                ConversionAction action;
                string reason;

                switch (classification)
                {
                    case Classification.R0C:
                        // Defensive skip (see class header comment): under
                        // SkillMutation.Chance==Disabled, a native dil=1
                        // RNG-bypass invocation can independently produce
                        // rawResult==1 while bit6 was already SET (a
                        // separate, pre-existing bug that
                        // MutationDisabledBit6Guard corrects back to 0).
                        // This class and that guard cannot be told apart
                        // from Harmony-visible state alone once either one
                        // has written __result, so - rather than depend on
                        // an unverified Harmony postfix execution order
                        // between the two patches - this class never
                        // converts while Mode==Disabled. Native/Always are
                        // unaffected (that dil=1-produces-a-false-1 bug is
                        // specific to Mutation.Disabled's own raw patch).
                        if (SkillMutationChanceControl.Mode == NativeChanceMode.Disabled)
                        {
                            action = ConversionAction.NotConverted;
                            reason = "SkillMutation.Chance=Disabled active; cannot safely distinguish from " +
                                     "dil=1 bypass artifact (MutationDisabledBit6Guard), conversion skipped defensively.";
                        }
                        else
                        {
                            // Ordinary success tail's only side effects are
                            // "flag |= 0x40; return 1" (PLAN.md, byte-exact
                            // disassembly). bit6 is already guaranteed SET
                            // here (R0-C's own branch condition), so the
                            // flag write would be an idempotent no-op -
                            // deliberately NOT re-issued, per the explicit
                            // instruction to touch __result only.
                            __result = 1;
                            action = ConversionAction.Converted;
                            reason = "Repeat Unlimited ordinary Power-Up retry.";
                        }
                        break;

                    case Classification.R0B:
                        action = ConversionAction.NotConverted;
                        reason = "Native exclusion matched.";
                        break;

                    case Classification.R0A:
                        action = ConversionAction.NotConverted;
                        reason = "No candidate skill (PUpSkillID==0).";
                        break;

                    default:
                        if (!_exclusionObservedThisCore)
                        {
                            action = ConversionAction.AbortedInconsistentObserver;
                            reason = "Exclusion observer not observed.";
                        }
                        else if (SkillPowerUpChanceControl.Mode == NativeChanceMode.Disabled)
                        {
                            // Expected, not a genuine inconsistency: under
                            // [SkillPowerUp] Chance=Disabled, the ordinary
                            // success tail's own native bytes are raw-patched
                            // (0x18227E4E8/0x18227E4EC NOPed/zeroed), so a
                            // genuine "bit6 was CLEAR, ordinary Power-Up
                            // applies" invocation also returns rawResult==0
                            // here - with bit6WasSet==false, since reaching
                            // that tail at all requires bit6 CLEAR beforehand
                            // (mutually exclusive with R0-C's own bit6-SET
                            // branch condition by CFG construction). This
                            // class's own bit6WasSet==true requirement for
                            // R0-C already keeps it out of Option F's
                            // conversion scope - this branch exists only to
                            // log an accurate reason instead of a misleading
                            // "inconsistency" label.
                            action = ConversionAction.NotConverted;
                            reason = "SkillPowerUp.Chance=Disabled active; native ordinary success tail is " +
                                     "raw-patched (bit6WasSet=false expected here), not a real observer inconsistency.";
                        }
                        else
                        {
                            action = ConversionAction.NotConverted;
                            reason = "Observer inconsistency: elimination did not resolve to a known " +
                                     "return0 path (bit6WasSet=false).";
                        }
                        break;
                }

                LogDecision(state, rawResult, pUpSkillId, classification, action, reason);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] OptionFRepeatUnlimitedControl postfix failed safely: {ex.Message}");
            }
            finally
            {
                _coreActive = false;
            }
        }

        private static void LogDecision(
            CorePrefixState state, int rawResult, int pUpSkillId,
            Classification classification, ConversionAction action, string reason)
        {
            string classificationText = classification switch
            {
                Classification.R0A => "R0-A",
                Classification.R0B => "R0-B",
                Classification.R0C => "R0-C",
                _ => "UNKNOWN"
            };
            string actionText = action switch
            {
                ConversionAction.Converted => "CONVERTED_TO_ORDINARY_SUCCESS",
                ConversionAction.NotConverted => "NOT_CONVERTED",
                _ => "ABORTED_INCONSISTENT_OBSERVER"
            };
            string skillName = SkillNameResolver.Resolve(pUpSkillId);

            MelonLogger.Msg(
                "[NocturneModernGameplay] OPTION-F-DECISION; " +
                $"invocation={state.InvocationId}; unit={state.UnitId}; level={state.Level}; " +
                $"pUpSkillID={pUpSkillId}; pUpSkillName=\"{skillName}\"; rawResult={rawResult}; " +
                $"bit6WasSet={state.Bit6WasSet}; exclusionObserved={_exclusionObservedThisCore}; " +
                $"exclusionMatched={_exclusionMatchedThisCore}; classification={classificationText}; " +
                $"repeatMode={GameplaySettingsService.Repeat}; " +
                $"mutationChanceMode={SkillMutationChanceControl.Mode}; " +
                $"powerUpChanceMode={SkillPowerUpChanceControl.Mode}; " +
                $"action={actionText}; reason={reason}.");
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class OptionFRepeatUnlimitedCorePatch
    {
        private static void Prefix(out OptionFRepeatUnlimitedControl.CorePrefixState? __state)
        {
            __state = OptionFRepeatUnlimitedControl.OnCorePrefix();
        }

        private static void Postfix(ref sbyte __result, OptionFRepeatUnlimitedControl.CorePrefixState? __state)
        {
            OptionFRepeatUnlimitedControl.OnCorePostfix(ref __result, __state);
        }
    }

    // Positional binding (Harmony "__N" convention), matching
    // OptionFF2ExclusionListObserver's own established reasoning: the
    // interop assembly's real parameter names for this native-heavy method
    // are not confirmed, so binding by position (4th parameter, 0-indexed
    // = __3) is the reliable option.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCreateBeforeSkillList))]
    internal static class OptionFRepeatUnlimitedExclusionObserver
    {
        private static void Postfix(Il2Cppresult2_H.rstSkillInfo_t __3)
        {
            OptionFRepeatUnlimitedControl.OnExclusionListObserved(__3);
        }
    }
}
