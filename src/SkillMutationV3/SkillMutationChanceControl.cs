using System;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Zero-base implementation of [SkillMutation] Chance = Native / Always /
    // Disabled, per the Canonical CFG in 01_CURRENT_STATE.md
    // ("Power-Up / genuine Mutation 振り分けCFG"). Does NOT reuse or
    // coexist with the old SkillMutationAlways (Patch A/B/C) - that class
    // is retired from runtime (no longer Initialize()'d or registered; see
    // ModMain.cs / GameplayFeatureRegistry.cs), specifically because its
    // Patch C site (0x18227E39C) is the exact same VA this class's
    // "id==0 path" site patches with different bytes - the two must never
    // both be active.
    //
    // Site inventory (all VAs statically confirmed via capstone
    // disassembly this session):
    //
    //   Always (3 sites, applied together as a set - the outer gate,
    //   VA 0x18227EFD0, is a SEPARATE shared resource; see below):
    //     RNG path A  0x18227E339  7E 11 -> 90 90       (first-roll success
    //                                                    threshold jle; NOP
    //                                                    so it never
    //                                                    diverts away from
    //                                                    the RNG-bypass
    //                                                    Mutation route)
    //     RNG path B  0x18227E342  7F 5D -> 90 90       (first-roll
    //                                                    degenerate
    //                                                    threshold jg; NOP
    //                                                    for the same
    //                                                    reason)
    //     id==0 path  0x18227E39C  40 32 FF -> 40 B7 01 (second roll's own
    //                                                    success assignment
    //                                                    - completeness for
    //                                                    the practically-
    //                                                    unreachable id==0
    //                                                    case; NOT the same
    //                                                    role as the old
    //                                                    Patch C even
    //                                                    though it shares
    //                                                    the VA and patched
    //                                                    bytes)
    //
    //     With all four applied, every non-degenerate RNG roll falls
    //     through unconditionally to 0x18227E344 ("mov dil,1; jmp
    //     0x18227e4bc"), the native RNG-bypass route that reaches
    //     cmbGetMutationSkill without ever consulting bit6/promotion-check/
    //     the 16-scan. cmbGetMutationSkill's own return value is never
    //     overridden - a genuine native mapping failure (nativeResult==0)
    //     still yields coreResult==3 exactly as vanilla.
    //
    //   Disabled (2 sites, applied together as a set):
    //     D-entry 1   0x18227E4BA  75 56 -> 90 90   (scan-hit branch)
    //     D-entry 2   0x18227E4BF  75 47 -> 90 90   (dil==1 branch)
    //
    //     With both applied, the merge block at 0x18227E4BC always falls
    //     through to the ordinary Power-Up block regardless of dil/sil,
    //     suppressing all three native Mutation triggers (RNG-bypass,
    //     promotion-check, 16-scan) at once while leaving ordinary
    //     Power-Up's own code path completely untouched.
    //
    // The outer gate (VA 0x18227EFD0) is owned exclusively by
    // SharedSkillChangeOuterGateControl, not by this class. Native and
    // Disabled leave it vanilla (A8 03) unless SkillPowerUpChanceControl's
    // own Mode independently demands Always - Disabled must never suppress
    // the outer gate itself, since that would also suppress ordinary Skill
    // Power-Up, which is out of scope for SkillMutation.Chance. This class
    // calls SharedSkillChangeOuterGateControl.Recompute() at the end of its
    // own ApplyMode so the shared gate always reflects both Chance modes
    // together, never just this one in isolation.
    internal static class SkillMutationChanceControl
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

        // The outer gate (VA 0x18227EFD0) is NOT owned here - it is
        // exclusively owned by SharedSkillChangeOuterGateControl, which
        // derives its forced/vanilla state from both this class's Mode and
        // SkillPowerUpChanceControl.Mode (either == Always forces it). This
        // class must never define a Site for that VA - see ApplyMode's
        // Recompute() call below.
        private static readonly Site RngPathA = new()
        {
            Name = "MutationChance rng-path-A",
            Va = 0x18227E339L,
            Vanilla = new byte[] { 0x7E, 0x11 },
            Patched = new byte[] { 0x90, 0x90 }
        };

        private static readonly Site RngPathB = new()
        {
            Name = "MutationChance rng-path-B",
            Va = 0x18227E342L,
            Vanilla = new byte[] { 0x7F, 0x5D },
            Patched = new byte[] { 0x90, 0x90 }
        };

        private static readonly Site IdZeroPath = new()
        {
            Name = "MutationChance id-zero-path",
            Va = 0x18227E39CL,
            Vanilla = new byte[] { 0x40, 0x32, 0xFF },
            Patched = new byte[] { 0x40, 0xB7, 0x01 }
        };

        private static readonly Site DEntry1 = new()
        {
            Name = "MutationChance d-entry-1",
            Va = 0x18227E4BAL,
            Vanilla = new byte[] { 0x75, 0x56 },
            Patched = new byte[] { 0x90, 0x90 }
        };

        private static readonly Site DEntry2 = new()
        {
            Name = "MutationChance d-entry-2",
            Va = 0x18227E4BFL,
            Vanilla = new byte[] { 0x75, 0x47 },
            Patched = new byte[] { 0x90, 0x90 }
        };

        private static readonly Site[] AlwaysSites = { RngPathA, RngPathB, IdZeroPath };
        private static readonly Site[] DisabledSites = { DEntry1, DEntry2 };
        private static readonly Site[] AllSites = { RngPathA, RngPathB, IdZeroPath, DEntry1, DEntry2 };

        internal static NativeChanceMode Mode { get; private set; } = NativeChanceMode.Native;
        private static bool _resolved;

        internal static void Initialize()
        {
            if (_resolved) return;
            try
            {
                IntPtr moduleBase = NativeChancePatchUtility.ResolveModuleBase();
                bool allOk = true;
                foreach (Site site in AllSites)
                {
                    site.Address = NativeChancePatchUtility.ResolveVa(moduleBase, site.Va);
                    allOk &= NativeChancePatchUtility.VerifyKnownState(
                        site.Address, site.Vanilla, site.Patched, site.Name, out bool isPatched);
                    site.IsPatched = isPatched;
                }
                if (!allOk)
                    throw new InvalidOperationException(
                        "one or more SkillMutationChanceControl sites failed verification");

                _resolved = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SkillMutationChanceControl resolved; all sites vanilla-or-known.");

                // Defensive: reconcile to Native at startup regardless of any
                // unexpected leftover patched state (should always be
                // vanilla on a fresh process, since this mod owns the only
                // known patched form and has not run yet this session).
                ApplyMode(NativeChanceMode.Native);
            }
            catch (Exception ex)
            {
                _resolved = false;
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SkillMutationChanceControl init refused safely: {ex}");
            }
        }

        internal static void SetMode(NativeChanceMode mode)
        {
            if (!_resolved)
            {
                MelonLogger.Warning(
                    "[NocturneModernGameplay] SkillMutationChanceControl not resolved; ignoring SetMode.");
                return;
            }
            // Fail-safe: Always depends on SharedSkillChangeOuterGateControl
            // forcing the outer gate open. If that shared owner did not
            // resolve, refuse the transition entirely (Mode stays whatever
            // it already was) rather than applying this class's own 3 sites
            // while the outer gate remains vanilla - that would silently
            // leave "Always" running at native (~1/4) frequency instead of
            // 100%, which must never happen unannounced. Native/Disabled
            // never need the outer gate forced, so they are unaffected.
            if (mode == NativeChanceMode.Always && !SharedSkillChangeOuterGateControl.IsResolved)
            {
                MelonLogger.Error(
                    "[NocturneModernGameplay] SkillMutationChanceControl refusing Always; " +
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
                Site[] targetPatchedSites = mode switch
                {
                    NativeChanceMode.Always => AlwaysSites,
                    NativeChanceMode.Disabled => DisabledSites,
                    _ => Array.Empty<Site>()
                };

                foreach (Site site in AllSites)
                {
                    bool shouldBePatched = Array.IndexOf(targetPatchedSites, site) >= 0;
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
                MelonLogger.Msg($"[NocturneModernGameplay] SkillMutationChanceControl mode set; mode={mode}.");

                SharedSkillChangeOuterGateControl.Recompute();
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SkillMutationChanceControl SetMode({mode}) failed safely: {ex}");
            }
        }
    }
}
