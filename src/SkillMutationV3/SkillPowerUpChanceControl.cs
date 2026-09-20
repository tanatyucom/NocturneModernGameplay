using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Zero-base implementation of [SkillPowerUp] Chance = Native / Always /
    // Disabled, per the Canonical CFG in 01_CURRENT_STATE.md. Disabled uses
    // the same raw-patch discipline as SkillMutationChanceControl. Always
    // uses Harmony Prefix/Postfix instead of a raw patch - the priority
    // rule (ordinary Power-Up outranks genuine Mutation whenever bit6 was
    // CLEAR, regardless of Mutation.Chance) needs bit6's value captured
    // before Core runs and a conditional revert of Core's own PUpSkillID
    // write after it returns, which does not fit as a same-length in-place
    // byte substitution. See SkillPowerUpChanceAlwaysPatch below.
    internal static class SkillPowerUpChanceControl
    {
        private sealed class Site
        {
            internal string Name = string.Empty;
            internal long Va;
            internal byte[] Vanilla = Array.Empty<byte>();
            internal byte[] Patched = Array.Empty<byte>();
            internal IntPtr Address;
            internal bool IsPatched;
        }

        // Disabled (2 sites, applied together as a set): the ordinary
        // Power-Up block's only state write (bit6 OR) and its return value.
        // Confirmed via instruction-level review of 0x18227E4C1-0x18227E4EC
        // that these are the block's ONLY side effects - no other field is
        // written there.
        private static readonly Site OrWrite = new()
        {
            Name = "PowerUpChance or-write",
            Va = 0x18227E4E8L,
            Vanilla = new byte[] { 0x83, 0x48, 0x10, 0x40 },
            Patched = new byte[] { 0x90, 0x90, 0x90, 0x90 }
        };

        private static readonly Site ReturnValue = new()
        {
            Name = "PowerUpChance return-value",
            Va = 0x18227E4ECL,
            Vanilla = new byte[] { 0xB0, 0x01 },
            Patched = new byte[] { 0xB0, 0x00 }
        };

        private static readonly Site[] DisabledSites = { OrWrite, ReturnValue };

        internal static NativeChanceMode Mode { get; private set; } = NativeChanceMode.Native;
        private static bool _resolved;

        internal static void Initialize()
        {
            if (_resolved) return;
            try
            {
                IntPtr moduleBase = NativeChancePatchUtility.ResolveModuleBase();
                bool allOk = true;
                foreach (Site site in DisabledSites)
                {
                    site.Address = NativeChancePatchUtility.ResolveVa(moduleBase, site.Va);
                    allOk &= NativeChancePatchUtility.VerifyKnownState(
                        site.Address, site.Vanilla, site.Patched, site.Name, out bool isPatched);
                    site.IsPatched = isPatched;
                }
                if (!allOk)
                    throw new InvalidOperationException(
                        "one or more SkillPowerUpChanceControl sites failed verification");

                _resolved = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SkillPowerUpChanceControl resolved; all sites vanilla-or-known.");

                ApplyMode(NativeChanceMode.Native);
            }
            catch (Exception ex)
            {
                _resolved = false;
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SkillPowerUpChanceControl init refused safely: {ex}");
            }
        }

        internal static void SetMode(NativeChanceMode mode)
        {
            if (!_resolved)
            {
                MelonLogger.Warning(
                    "[NocturneModernGameplay] SkillPowerUpChanceControl not resolved; ignoring SetMode.");
                return;
            }
            // Fail-safe: Always depends on SharedSkillChangeOuterGateControl
            // forcing the outer gate open (VA 0x18227EFD0) so Core is
            // actually invoked every level-up. If that shared owner did not
            // resolve, refuse the transition entirely (Mode stays whatever
            // it already was) - SkillPowerUpChanceAlwaysPatch's own
            // Core-result conversion is worthless if Core is only entered
            // at native (~1/4) frequency, so "Always" must never silently
            // degrade to that. Native/Disabled never need the outer gate
            // forced, so they are unaffected.
            if (mode == NativeChanceMode.Always && !SharedSkillChangeOuterGateControl.IsResolved)
            {
                MelonLogger.Error(
                    "[NocturneModernGameplay] SkillPowerUpChanceControl refusing Always; " +
                    "SharedSkillChangeOuterGateControl is not resolved (outer gate cannot be forced). " +
                    "Mode left unchanged.");
                return;
            }
            ApplyMode(mode);
        }

        internal static void Shutdown()
        {
            if (!_resolved) return;
            ApplyMode(NativeChanceMode.Native);
        }

        private static void ApplyMode(NativeChanceMode mode)
        {
            try
            {
                bool shouldBePatched = mode == NativeChanceMode.Disabled;
                foreach (Site site in DisabledSites)
                {
                    if (site.IsPatched == shouldBePatched) continue;

                    byte[] target = shouldBePatched ? site.Patched : site.Vanilla;
                    NativeChancePatchUtility.WriteExecutableBytes(site.Address, target);

                    byte[] readback = NativeChancePatchUtility.ReadBytes(site.Address, target.Length);
                    if (!NativeChancePatchUtility.BytesEqual(readback, target))
                        throw new InvalidOperationException(
                            $"{site.Name} readback mismatch after write; " +
                            $"expected={NativeChancePatchUtility.FormatBytes(target)} " +
                            $"actual={NativeChancePatchUtility.FormatBytes(readback)}");

                    site.IsPatched = shouldBePatched;
                }

                Mode = mode;
                MelonLogger.Msg($"[NocturneModernGameplay] SkillPowerUpChanceControl mode set; mode={mode}.");

                SharedSkillChangeOuterGateControl.Recompute();
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SkillPowerUpChanceControl SetMode({mode}) failed safely: {ex}");
            }
        }
    }

    // Captures the authoritative "original Power-Up target skill ID" for
    // whichever rstCalcSkillPowerUpCore invocation is currently in flight.
    // rstcalc.rstRndGetPowerUpSkill(datUnitWork_t, ref SByte) : ushort has
    // exactly ONE caller in the entire binary - Core itself (CONFIRMED via
    // whole-binary xref scan, see 01_CURRENT_STATE.md) - so this Postfix
    // firing always means "Core's own RNG pick, for the invocation that is
    // about to run the rest of its body". __result IS the value Core's own
    // "0x18227E15F: mov word[rbx+0x4e],ax" write puts into GBWK.PUpSkillID
    // at that exact moment - i.e. this is the value PUpSkillID holds
    // immediately before cmbGetMutationSkill (if reached later this same
    // invocation) overwrites it. Capturing it HERE, directly from the
    // native call's own return value, is deliberate: by the time Core
    // returns, GBWK.PUpSkillID may already be the MUTATED value (on
    // coreResult==2), so there is no way to recover the original by reading
    // GBWK afterward - re-deriving it from other state (e.g.
    // pCurrentStock.skill[PUpSkillIndex], the skill being mutated FROM, not
    // the ordinary Power-Up target) would be a different value entirely and
    // was explicitly rejected as speculative.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstRndGetPowerUpSkill))]
    internal static class SkillPowerUpChanceCandidateCapture
    {
        internal static ushort LastOriginalCandidateSkillId { get; private set; }

        private static void Postfix(ushort __result)
        {
            if (SkillPowerUpChanceControl.Mode != NativeChanceMode.Always) return;
            LastOriginalCandidateSkillId = __result;
        }

        // Real-machine regression (2026-09-19, see SkillPowerUpAlwaysEpisode
        // Guarantee.cs): relying on HarmonyLib's postfix priority ordering
        // to guarantee SkillPowerUpAlwaysEpisodeGuarantee's Postfix runs
        // strictly after SkillPowerUpChanceAlwaysPatch's proved unreliable
        // in this environment - two independent attempts, opposite
        // priority values, produced the IDENTICAL observed execution order
        // (this class's own native roll was always captured, and
        // SkillPowerUpChanceAlwaysPatch's restore always overwrote the
        // fallback target, regardless of Priority.Last vs Priority.First).
        // Rather than continue fighting an unpredictable ordering, keep
        // this field itself in sync: whenever the episode-guarantee's own
        // Phase B picks a genuine fallback target, it calls this so that
        // IF SkillPowerUpChanceAlwaysPatch's restore runs after all, it
        // restores to the SAME value already set - a no-op instead of a
        // silent corruption back to the stale, pre-fallback candidate
        // (which was 0/"Reserve" in the observed regression).
        internal static void SyncFallbackTarget(ushort target) => LastOriginalCandidateSkillId = target;
    }

    // Always priority (per this session's corrected spec): whenever
    // PowerUp.Chance==Always, ordinary Skill Power-Up wins over genuine
    // Mutation - regardless of whether Mutation.Chance is Native or Always.
    // The explicit user choice "Power-Up=100%" is treated as intent to see
    // that Power-Up happen, so it outranks even a genuinely native Mutation
    // trigger.
    //
    // bit6WasSet handling (2026-09-15 FIX, User-directed - see
    // investigations/ACQUISITION_LEARNASNEW/PLAN.md "Option F / SkillPowerUp.
    // Always regression" for the runtime evidence this fix responds to):
    // when bit6 was SET entering this invocation, Repeat=Native means
    // Power-Up cannot occur at all this cycle - this Postfix does nothing in
    // that case, and whatever Mutation.Chance (Native/Always/Disabled)
    // produced natively stands untouched (unchanged from Phase A). BUT under
    // Repeat=Unlimited, bit6 being already SET does NOT mean "this cycle is
    // over" - Repeat=Unlimited's whole purpose is to keep offering more
    // Power-Up opportunities within the same level-up, and the Canonical
    // spec (SkillPowerUp.Chance=Always + Repeat=Unlimited => only ordinary
    // Power-Up ever surfaces, as long as a valid candidate exists) requires
    // this same 2/3->1 priority conversion to keep applying on every
    // subsequent roll too, not just the first. Root cause of the bug this
    // fixes: Phase C (Repeat=Unlimited, OptionFRepeatUnlimitedControl.cs)
    // was added without updating this Phase A bit6WasSet-skip condition to
    // account for it - OptionFRepeatUnlimitedControl only ever converts
    // rawResult==0 (R0-A/B/C), never rawResult==2/3, so nothing else in the
    // codebase covers the "bit6 already SET, dil=1 RNG-bypass genuinely
    // reaches Mutation with a nonzero raw result" case under Unlimited -
    // CONFIRMED runtime observed (2026-09-15): unit=59, SkillPowerUp.Chance=
    // Always + Repeat=Unlimited + SkillMutation.Chance=Native, 5/5 successful
    // outcomes this session were native rawResult==2 (Mutation), 0/5 were
    // rawResult==1, with bit6WasSet==True on every OPTION-F-DECISION log for
    // this unit in the same session.
    //
    // Prefix/Postfix pairing follows the same "_captured guard" pattern
    // already established by RepeatableSkillPowerUp.cs (CapturePrefix /
    // ApplyPostfix), reused here for consistency and because it is already
    // a reviewed, low-risk shape for exactly this kind of "read state
    // before the native call, act on it after" logic.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class SkillPowerUpChanceAlwaysPatch
    {
        private static bool _captured;
        private static bool _bit6WasSet;

        private static void Prefix()
        {
            _captured = false;
            if (SkillPowerUpChanceControl.Mode != NativeChanceMode.Always) return;
            try
            {
                var stock = rstinit.GBWK?.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;
                _bit6WasSet = (stock.flag & 0x40) != 0;
                _captured = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillPowerUpChance Always prefix capture failed safely: {ex.Message}");
            }
        }

        // Exemption for SkillPowerUpAlwaysEpisodeGuarantee's Phase B
        // (2026-09-19, User-directed design: "Power-UpできるならPower-Up.
        // Power-UpできないならMutation" - once Phase B has been reached,
        // it already means no valid ordinary Power-Up candidate existed at
        // all, so a genuine Mutation success there must stay a Mutation,
        // not get relabeled as an ordinary Power-Up the way a genuine
        // native dil=1 success does under this same Always setting).
        // Real-machine evidence this responds to: unit=97, native
        // cmbGetMutationSkill(original=13:"ジオ") legitimately returned
        // 36:"ディア" (a correct, native-sanctioned but cross-family
        // Mutation result) - this class's own conversion then relabeled it
        // as "Skill Power-Up: ジオ→ディア", which reads as nonsensical to a
        // player since ordinary Power-Up is expected to stay within one
        // family. Phase B sets this flag immediately before setting its
        // own __result=2, and this Postfix consumes (clears) it the first
        // time it is observed, regardless of whether this Postfix happens
        // to run before or after Phase B's own Postfix in a given build
        // (HarmonyLib's postfix ordering between these two specific
        // patches proved unpredictable this session - see
        // SkillPowerUpChanceCandidateCapture.SyncFallbackTarget's own
        // comment for the same finding). Only Phase A (ordinary Power-Up
        // retry) and genuine native dil=1 successes remain subject to this
        // conversion - unchanged.
        internal static bool SuppressNextMutationConversion;

        private static void Postfix(ref sbyte __result)
        {
            if (SkillPowerUpChanceControl.Mode != NativeChanceMode.Always || !_captured) return;
            _captured = false; // consume once per Prefix/Postfix pair

            // Repeat=Native: already power-upped this cycle - defer entirely.
            // Repeat=Unlimited: bit6 already being SET does not end the
            // cycle - keep converting (see class-level comment above).
            if (_bit6WasSet && GameplaySettingsService.Repeat != "Unlimited") return;

            if (SuppressNextMutationConversion)
            {
                SuppressNextMutationConversion = false;
                return;
            }

            try
            {
                if (__result == 2 || __result == 3)
                {
                    // __result==2: genuine Mutation succeeded (native or via
                    //   Mutation.Always's forced bypass) - overridden per
                    //   the Power-Up-priority spec. GBWK.PUpSkillID
                    //   currently holds the MUTATED value (Core's own
                    //   write); restore it to the original candidate
                    //   captured authoritatively above, from
                    //   rstRndGetPowerUpSkill's own return value, before
                    //   applying the ordinary Power-Up write.
                    // __result==3: Mutation attempt genuinely failed -
                    //   PUpSkillID was never touched by Core on this path,
                    //   so writing it again here is a harmless no-op that
                    //   keeps both branches symmetric.
                    var gbwk = rstinit.GBWK;
                    if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                    var stock = gbwk.pCurrentStock;
                    if (stock == null || stock.Pointer == IntPtr.Zero) return;

                    gbwk.PUpSkillID = SkillPowerUpChanceCandidateCapture.LastOriginalCandidateSkillId;
                    stock.flag |= 0x40;
                    __result = 1;

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SkillPowerUpChance Always priority applied; " +
                        $"unit={stock.id} frame={UnityEngine.Time.frameCount} " +
                        $"restoredSkillId={SkillPowerUpChanceCandidateCapture.LastOriginalCandidateSkillId}.");
                }
                // __result==1: already ordinary Power-Up - nothing to do.
                // __result==0: no candidate / excluded - never fabricated.
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillPowerUpChance Always priority failed safely: {ex.Message}");
            }
        }
    }
}
