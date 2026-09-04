using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Chance mode shared by SkillMutationChanceControl and
    // SkillPowerUpChanceControl.
    internal enum NativeChanceMode
    {
        Native,
        Always,
        Disabled
    }

    // Shared raw byte-patch infrastructure for the SkillMutation.Chance /
    // SkillPowerUp.Chance native control points. This is a zero-base
    // implementation of the same verify/apply/restore discipline
    // SkillMutationAlways.cs already established for the old Patch A/B/C
    // (fail-closed: refuse to touch a site whose current bytes are neither
    // vanilla nor this code's own known patched form) - it does not read,
    // call, or otherwise depend on SkillMutationAlways.cs. That file is
    // retired from runtime (no longer initialized/registered - see
    // ModMain.cs / GameplayFeatureRegistry.cs) rather than modified, so its
    // own implementation is left untouched.
    internal static class NativeChancePatchUtility
    {
        private const uint PageExecuteReadWrite = 0x40;
        private const long GameAssemblyPreferredBase = 0x180000000L;

        internal static IntPtr ResolveModuleBase()
        {
            IntPtr moduleBase = GetModuleHandle("GameAssembly.dll");
            if (moduleBase == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"GameAssembly.dll module base unavailable; error={Marshal.GetLastWin32Error()}");
            return moduleBase;
        }

        internal static IntPtr ResolveVa(IntPtr moduleBase, long va)
        {
            long rva = va - GameAssemblyPreferredBase;
            return new IntPtr(checked(moduleBase.ToInt64() + rva));
        }

        internal static byte[] ReadBytes(IntPtr address, int length)
        {
            var bytes = new byte[length];
            Marshal.Copy(address, bytes, 0, length);
            return bytes;
        }

        internal static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        internal static string FormatBytes(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", " ");

        // Fail-closed verification: the live bytes at `address` must equal
        // EXACTLY one of `vanilla` or `patched` (this class's own known
        // patched form). Any other byte sequence (another mod, a game
        // update, corruption) is refused rather than guessed at - logs and
        // returns false without throwing, so callers can decide how to
        // aggregate multiple site failures before aborting as a whole.
        internal static bool VerifyKnownState(
            IntPtr address, byte[] vanilla, byte[] patched, string name, out bool isPatched)
        {
            byte[] actual = ReadBytes(address, vanilla.Length);
            if (BytesEqual(actual, vanilla)) { isPatched = false; return true; }
            if (BytesEqual(actual, patched)) { isPatched = true; return true; }
            isPatched = false;
            MelonLogger.Error(
                $"[NocturneModernGameplay] {name} unexpected bytes at 0x{address.ToInt64():X}; " +
                $"expected vanilla={FormatBytes(vanilla)} or patched={FormatBytes(patched)}, " +
                $"actual={FormatBytes(actual)}. Refusing to touch this site.");
            return false;
        }

        internal static void WriteExecutableBytes(IntPtr address, byte[] bytes)
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
    }
}
