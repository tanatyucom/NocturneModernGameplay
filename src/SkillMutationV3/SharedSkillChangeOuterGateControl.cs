using System;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Single owner of the outer skill-change gate (VA 0x18227EFD0,
    // rstcalc.rstCalc's "test al, 3" that decides whether
    // rstCalcSkillPowerUpCore is even called this level-up). Neither
    // SkillMutationChanceControl nor SkillPowerUpChanceControl may hold a
    // Site definition for this VA - this class is the only writer.
    //
    // Effective policy (STRONGLY SUPPORTED that 0x18227EFD0 is the pre-Core
    // stochastic gate - static disassembly + the Mutation.Always/Native
    // outer-gate raw-patch precedent; runtime AL observation not performed.
    // Not documented as fully CONFIRMED native semantics):
    //   forcePass = SkillMutationChanceControl.Mode == Always
    //            || SkillPowerUpChanceControl.Mode == Always
    //   forcePass == false -> vanilla (A8 03)
    //   forcePass == true  -> patched (A8 00)
    //
    // Recompute() re-reads BOTH Chance modes fresh every time either
    // ChanceControl finishes applying its own mode - never assumes "last
    // setter wins" - so setting order never leaves a stale byte state (see
    // 01_CURRENT_STATE.md Phase A for the verified transition sequence).
    internal static class SharedSkillChangeOuterGateControl
    {
        private const long OuterGateVa = 0x18227EFD0L;
        private static readonly byte[] Vanilla = { 0xA8, 0x03 };
        private static readonly byte[] Patched = { 0xA8, 0x00 };

        private static IntPtr _address;
        private static bool _isPatched;
        private static bool _resolved;

        internal static bool IsResolved => _resolved;

        internal static void Initialize()
        {
            if (_resolved) return;
            try
            {
                IntPtr moduleBase = NativeChancePatchUtility.ResolveModuleBase();
                _address = NativeChancePatchUtility.ResolveVa(moduleBase, OuterGateVa);

                if (!NativeChancePatchUtility.VerifyKnownState(
                        _address, Vanilla, Patched, "SharedOuterGate", out bool isPatched))
                    throw new InvalidOperationException("SharedOuterGate site failed verification");

                _isPatched = isPatched;
                _resolved = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SharedSkillChangeOuterGateControl resolved; site vanilla-or-known.");

                // Defensive: reconcile from current Chance modes right away
                // (both SkillMutationChanceControl.Mode / SkillPowerUpChanceControl.Mode
                // read their compile-time default (Native) until their own
                // Initialize()/GameplaySettingsService.Load() runs, which is
                // fine - this just establishes the vanilla baseline before
                // either ChanceControl has patched anything).
                Recompute();
            }
            catch (Exception ex)
            {
                _resolved = false;
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SharedSkillChangeOuterGateControl init refused safely: {ex}");
            }
        }

        // Called by SkillMutationChanceControl / SkillPowerUpChanceControl
        // at the end of their own ApplyMode, after their own Mode property
        // has been updated. Re-derives forcePass from both current Mode
        // values - never trusts "which side just changed".
        internal static void Recompute()
        {
            if (!_resolved)
            {
                MelonLogger.Warning(
                    "[NocturneModernGameplay] SharedSkillChangeOuterGateControl not resolved; ignoring Recompute.");
                return;
            }

            NativeChanceMode mutationMode = SkillMutationChanceControl.Mode;
            NativeChanceMode powerUpMode = SkillPowerUpChanceControl.Mode;
            bool forcePass = mutationMode == NativeChanceMode.Always || powerUpMode == NativeChanceMode.Always;

            try
            {
                if (_isPatched != forcePass)
                {
                    byte[] target = forcePass ? Patched : Vanilla;
                    NativeChancePatchUtility.WriteExecutableBytes(_address, target);

                    byte[] readback = NativeChancePatchUtility.ReadBytes(_address, target.Length);
                    if (!NativeChancePatchUtility.BytesEqual(readback, target))
                        throw new InvalidOperationException(
                            $"SharedOuterGate readback mismatch after write; " +
                            $"expected={NativeChancePatchUtility.FormatBytes(target)} " +
                            $"actual={NativeChancePatchUtility.FormatBytes(readback)}");

                    _isPatched = forcePass;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SharedOuterGate; " +
                        $"mutationMode={mutationMode} powerUpMode={powerUpMode} forcePass={forcePass} " +
                        $"bytes={NativeChancePatchUtility.FormatBytes(target)}.");
                }
                else if (PowerUpMutationCfgDiagnostics.Enabled)
                {
                    byte[] current = NativeChancePatchUtility.ReadBytes(_address, Vanilla.Length);
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SharedOuterGate (unchanged); " +
                        $"mutationMode={mutationMode} powerUpMode={powerUpMode} forcePass={forcePass} " +
                        $"bytes={NativeChancePatchUtility.FormatBytes(current)}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SharedSkillChangeOuterGateControl Recompute failed safely: {ex}");
            }
        }
    }
}
