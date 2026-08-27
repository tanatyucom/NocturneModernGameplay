using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    internal static class SkillMutationAlways
    {
        private const uint PageExecuteReadWrite = 0x40;
        private const long GameAssemblyPreferredBase = 0x180000000L;
        private const long MutationHelperNativeVa = 0x18227E100L;
        private const int MutationHelperPatchBOffset = 0x24C;
        private const int MutationHelperPatchCOffset = 0x29C;
        private static readonly byte[] PatchAVanillaBytes = { 0xA8, 0x03 };
        private static readonly byte[] PatchAPatchedBytes = { 0xA8, 0x00 };
        private static readonly byte[] RollFailureVanillaBytes = { 0x40, 0x32, 0xFF };
        private static readonly byte[] RollFailurePatchedBytes = { 0x40, 0xB7, 0x01 };
        private static readonly byte[] FailureReturnBytes =
            { 0x30, 0xC0, 0x48, 0x83, 0xC4, 0x28, 0xC3 };
        private static readonly byte[] JumpToCoreBytes =
            { 0xE9, 0x20, 0x00, 0x00, 0x00, 0x90, 0x90 };
        private static readonly byte[] ReturnTrueBytes = { 0xB0, 0x01, 0xC3 };
        private static bool _enabled = true;
        private static bool _patched;
        private static IntPtr _patchAAddress;
        private static IntPtr _patchBAddress;
        private static IntPtr _patchCAddress;
        private static bool _patchAWritten;
        private static bool _patchBWritten;
        private static bool _patchCWritten;

        internal static void Initialize()
        {
            if (_enabled)
            {
                ApplyPatch();
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (enabled) ApplyPatch();
            else RestorePatch();
            MelonLogger.Msg(
                $"[NocturneModernGameplay] Skill Mutation: Always " +
                $"{(enabled && _patched ? "enabled" : "disabled")}.");
        }

        internal static void Shutdown() => RestorePatch();

        internal static bool IsEnabled => _enabled && _patched;

        private static void ApplyPatch()
        {
            if (_patched) return;
            try
            {
                IntPtr resultCalculator = GetNativeMethodByPrefix(
                    "NativeMethodInfoPtr_rstCalc_");
                if (resultCalculator == IntPtr.Zero)
                    throw new InvalidOperationException("rstCalc native entry is unavailable");

                // rstCalc's seq=8 level-up case performs the authoritative upstream
                // mutation roll here: call check; test al,3; je mutation-calculation.
                _patchAAddress = IntPtr.Add(resultCalculator, 0x8C0);

                IntPtr helperEntry = ResolveMutationHelperEntry();
                _patchBAddress = IntPtr.Add(helperEntry, MutationHelperPatchBOffset);
                _patchCAddress = IntPtr.Add(helperEntry, MutationHelperPatchCOffset);

                // Atomic verification phase: no site is changed until all three sites
                // are confirmed as either vanilla or already patched.
                PatchSiteState patchAState = VerifyPatchSite(
                    _patchAAddress, PatchAVanillaBytes, PatchAPatchedBytes, "Patch A");
                PatchSiteState patchBState = VerifyPatchSite(
                    _patchBAddress, RollFailureVanillaBytes, RollFailurePatchedBytes, "Patch B");
                PatchSiteState patchCState = VerifyPatchSite(
                    _patchCAddress, RollFailureVanillaBytes, RollFailurePatchedBytes, "Patch C");

                try
                {
                    if (patchAState == PatchSiteState.Vanilla)
                    {
                        WriteExecutableBytes(_patchAAddress, PatchAPatchedBytes);
                        _patchAWritten = true;
                    }
                    if (patchBState == PatchSiteState.Vanilla)
                    {
                        WriteExecutableBytes(_patchBAddress, RollFailurePatchedBytes);
                        _patchBWritten = true;
                    }
                    if (patchCState == PatchSiteState.Vanilla)
                    {
                        WriteExecutableBytes(_patchCAddress, RollFailurePatchedBytes);
                        _patchCWritten = true;
                    }

                    RequirePatchedBytes(_patchAAddress, PatchAPatchedBytes, "Patch A");
                    RequirePatchedBytes(_patchBAddress, RollFailurePatchedBytes, "Patch B");
                    RequirePatchedBytes(_patchCAddress, RollFailurePatchedBytes, "Patch C");
                }
                catch
                {
                    RollBackOwnedWrites();
                    throw;
                }

                _patched = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SkillMutationAlways enabled; " +
                    $"patchA=0x{_patchAAddress.ToInt64():X} " +
                    $"patchB=0x{_patchBAddress.ToInt64():X} " +
                    $"patchC=0x{_patchCAddress.ToInt64():X}.");
                LogUiGetterSignatures();
            }
            catch (Exception ex)
            {
                RollBackOwnedWrites();
                _patched = false;
                _enabled = false;
                if (_patchAWritten || _patchBWritten || _patchCWritten)
                {
                    MelonLogger.Error(
                        "[NocturneModernGameplay] SkillMutationAlways rollback incomplete; " +
                        "owned writes will be retried during shutdown.");
                }
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SkillMutationAlways disabled; " +
                    $"three-site patch refused safely: {ex}");
            }
#if false
            try
            {
                IntPtr method = GetNativeMethod(
                    "NativeMethodInfoPtr_rstChkSkillPowerUp1_Internal_Static_SByte_0");
                if (method == IntPtr.Zero)
                    throw new InvalidOperationException("rstChkSkillPowerUp1 address is unavailable");

                _patchAddress = IntPtr.Add(method, ChanceInstructionOffset);
                _firstStartCheckAddress = method;
                _secondStartCheckAddress = ResolveJumpTarget(GetNativeMethod(
                    "NativeMethodInfoPtr_rstChkSkillPowerUp2_Internal_Static_SByte_0"));
                IntPtr calculationThunk = GetNativeMethod(
                    "NativeMethodInfoPtr_rstCalcSkillPowerUp_Internal_Static_SByte_0");
                IntPtr calculationTarget = ResolveJumpTarget(calculationThunk);
                _inlinePatchAddress = IntPtr.Add(calculationTarget, ChanceInstructionOffset);
                _failureReturnAddress = IntPtr.Add(calculationTarget, FailureReturnOffset);
                _devilEligibilityAddress = GetNativeMethodByPrefix(
                    "NativeMethodInfoPtr_rstChkDevilSkillPowerUp_");
                _partyEligibilityAddress = GetNativeMethodByPrefix(
                    "NativeMethodInfoPtr_rstChkPartyDevilSkillPowerUp_");
                if (_devilEligibilityAddress == IntPtr.Zero || _partyEligibilityAddress == IntPtr.Zero)
                    throw new InvalidOperationException("mutation eligibility check address is unavailable");

                _devilEligibilityOriginal = ReadBytes(_devilEligibilityAddress, ReturnTrueBytes.Length);
                _partyEligibilityOriginal = ReadBytes(_partyEligibilityAddress, ReturnTrueBytes.Length);
                _firstStartCheckOriginal = ReadBytes(_firstStartCheckAddress, ReturnTrueBytes.Length);
                _secondStartCheckOriginal = ReadBytes(_secondStartCheckAddress, ReturnTrueBytes.Length);
                VerifyEligibilityPrologue(_devilEligibilityOriginal, "rstChkDevilSkillPowerUp");
                VerifyEligibilityPrologue(_partyEligibilityOriginal, "rstChkPartyDevilSkillPowerUp");
                VerifyEligibilityPrologue(_firstStartCheckOriginal, "rstChkSkillPowerUp1");
                VerifyEligibilityPrologue(_secondStartCheckOriginal, "rstChkSkillPowerUp2");

                VerifyPatchSite(_patchAddress, "rstChkSkillPowerUp1");
                VerifyPatchSite(_inlinePatchAddress, "rstCalcSkillPowerUp inline");
                VerifySequence(_failureReturnAddress, FailureReturnBytes,
                    JumpToCoreBytes, "rstCalcSkillPowerUp failure return");
                WriteExecutableBytes(_patchAddress, PatchedBytes);
                WriteExecutableBytes(_inlinePatchAddress, PatchedBytes);
                WriteExecutableBytes(_failureReturnAddress, JumpToCoreBytes);
                WriteExecutableBytes(_devilEligibilityAddress, ReturnTrueBytes);
                WriteExecutableBytes(_partyEligibilityAddress, ReturnTrueBytes);
                WriteExecutableBytes(_firstStartCheckAddress, ReturnTrueBytes);
                WriteExecutableBytes(_secondStartCheckAddress, ReturnTrueBytes);
                _patched = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION native chance patches applied; " +
                    $"helper=0x{_patchAddress.ToInt64():X} inline=0x{_inlinePatchAddress.ToInt64():X} " +
                    $"failureRedirect=0x{_failureReturnAddress.ToInt64():X} " +
                    $"devilEligibility=0x{_devilEligibilityAddress.ToInt64():X} " +
                    $"partyEligibility=0x{_partyEligibilityAddress.ToInt64():X} " +
                    $"startChecks=0x{_firstStartCheckAddress.ToInt64():X}/0x{_secondStartCheckAddress.ToInt64():X} " +
                    "chance=A8 03->A8 00 failure=return0->core eligibility/startChecks=return1.");
            }
            catch (Exception ex)
            {
                _patched = false;
                _enabled = false;
                MelonLogger.Error(
                    $"[NocturneModernGameplay] Native mutation chance patch refused safely: {ex}");
            }
