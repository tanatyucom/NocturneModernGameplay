using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Phase A only: read-only reproduction of cmbGetMutationSkill's native
    // candidate filters. This does not change __result, the skill array, or
    // any GBWK/progression field. It exists to distinguish "no valid native
    // candidate" from "valid candidates existed but the 64 random draws all
    // missed them".
    [HarmonyPatch(typeof(rstCalcCore), nameof(rstCalcCore.cmbGetMutationSkill))]
    [HarmonyPriority(Priority.Last)]
    internal static class MutationCandidateDiagnostics
    {
        private const long CmbGetMutationSkillThunkRva = 0x227B6B0;
        private const int CandidateClassSlotInstructionOffset = 0x29;
        private const int Il2CppClassStaticFieldsOffset = 0xB8;
        private const int Il2CppArrayLengthOffset = 0x18;
        private const int Il2CppArrayVectorOffset = 0x20;
        private const int CandidateSkillIdOffset = 0x10;
        private const int MaximumReasonableCandidateCount = 4096;

        private static readonly object Sync = new();
        private static ushort[]? _candidateTable;
        private static string? _resolutionFailure;

        private static void Postfix(
            ushort __0,
            Il2Cppnewdata_H.datUnitWork_t __1,
            ushort __result)
        {
            int frame = UnityEngine.Time.frameCount;
            int unit = -1;
            try
            {
                IntPtr stockPtr = __1?.Pointer ?? IntPtr.Zero;
                if (__1 == null || stockPtr == IntPtr.Zero)
                {
                    LogUnavailable(frame, unit, __0, __result, "stock-invalid");
                    return;
                }

                unit = __1.id;
                if (!TryGetCandidateTable(out ushort[] candidates, out string failure))
                {
                    LogUnavailable(frame, unit, __0, __result, failure);
                    return;
                }

                byte originalGrade = rstCalcCore.cmbGetKeisyoSkillLevel(__0);
                var valid = new List<ushort>();
                int ownedRejected = 0;
                int eligibilityRejected = 0;
                int sameSkillRejected = 0;
                int gradeRejected = 0;

                foreach (ushort candidate in candidates)
                {
                    // Native order at helper+0x13A..0x1D9:
                    // owner -> stock eligibility -> same skill -> exact +1 grade.
                    if (fclCombineCalcCore.cmbChkSkillOwner(candidate, __1) >= 0)
                    {
                        ownedRejected++;
                        continue;
                    }

                    if (rstCalcCore.cmbCalcKeisyoSkillRate(__1, candidate) == 0)
                    {
                        eligibilityRejected++;
                        continue;
                    }

                    if (candidate == __0)
                    {
                        sameSkillRejected++;
                        continue;
                    }

                    byte candidateGrade = rstCalcCore.cmbGetKeisyoSkillLevel(candidate);
                    if (candidateGrade != originalGrade + 1)
                    {
                        gradeRejected++;
                        continue;
                    }

                    valid.Add(candidate);
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MUTATION-VALID-CANDIDATES; " +
                    $"frame={frame} unit={unit} original={__0} nativeResult={__result} " +
                    $"candidateCount={candidates.Length} validCount={valid.Count} " +
                    $"validCandidates=[{string.Join(",", valid)}] " +
                    $"rejectedOwned={ownedRejected} rejectedEligibility={eligibilityRejected} " +
                    $"rejectedSameSkill={sameSkillRejected} rejectedGrade={gradeRejected}.");

                if (__result == 0 && valid.Count > 0)
                {
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] MUTATION-RANDOM-SEARCH-MISS; " +
                        $"frame={frame} unit={unit} original={__0} " +
                        $"candidateCount={candidates.Length} validCount={valid.Count} " +
                        $"validCandidates=[{string.Join(",", valid)}].");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    "[NocturneModernGameplay] MUTATION-VALID-CANDIDATES failed safely; " +
                    $"frame={frame} unit={unit} original={__0} nativeResult={__result} " +
                    $"reason={ex.GetType().Name}:{ex.Message}.");
            }
        }

        private static void LogUnavailable(
            int frame, int unit, ushort original, ushort nativeResult, string reason)
        {
            MelonLogger.Warning(
                "[NocturneModernGameplay] MUTATION-VALID-CANDIDATES unavailable; " +
                $"frame={frame} unit={unit} original={original} nativeResult={nativeResult} " +
                $"reason={reason}.");
        }

        private static bool TryGetCandidateTable(
            out ushort[] candidates, out string failure)
        {
            lock (Sync)
            {
                if (_candidateTable != null)
                {
                    candidates = _candidateTable;
                    failure = string.Empty;
                    return true;
                }

                if (_resolutionFailure != null)
                {
                    candidates = Array.Empty<ushort>();
                    failure = _resolutionFailure;
                    return false;
                }

                try
                {
                    IntPtr moduleBase = GetModuleHandle("GameAssembly.dll");
                    if (moduleBase == IntPtr.Zero)
                        throw new InvalidOperationException("GameAssembly.dll module base unavailable");

                    IntPtr runtimeEntry = IntPtr.Add(
                        moduleBase, checked((int)CmbGetMutationSkillThunkRva));
                    byte[] runtimeEntryBytes = ReadBytes(runtimeEntry, 8);
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTATION-CANDIDATE-RESOLVER entry; " +
                        $"moduleBase=0x{moduleBase.ToInt64():X} " +
                        $"rva=0x{CmbGetMutationSkillThunkRva:X} " +
                        $"runtimeEntry=0x{runtimeEntry.ToInt64():X} " +
                        $"runtimeEntryBytes={FormatBytes(runtimeEntryBytes)}.");

                    // Harmony detours the live method entry before this Postfix runs,
                    // so the runtime bytes are not guaranteed to remain the original
                    // E9 IL2CPP thunk. Recover the authoritative original displacement
                    // from the exact GameAssembly.dll backing this loaded module, then
                    // map its target RVA back into the loaded image. This is still
                    // fail-closed: no alternative address is guessed.
                    string modulePath = GetLoadedModulePath(moduleBase);
                    byte[] image = File.ReadAllBytes(modulePath);
                    int thunkFileOffset = RvaToFileOffset(
                        image, checked((uint)CmbGetMutationSkillThunkRva));
                    if (image[thunkFileOffset] != 0xE9)
                    {
                        throw new InvalidOperationException(
                            $"on-disk cmbGetMutationSkill thunk opcode mismatch; " +
                            $"path={modulePath} rva=0x{CmbGetMutationSkillThunkRva:X} " +
                            $"actual={FormatBytes(image, thunkFileOffset, 8)}");
                    }

                    int thunkDisplacement = BitConverter.ToInt32(image, thunkFileOffset + 1);
                    long helperRva = CmbGetMutationSkillThunkRva + 5L + thunkDisplacement;
                    if (helperRva <= 0 || helperRva > uint.MaxValue)
                        throw new InvalidOperationException(
                            $"resolved helper RVA is out of range: 0x{helperRva:X}");
                    IntPtr helper = new(moduleBase.ToInt64() + helperRva);

                    // Exact bytes from the same on-disk GameAssembly image.
                    // 0x40 is a valid (semantically neutral here) REX prefix
                    // belonging to the first `push rbp`; it must not be
                    // dropped when validating the native entry.
                    byte[] expectedHelperPrologue =
                        { 0x40, 0x55, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x28 };
                    byte[] actualHelperPrologue = ReadBytes(helper, expectedHelperPrologue.Length);
                    if (!BytesEqual(actualHelperPrologue, expectedHelperPrologue))
                    {
                        throw new InvalidOperationException(
                            $"cmbGetMutationSkill helper prologue mismatch at 0x{helper.ToInt64():X}; " +
                            $"expected={FormatBytes(expectedHelperPrologue)} " +
                            $"actual={FormatBytes(actualHelperPrologue)}");
                    }
                    IntPtr slotInstruction = IntPtr.Add(helper, CandidateClassSlotInstructionOffset);
                    if (Marshal.ReadByte(slotInstruction, 0) != 0x48 ||
                        Marshal.ReadByte(slotInstruction, 1) != 0x8B ||
                        Marshal.ReadByte(slotInstruction, 2) != 0x0D)
                    {
                        throw new InvalidOperationException(
                            $"candidate class-slot instruction mismatch at 0x{slotInstruction.ToInt64():X}");
                    }

                    int slotDisplacement = Marshal.ReadInt32(slotInstruction, 3);
                    IntPtr classSlot = new(slotInstruction.ToInt64() + 7L + slotDisplacement);
                    IntPtr classPtr = Marshal.ReadIntPtr(classSlot);
                    IntPtr staticFields = classPtr == IntPtr.Zero
                        ? IntPtr.Zero
                        : Marshal.ReadIntPtr(classPtr, Il2CppClassStaticFieldsOffset);
                    IntPtr arrayPtr = staticFields == IntPtr.Zero
                        ? IntPtr.Zero
                        : Marshal.ReadIntPtr(staticFields);
                    if (classPtr == IntPtr.Zero || staticFields == IntPtr.Zero || arrayPtr == IntPtr.Zero)
                        throw new InvalidOperationException("candidate table pointer chain contains null");

                    int count = Marshal.ReadInt32(arrayPtr, Il2CppArrayLengthOffset);
                    if (count <= 0 || count > MaximumReasonableCandidateCount)
                        throw new InvalidOperationException($"candidate table length out of range: {count}");

                    var resolved = new ushort[count];
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr entry = Marshal.ReadIntPtr(
                            arrayPtr, Il2CppArrayVectorOffset + checked(i * IntPtr.Size));
                        if (entry == IntPtr.Zero)
                            throw new InvalidOperationException($"candidate table entry {i} is null");
                        resolved[i] = unchecked((ushort)Marshal.ReadInt16(entry, CandidateSkillIdOffset));
                    }

                    _candidateTable = resolved;
                    candidates = resolved;
                    failure = string.Empty;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTATION-CANDIDATE-TABLE resolved read-only; " +
                        $"runtimeEntry=0x{runtimeEntry.ToInt64():X} " +
                        $"runtimeEntryBytes={FormatBytes(runtimeEntryBytes)} " +
                        $"helperRva=0x{helperRva:X} helper=0x{helper.ToInt64():X} " +
                        $"classSlot=0x{classSlot.ToInt64():X} " +
                        $"array=0x{arrayPtr.ToInt64():X} count={count}.");
                    return true;
                }
                catch (Exception ex)
                {
                    _resolutionFailure = $"{ex.GetType().Name}:{ex.Message}";
                    candidates = Array.Empty<ushort>();
                    failure = _resolutionFailure;
                    return false;
                }
            }
        }

        private static string GetLoadedModulePath(IntPtr moduleBase)
        {
            var path = new StringBuilder(1024);
            uint length = GetModuleFileName(moduleBase, path, path.Capacity);
            if (length == 0 || length >= path.Capacity)
                throw new InvalidOperationException(
                    $"GetModuleFileName failed for GameAssembly.dll; error={Marshal.GetLastWin32Error()}");
            return path.ToString();
        }

        private static int RvaToFileOffset(byte[] image, uint rva)
        {
            if (image.Length < 0x100)
                throw new InvalidOperationException("GameAssembly.dll is too small for a PE image");

            int peOffset = BitConverter.ToInt32(image, 0x3C);
            if (peOffset < 0 || peOffset + 0x18 > image.Length ||
                image[peOffset] != (byte)'P' || image[peOffset + 1] != (byte)'E' ||
                image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
                throw new InvalidOperationException("GameAssembly.dll PE header is invalid");

            ushort sectionCount = BitConverter.ToUInt16(image, peOffset + 6);
            ushort optionalHeaderSize = BitConverter.ToUInt16(image, peOffset + 20);
            int sectionTable = checked(peOffset + 24 + optionalHeaderSize);
            for (int i = 0; i < sectionCount; i++)
            {
                int section = checked(sectionTable + i * 40);
                if (section < 0 || section + 40 > image.Length)
                    throw new InvalidOperationException("GameAssembly.dll section table is truncated");

                uint virtualSize = BitConverter.ToUInt32(image, section + 8);
                uint virtualAddress = BitConverter.ToUInt32(image, section + 12);
                uint rawSize = BitConverter.ToUInt32(image, section + 16);
                uint rawOffset = BitConverter.ToUInt32(image, section + 20);
                uint mappedSize = Math.Max(virtualSize, rawSize);
                if (rva < virtualAddress || (ulong)rva >= (ulong)virtualAddress + mappedSize)
                    continue;

                ulong fileOffset = (ulong)rawOffset + (rva - virtualAddress);
                if (fileOffset >= (ulong)image.Length)
                    throw new InvalidOperationException(
                        $"RVA 0x{rva:X} maps outside GameAssembly.dll");
                return checked((int)fileOffset);
            }

            throw new InvalidOperationException(
                $"RVA 0x{rva:X} is not covered by a GameAssembly.dll section");
        }

        private static byte[] ReadBytes(IntPtr address, int length)
        {
            var bytes = new byte[length];
            Marshal.Copy(address, bytes, 0, length);
            return bytes;
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

        private static string FormatBytes(byte[] bytes, int offset, int count)
        {
            int available = Math.Min(count, bytes.Length - offset);
            if (available <= 0) return string.Empty;
            var slice = new byte[available];
            Buffer.BlockCopy(bytes, offset, slice, 0, available);
            return FormatBytes(slice);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileName(
            IntPtr module, StringBuilder fileName, int size);
    }
}