#endif
        }

        private static void RestorePatch()
        {
            if (!_patched && !_patchAWritten && !_patchBWritten && !_patchCWritten) return;
            try
            {
                RollBackOwnedWrites();
                if (_patchAWritten || _patchBWritten || _patchCWritten)
                    throw new InvalidOperationException(
                        "one or more owned patch sites could not be restored");
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SkillMutationAlways three-site patch restored.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] Authoritative mutation chance restore failed: {ex}");
            }
            finally
            {
                _patched = false;
                if (!_patchAWritten && !_patchBWritten && !_patchCWritten)
                    ClearPatchAddresses();
            }
#if false
            if (_patchAddress == IntPtr.Zero) return;
            try
            {
                WriteExecutableBytes(_patchAddress, ExpectedBytes);
                if (_inlinePatchAddress != IntPtr.Zero)
                    WriteExecutableBytes(_inlinePatchAddress, ExpectedBytes);
                if (_failureReturnAddress != IntPtr.Zero)
                    WriteExecutableBytes(_failureReturnAddress, FailureReturnBytes);
                if (_devilEligibilityAddress != IntPtr.Zero && _devilEligibilityOriginal != null)
                    WriteExecutableBytes(_devilEligibilityAddress, _devilEligibilityOriginal);
                if (_partyEligibilityAddress != IntPtr.Zero && _partyEligibilityOriginal != null)
                    WriteExecutableBytes(_partyEligibilityAddress, _partyEligibilityOriginal);
                if (_firstStartCheckAddress != IntPtr.Zero && _firstStartCheckOriginal != null)
                    WriteExecutableBytes(_firstStartCheckAddress, _firstStartCheckOriginal);
                if (_secondStartCheckAddress != IntPtr.Zero && _secondStartCheckOriginal != null)
                    WriteExecutableBytes(_secondStartCheckAddress, _secondStartCheckOriginal);
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION native chance patch restored.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] Native mutation chance restore failed: {ex}");
            }
            finally { _patched = false; }
#endif
        }

        private static IntPtr GetNativeMethod(string fieldName)
        {
            FieldInfo? field = typeof(rstcalc).GetField(fieldName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            IntPtr methodInfo = field?.GetValue(null) is IntPtr value ? value : IntPtr.Zero;
            return methodInfo == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(methodInfo);
        }

        private static void LogUiGetterSignatures()
        {
            try
            {
                var signatures = new List<string>();
                foreach (Type type in new[] { typeof(datSkillName), typeof(datSkillHelp_msg) })
                {
                    foreach (MethodInfo method in type.GetMethods(
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!string.Equals(method.Name, "Get", StringComparison.Ordinal)) continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        string args = string.Join(",", Array.ConvertAll(parameters,
                            parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name));
                        signatures.Add($"{type.FullName}.{method.Name}({args})->{method.ReturnType.FullName}");
                    }
                }
                MelonLogger.Msg("[NocturneModernGameplay] SKILL-MUTATION UI getter signatures; " +
                    string.Join(" | ", signatures) + ".");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] UI getter signature probe failed: {ex.Message}");
            }
        }

        private static IntPtr GetNativeMethodByPrefix(string fieldPrefix)
        {
            foreach (FieldInfo field in typeof(rstcalc).GetFields(
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!field.Name.StartsWith(fieldPrefix, StringComparison.Ordinal)) continue;
                IntPtr methodInfo = field.GetValue(null) is IntPtr value ? value : IntPtr.Zero;
                if (methodInfo != IntPtr.Zero) return Marshal.ReadIntPtr(methodInfo);
            }
            return IntPtr.Zero;
        }

        private static void DumpResultCalculatorNativeCode()
        {
            try
            {
                IntPtr entry = GetNativeMethodByPrefix("NativeMethodInfoPtr_rstCalc_");
                if (entry == IntPtr.Zero)
                    throw new InvalidOperationException("rstCalc native entry is unavailable");

                IntPtr target = entry;
                if (Marshal.ReadByte(entry) == 0xE9)
                {
                    int displacement = Marshal.ReadInt32(entry, 1);
                    target = new IntPtr(entry.ToInt64() + 5L + displacement);
                }

                const int dumpLength = 0x8000;
                byte[] bytes = ReadBytes(target, dumpLength);
                string directory = @"C:\SMT3Modding\NocturneModernGameplay\diagnostics";
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "rstCalc-native.bin");
                File.WriteAllBytes(path, bytes);
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] SKILL-MUTATION rstCalc native dump; " +
                    $"entry=0x{entry.ToInt64():X} target=0x{target.ToInt64():X} " +
                    $"length=0x{dumpLength:X} path={path}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] rstCalc native dump failed: {ex}");
            }
        }

        private static void DumpNativeMethod(
            string fieldPrefix, string fileName, Type? owner = null)
        {
            try
            {
                Type type = owner ?? typeof(rstcalc);
                IntPtr entry = IntPtr.Zero;
                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!field.Name.StartsWith(fieldPrefix, StringComparison.Ordinal)) continue;
                    IntPtr methodInfo = field.GetValue(null) is IntPtr value ? value : IntPtr.Zero;
                    if (methodInfo != IntPtr.Zero) entry = Marshal.ReadIntPtr(methodInfo);
                    break;
                }
                if (entry == IntPtr.Zero)
                {
                    string available = string.Join(",", Array.ConvertAll(
                        type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
                        field => field.Name.Contains("DefaultSkill", StringComparison.Ordinal)
                            ? field.Name : string.Empty));
                    throw new InvalidOperationException(
                        $"{fieldPrefix} native entry is unavailable; candidates={available}");
                }

                IntPtr target = entry;
                if (Marshal.ReadByte(entry) == 0xE9)
                {
                    int displacement = Marshal.ReadInt32(entry, 1);
                    target = new IntPtr(entry.ToInt64() + 5L + displacement);
                }
                else if (Marshal.ReadByte(entry) == 0xFF && Marshal.ReadByte(entry, 1) == 0x25)
                {
                    int displacement = Marshal.ReadInt32(entry, 2);
                    IntPtr slot = new IntPtr(entry.ToInt64() + 6L + displacement);
                    target = Marshal.ReadIntPtr(slot);
                }
                if (VirtualQuery(target, out MemoryBasicInformation memory,
                    (UIntPtr)Marshal.SizeOf<MemoryBasicInformation>()) == UIntPtr.Zero ||
                    memory.State != 0x1000 || (memory.Protect & 0x100) != 0 ||
                    (memory.Protect & 0x01) != 0)
                    throw new InvalidOperationException(
                        $"resolved target 0x{target.ToInt64():X} is not readable committed memory");

                long regionEnd = memory.BaseAddress.ToInt64() +
                    checked((long)memory.RegionSize.ToUInt64());
                long remaining = regionEnd - target.ToInt64();
                int dumpLength = checked((int)Math.Min(0x4000L, remaining));
                if (dumpLength < 64)
                    throw new InvalidOperationException(
                        $"resolved target has only {dumpLength} readable bytes remaining");
                byte[] bytes = ReadBytes(target, dumpLength);
                string directory = @"C:\SMT3Modding\NocturneModernGameplay\diagnostics";
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, fileName);
                File.WriteAllBytes(path, bytes);
                MelonLogger.Msg($"[NocturneModernGameplay] native dump; method={fieldPrefix} " +
                    $"entry=0x{entry.ToInt64():X} target=0x{target.ToInt64():X} path={path}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] {fieldPrefix} dump failed: {ex.Message}");
            }
        }

        private static byte[] ReadBytes(IntPtr address, int length)
        {
            var bytes = new byte[length];
            Marshal.Copy(address, bytes, 0, length);
            return bytes;
        }

        private static void VerifyEligibilityPrologue(byte[] bytes, string name)
        {
            bool alreadyPatched = bytes.Length == ReturnTrueBytes.Length;
            for (int i = 0; i < bytes.Length && alreadyPatched; i++)
                alreadyPatched &= bytes[i] == ReturnTrueBytes[i];
            if (alreadyPatched) return;

            // rstChkDevil starts with sub rsp,28; the party-wide scan starts by
            // saving rbx. Reject unknown code so an updated executable is not patched.
            bool devilPrologue = bytes[0] == 0x48 && bytes[1] == 0x83 && bytes[2] == 0xEC;
            bool partyPrologue = bytes[0] == 0x48 && bytes[1] == 0x89 && bytes[2] == 0x5C;
            if (!devilPrologue && !partyPrologue)
                throw new InvalidOperationException(
                    $"{name} has unexpected prologue {BitConverter.ToString(bytes)}");
        }

        private static IntPtr ResolveJumpTarget(IntPtr thunk)
        {
            if (thunk == IntPtr.Zero || Marshal.ReadByte(thunk) != 0xE9)
                throw new InvalidOperationException("native method is not an E9 thunk");
            int displacement = Marshal.ReadInt32(thunk, 1);
            return new IntPtr(thunk.ToInt64() + 5L + displacement);
        }

        private static PatchSiteState VerifyPatchSite(
            IntPtr address, byte[] vanillaBytes, byte[] patchedBytes, string name)
        {
            byte[] actual = ReadBytes(address, vanillaBytes.Length);
            if (BytesEqual(actual, vanillaBytes))
            {
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] {name} verified; " +
                    $"address=0x{address.ToInt64():X} state=vanilla " +
                    $"bytes={FormatBytes(actual)}.");
                return PatchSiteState.Vanilla;
            }
            if (BytesEqual(actual, patchedBytes))
            {
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] {name} verified; " +
                    $"address=0x{address.ToInt64():X} state=already-patched " +
                    $"bytes={FormatBytes(actual)}.");
                return PatchSiteState.AlreadyPatched;
            }

            throw new InvalidOperationException(
                $"{name} verification failed at 0x{address.ToInt64():X}; " +
                $"expected={FormatBytes(vanillaBytes)} or {FormatBytes(patchedBytes)} " +
                $"actual={FormatBytes(actual)}");
        }

        private static IntPtr ResolveMutationHelperEntry()
        {
            IntPtr moduleBase = GetModuleHandle("GameAssembly.dll");
            if (moduleBase == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"GameAssembly.dll module base is unavailable; error={Marshal.GetLastWin32Error()}");

            long helperRva = MutationHelperNativeVa - GameAssemblyPreferredBase;
            IntPtr helperEntry = new IntPtr(checked(moduleBase.ToInt64() + helperRva));
            if (VirtualQuery(helperEntry, out MemoryBasicInformation memory,
                (UIntPtr)Marshal.SizeOf<MemoryBasicInformation>()) == UIntPtr.Zero ||
                memory.State != 0x1000 || (memory.Protect & 0x100) != 0 ||
                (memory.Protect & 0x01) != 0)
                throw new InvalidOperationException(
                    $"mutation helper 0x{helperEntry.ToInt64():X} is not readable committed memory");

            MelonLogger.Msg(
                "[NocturneModernGameplay] SkillMutationAlways helper resolved; " +
                $"moduleBase=0x{moduleBase.ToInt64():X} rva=0x{helperRva:X} " +
                $"entry=0x{helperEntry.ToInt64():X}.");
            return helperEntry;
        }

        private static void RequirePatchedBytes(IntPtr address, byte[] patchedBytes, string name)
        {
            byte[] actual = ReadBytes(address, patchedBytes.Length);
            if (!BytesEqual(actual, patchedBytes))
                throw new InvalidOperationException(
                    $"{name} post-write verification failed at 0x{address.ToInt64():X}; " +
                    $"expected={FormatBytes(patchedBytes)} actual={FormatBytes(actual)}");
        }

        private static void RollBackOwnedWrites()
        {
            RestoreOwnedSite(_patchCAddress, RollFailureVanillaBytes, ref _patchCWritten, "Patch C");
            RestoreOwnedSite(_patchBAddress, RollFailureVanillaBytes, ref _patchBWritten, "Patch B");
            RestoreOwnedSite(_patchAAddress, PatchAVanillaBytes, ref _patchAWritten, "Patch A");
        }

        private static void RestoreOwnedSite(
            IntPtr address, byte[] vanillaBytes, ref bool written, string name)
        {
            if (!written || address == IntPtr.Zero) return;
            try
            {
                WriteExecutableBytes(address, vanillaBytes);
                written = false;
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] {name} rollback failed at " +
                    $"0x{address.ToInt64():X}: {ex.Message}");
            }
        }

        private static void ClearPatchAddresses()
        {
            _patchAAddress = IntPtr.Zero;
            _patchBAddress = IntPtr.Zero;
            _patchCAddress = IntPtr.Zero;
            _patchAWritten = false;
            _patchBWritten = false;
            _patchCWritten = false;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static string FormatBytes(byte[] bytes) =>
            BitConverter.ToString(bytes).Replace("-", " ");

        private enum PatchSiteState
        {
            Vanilla,
            AlreadyPatched
        }

        private static void VerifySequence(
            IntPtr address, byte[] expectedBytes, byte[] replacementBytes, string name)
        {
            bool expected = true;
            bool patched = true;
            for (int i = 0; i < expectedBytes.Length; i++)
            {
                byte value = Marshal.ReadByte(address, i);
                expected &= value == expectedBytes[i];
                patched &= value == replacementBytes[i];
            }
            if (!expected && !patched)
                throw new InvalidOperationException($"{name} has unexpected instruction bytes");
        }

        private static void ProbeSecondChanceFunction()
        {
            try
            {
                IntPtr thunk = GetNativeMethod(
                    "NativeMethodInfoPtr_rstChkSkillPowerUp2_Internal_Static_SByte_0");
                if (thunk == IntPtr.Zero || Marshal.ReadByte(thunk) != 0xE9)
                    throw new InvalidOperationException("rstChkSkillPowerUp2 is not an E9 thunk");
                int displacement = Marshal.ReadInt32(thunk, 1);
                IntPtr target = new IntPtr(thunk.ToInt64() + 5L + displacement);
                if (VirtualQuery(target, out MemoryBasicInformation memory,
                    (UIntPtr)Marshal.SizeOf<MemoryBasicInformation>()) == UIntPtr.Zero ||
                    memory.State != 0x1000)
                    throw new InvalidOperationException(
                        $"resolved target 0x{target.ToInt64():X} is not committed memory");

                var bytes = new byte[128];
                Marshal.Copy(target, bytes, 0, bytes.Length);
                MelonLogger.Msg("[NocturneModernGameplay] MUTATION-NATIVE-2 " +
                    $"thunk=0x{thunk.ToInt64():X} target=0x{target.ToInt64():X} " +
                    $"protect=0x{memory.Protect:X} bytes={BitConverter.ToString(bytes).Replace("-", string.Empty)}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Second mutation chance probe failed safely: {ex.Message}");
            }
        }

        private static void WriteExecutableBytes(IntPtr address, byte[] bytes)
        {
            if (!VirtualProtect(address, (UIntPtr)bytes.Length, PageExecuteReadWrite, out uint oldProtect))
                throw new InvalidOperationException(
                    $"VirtualProtect failed with error {Marshal.GetLastWin32Error()}");
            try
            {
                Marshal.Copy(bytes, 0, address, bytes.Length);
                FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)bytes.Length);
            }
            finally
            {
                VirtualProtect(address, (UIntPtr)bytes.Length, oldProtect, out _);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(
            IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(
            IntPtr process, IntPtr address, UIntPtr size);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            internal IntPtr BaseAddress;
            internal IntPtr AllocationBase;
            internal uint AllocationProtect;
            internal ushort PartitionId;
            internal UIntPtr RegionSize;
            internal uint State;
            internal uint Protect;
            internal uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(
            IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class MutationEligibilityFlagPatch
    {
        private static bool Prepare() => false;
        private const uint MutationCandidateFlag = 0x40;
        private static readonly List<(Il2Cppnewdata_H.datUnitWork_t Stock, uint Flag)> Saved = new();
        private static bool _logged;

        private static void Prefix()
        {
            if (!SkillMutationAlways.IsEnabled) return;
            Saved.Clear();
            try
            {
                Il2Cppdds3GlobalWork_H.dds3GlobalWork_t global = dds3GlobalWork.DDS3_GBWK;
                for (int i = 0; i < global.stockcnt; i++)
                {
                    int unitIndex = global.stocklist[i];
                    if (unitIndex < 0 || unitIndex >= global.unitwork.Length) continue;
                    Il2Cppnewdata_H.datUnitWork_t stock = global.unitwork[unitIndex];
                    if (stock == null || stock.Pointer == IntPtr.Zero) continue;
                    Saved.Add((stock, stock.flag));
                    stock.flag |= MutationCandidateFlag;
                }
                if (!_logged)
                {
                    _logged = true;
                    MelonLogger.Msg(
                        $"[NocturneModernGameplay] SKILL-MUTATION rstCalc candidate flags forced; units={Saved.Count}.");
                }
            }
            catch (Exception ex)
            {
                RestoreFlags();
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Could not force rstCalc mutation flags safely: {ex.Message}");
            }
        }

        private static void Postfix() => RestoreFlags();

        private static void RestoreFlags()
        {
            foreach ((Il2Cppnewdata_H.datUnitWork_t stock, uint flag) in Saved)
            {
                try
                {
                    if (stock != null && stock.Pointer != IntPtr.Zero) stock.flag = flag;
                }
                catch { }
            }
            Saved.Clear();
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstRndGetPowerUpSkill))]
    internal static class MutationCandidateSelectionPatch
    {
        private static bool Prepare() => false;
        private static void Postfix(
            Il2Cppnewdata_H.datUnitWork_t __0,
            ref sbyte __1,
            ref ushort __result)
        {
            if (!SkillMutationAlways.IsEnabled || __0 == null || __0.Pointer == IntPtr.Zero) return;
            try
            {
                if (__0.skillcnt <= 0 || __0.skill.Length == 0) return;
                ushort skill = unchecked((ushort)__0.skill[0]);
                if (skill == 0) return;
                __1 = 0;
                // High Pixie (36) and Jack Frost (7) are both confirmed to
                // mutate successfully from slot zero. Do not probe the mapping
                // here: cmbGetMutationSkill advances native mutation state.
                __result = checked((ushort)(skill + 3));
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] SKILL-MUTATION candidate forced; " +
                    $"unit={__0.id} index=0 skill={skill} selector={__result}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Mutation candidate redirect failed safely: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class MutationCoreTriggerPatch
    {
        private static readonly HashSet<int> TriggeredUnits = new();

        private static void Postfix()
        {
            if (!SkillMutationAlways.IsEnabled) return;
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                if (seq.Current == 0)
                {
                    TriggeredUnits.Clear();
                }
            }
            catch { }
        }

        internal static void TryForceAtDefaultSkillBoundary()
        {
            if (!SkillMutationAlways.IsEnabled) return;
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                if (seq.Current != 8 || seq.Last != 21) return;
                Il2Cppnewdata_H.datUnitWork_t stock = work.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero ||
                    !TriggeredUnits.Add(stock.id)) return;

                sbyte result = rstcalc.rstCalcSkillPowerUpCore();
                if (result == 2)
                {
                    work.DefSkillResult = 0;
                }
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] SKILL-MUTATION core forced at result boundary; " +
                    $"unit={stock.id} result={result} skill={work.PUpSkillID} index={work.PUpSkillIndex}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Forced mutation core failed safely: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class MutationDefaultSkillBoundaryPatch
    {
        private static bool Prepare() => false;
        private static string _lastState = string.Empty;

        private static void Prefix()
        {
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                int unit = work.pCurrentStock == null || work.pCurrentStock.Pointer == IntPtr.Zero
                    ? -1 : work.pCurrentStock.id;
                string state = $"{seq.Current}/{seq.Last}/{seq.Change}/{unit}/{work.DefSkillResult}";
                if (!string.Equals(state, _lastState, StringComparison.Ordinal))
                {
                    _lastState = state;
                    MelonLogger.Msg(
                        $"[NocturneModernGameplay] SKILL-MUTATION default-skill boundary; " +
                        $"current={seq.Current} last={seq.Last} change={seq.Change} " +
                        $"unit={unit} defaultResult={work.DefSkillResult}.");
                }
            }
            catch { }
            MutationCoreTriggerPatch.TryForceAtDefaultSkillBoundary();
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class MutationResultCalculatorBoundaryPatch
    {
        private static bool Prepare() => false;
        private static string _lastState = string.Empty;

        private static void Prefix(Il2Cppkernel_H.dds3ProcessID_t __0) => Observe("before", __0);
        private static void Postfix(Il2Cppkernel_H.dds3ProcessID_t __0) => Observe("after", __0);

        private static void Observe(string phase, Il2Cppkernel_H.dds3ProcessID_t pid)
        {
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                int unit = work.pCurrentStock == null || work.pCurrentStock.Pointer == IntPtr.Zero
                    ? -1 : work.pCurrentStock.id;
                string state = $"{phase}/{pid}/{seq.Current}/{seq.Last}/{seq.Change}/{unit}/" +
                    $"{work.PUpSkillResult}/{work.DefSkillResult}";
                if (string.Equals(state, _lastState, StringComparison.Ordinal)) return;
                _lastState = state;
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] SKILL-MUTATION rstCalc boundary; " +
                    $"phase={phase} pid={pid} current={seq.Current} last={seq.Last} " +
                    $"change={seq.Change} unit={unit} powerResult={work.PUpSkillResult} " +
                    $"defaultResult={work.DefSkillResult}.");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    [HarmonyPriority(Priority.First)]
    internal static class MutationCoreReplacementPatch
    {
        // Superseded by the verified native three-site SkillMutationAlways patch.
        // Keep the original core and all vanilla eligibility/candidate rules intact.
        private static bool Prepare() => false;

        private static bool Prefix(ref sbyte __result)
        {
            if (!SkillMutationAlways.IsEnabled) return true;
            // The full-capacity Learn-as-New route temporarily reuses the
            // ordinary level-up learn/forget sequence. That sequence returns
            // through seq=8, where the authoritative 100% roll would otherwise
            // start another mutation for the same demon and loop indefinitely.
            if (SkillMutationLearnAsNew.SuppressNestedMutation)
            {
                __result = 0;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-MUTATION nested mutation suppressed " +
                    "during queued forget flow.");
                return false;
            }
            try
            {
                var work = rstinit.GBWK;
                Il2Cppnewdata_H.datUnitWork_t stock = work.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero ||
                    stock.skillcnt <= 0 || stock.skill.Length == 0) return true;

                const int index = 0;
                ushort original = unchecked((ushort)stock.skill[index]);
                if (original == 0) return true;
                SkillMutationTelemetry.RecordCandidate(
                    stock, index, checked((ushort)(original + 3)));

                ushort mutated = 0;
                for (int attempt = 0; attempt < 32 && mutated == 0; attempt++)
                    mutated = rstCalcCore.cmbGetMutationSkill(original, stock);
                if (mutated == 0)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] Forced mutation mapping unavailable; " +
                        $"unit={stock.id} original={original}.");
                    return true;
                }

                work.PUpSkillIndex = index;
                work.PUpSkillID = mutated;
                work.PUpSkillResult = 2;
                __result = 2;
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] SKILL-MUTATION core replaced; " +
                    $"unit={stock.id} index={index} original={original} mutated={mutated} result=2.");
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Mutation core replacement failed safely: {ex.Message}");
                return true;
            }
        }
    }
}
