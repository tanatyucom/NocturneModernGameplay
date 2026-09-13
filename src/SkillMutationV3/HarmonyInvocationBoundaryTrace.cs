using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - HARMONY / IL2CPP INVOCATION BOUNDARY
    // INVESTIGATION. Read-only observer only. Never writes any field, byte,
    // or register. Never installs a detour/breakpoint - this is strictly
    // memory reads and reflection metadata reads.
    //
    // Purpose: prior steps CONFIRMED (raw Marshal reads, bypassing every
    // managed wrapper) that the SAME native datUnitWork_s memory, at the
    // SAME pCurrentStock pointer and the SAME skill[] array object, changes
    // from skillcnt=N/skill[N]=0 to skillcnt=N+1/skill[N]=pending32 inside
    // the Harmony Prefix/Postfix boundary around rstupdate.
    // rstUpdateSeqDefaultSkill - while exhaustive static disassembly of
    // that native function (VA 0x182288790, identity confirmed 3
    // independent ways) and every reachable callee (direct, IL2CPP lazy-
    // resolved generic thunks, and the GC-safepoint helper) found no
    // skill[]/skillcnt write anywhere. This class checks the next most
    // basic question before considering exotic explanations: is Harmony
    // actually wrapping the native function directly (an inline
    // prologue/detour patch at 0x182288790 itself), or is there an
    // intermediate managed wrapper / IL2CPP interop stub / trampoline layer
    // this investigation has not yet looked at?
    //
    // Two independent, purely-observational checks, done once (not per
    // invocation - these are static facts about the current process, not
    // per-call state):
    //   1. Reflection: get the MethodBase for rstupdate.
    //      rstUpdateSeqDefaultSkill, dump Harmony.GetPatchInfo (all
    //      registered prefixes/postfixes/owners/priorities - authoritative,
    //      not inferred from source grep), and dump every static IntPtr-
    //      valued field on the declaring type whose name mentions the
    //      method (the Il2CppInterop-generated "NativeMethodInfoPtr_*"
    //      pattern, if present) plus the Il2CppMethodInfo.methodPointer
    //      value read from it (methodPointer is documented to be the FIRST
    //      field of the native MethodInfo struct, offset 0).
    //   2. Raw byte comparison: read 128 live bytes from the runtime-
    //      relocated address of VA 0x182288790 (GameAssembly.dll module
    //      base + RVA) and compare them, byte for byte, against the known
    //      on-disk bytes at the same RVA (hardcoded below, extracted this
    //      session via .analysis/dump_defaultskill_bytes.py). Identical
    //      bytes mean no inline prologue detour was written at the
    //      function's own entry; a mismatch (typically a short jmp/call at
    //      the very start) means Harmony (or something else) redirected
    //      execution before the original prologue - in which case the
    //      first differing bytes are decoded as a possible jmp/call target
    //      to report where they redirect to.
    //
    // Per-invocation (lightweight, gated on an actual skillcnt change so
    // volume stays comparable to this investigation's other diagnostics):
    // managed thread id and a Stopwatch tick timestamp captured at the very
    // start of Prefix and the very start of Postfix, to let Prefix/native-
    // call/Postfix ordering and thread identity be checked directly instead
    // of inferred.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class HarmonyInvocationBoundaryTrace
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why.
        internal static readonly bool Enabled = false;

        private const long GameAssemblyPreferredBase = 0x180000000L;
        private const long DefaultSkillVA = 0x182288790L;

        // Captured this session via .analysis/dump_defaultskill_bytes.py,
        // reading GameAssembly.dll from disk at RVA (DefaultSkillVA -
        // GameAssemblyPreferredBase), 128 bytes.
        private const string DiskBytesHex =
            "4883EC28803D1743BA000075128B0D59378300E848E0E5FDC6050343BA0001" +
            "488B0D9A8CBA00F6812F01000002740E83B9E0000000007505E8930DDFFD33" +
            "C948895C2420E877D3FFFF3C017576488B05D4DDBB00488B88B8000000488B" +
            "41084885C00F84F5020000488B80D00000004885C00F84E5020000837818000F86E102";

        private static bool _oneTimeDumpDone;

        private static long _prefixTicks;
        private static int _prefixThreadId;
        private static long _prefixRawSkillCnt = long.MinValue;
        private static long _prefixPCurrentStock;

        private static void Prefix()
        {
            if (!Enabled) return;
            try
            {
                if (!_oneTimeDumpDone)
                {
                    _oneTimeDumpDone = true;
                    RunOneTimeDump();
                }

                _prefixTicks = Stopwatch.GetTimestamp();
                _prefixThreadId = Thread.CurrentThread.ManagedThreadId;
                CaptureRawStockState(out _prefixPCurrentStock, out _prefixRawSkillCnt);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HarmonyInvocationBoundaryTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                long postfixTicks = Stopwatch.GetTimestamp();
                int postfixThreadId = Thread.CurrentThread.ManagedThreadId;
                CaptureRawStockState(out long postfixPCurrentStock, out long postfixRawSkillCnt);

                if (_prefixRawSkillCnt == long.MinValue) return;
                if (postfixRawSkillCnt == _prefixRawSkillCnt) return; // unchanged, not an event

                double elapsedMs = (postfixTicks - _prefixTicks) * 1000.0 / Stopwatch.Frequency;
                bool sameThread = postfixThreadId == _prefixThreadId;
                bool samePointer = postfixPCurrentStock == _prefixPCurrentStock;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] HARMONY-BOUNDARY-TIMING; " +
                    $"prefixThreadId={_prefixThreadId}; postfixThreadId={postfixThreadId}; sameThread={sameThread}; " +
                    $"prefixPCurrentStock=0x{_prefixPCurrentStock:X}; postfixPCurrentStock=0x{postfixPCurrentStock:X}; samePointer={samePointer}; " +
                    $"rawSkillCntBefore={_prefixRawSkillCnt}; rawSkillCntAfter={postfixRawSkillCnt}; " +
                    $"elapsedMsPrefixToPostfix={elapsedMs:F4}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HarmonyInvocationBoundaryTrace postfix failed safely: {ex.Message}");
            }
        }

        private static void CaptureRawStockState(out long pCurrentStockPtr, out long rawSkillCnt)
        {
            pCurrentStockPtr = 0;
            rawSkillCnt = long.MinValue;
            var gbwk = rstinit.GBWK;
            if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
            long stockField = Marshal.ReadInt64(gbwk.Pointer, 0x60);
            pCurrentStockPtr = stockField;
            if (stockField == 0) return;
            rawSkillCnt = Marshal.ReadInt32(new IntPtr(stockField), 0x48);
        }

        // ---- one-time, static-fact dumps (reflection + raw byte compare) ----

        private static void RunOneTimeDump()
        {
            DumpHarmonyPatchInfo();
            DumpNativeMethodInfoFields();
            DumpRuntimeVsDiskBytes();
        }

        private static void DumpHarmonyPatchInfo()
        {
            try
            {
                Type declaringType = typeof(rstupdate);
                MethodBase? method = declaringType.GetMethod(
                    nameof(rstupdate.rstUpdateSeqDefaultSkill),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

                if (method == null)
                {
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] HARMONY-BOUNDARY-PATCHINFO; " +
                        "could not reflect MethodBase for rstupdate.rstUpdateSeqDefaultSkill.");
                    return;
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] HARMONY-BOUNDARY-METHODBASE; " +
                    $"declaringType={method.DeclaringType}; name={method.Name}; " +
                    $"methodHandleValue=0x{method.MethodHandle.Value.ToInt64():X}.");

                var patches = HarmonyLib.Harmony.GetPatchInfo(method);
                if (patches == null)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] HARMONY-BOUNDARY-PATCHINFO; " +
                        "Harmony.GetPatchInfo returned null (no patches registered via this Harmony instance's bookkeeping).");
                    return;
                }

                LogPatchGroup("Prefix", patches.Prefixes);
                LogPatchGroup("Postfix", patches.Postfixes);
                LogPatchGroup("Transpiler", patches.Transpilers);
                LogPatchGroup("Finalizer", patches.Finalizers);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HARMONY-BOUNDARY-PATCHINFO dump failed safely: {ex.Message}");
            }
        }

        private static void LogPatchGroup(string kind, System.Collections.Generic.IReadOnlyCollection<Patch> patchList)
        {
            if (patchList == null || patchList.Count == 0)
            {
                MelonLogger.Msg($"[NocturneModernGameplay] HARMONY-BOUNDARY-PATCHLIST; kind={kind}; count=0.");
                return;
            }
            foreach (var p in patchList)
            {
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HARMONY-BOUNDARY-PATCHLIST; " +
                    $"kind={kind}; owner={p.owner}; index={p.index}; priority={p.priority}; " +
                    $"patchMethod={p.PatchMethod.DeclaringType}.{p.PatchMethod.Name}; before=[{string.Join(",", p.before)}]; after=[{string.Join(",", p.after)}].");
            }
        }

        private static void DumpNativeMethodInfoFields()
        {
            try
            {
                Type declaringType = typeof(rstupdate);
                var fields = declaringType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int matched = 0;
                foreach (var f in fields)
                {
                    if (f.FieldType != typeof(IntPtr)) continue;
                    if (f.Name.IndexOf("DefaultSkill", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    matched++;
                    object? value = f.GetValue(null);
                    if (value is not IntPtr ptr)
                    {
                        MelonLogger.Msg(
                            $"[NocturneModernGameplay] HARMONY-BOUNDARY-NATIVEFIELD; field={f.Name}; value=non-IntPtr.");
                        continue;
                    }

                    long methodPointerValue = 0;
                    bool readOk = false;
                    if (ptr != IntPtr.Zero)
                    {
                        try { methodPointerValue = Marshal.ReadInt64(ptr, 0); readOk = true; }
                        catch { readOk = false; }
                    }

                    string moduleInfo = "n/a";
                    long resolvedRva = 0;
                    if (readOk && methodPointerValue != 0)
                    {
                        IntPtr gaBase = GetModuleBase("GameAssembly.dll");
                        if (gaBase != IntPtr.Zero)
                        {
                            long gaBaseVal = gaBase.ToInt64();
                            resolvedRva = methodPointerValue - gaBaseVal;
                            long preferredVa = GameAssemblyPreferredBase + resolvedRva;
                            bool matchesDefaultSkillVa = preferredVa == DefaultSkillVA;
                            moduleInfo = $"GameAssembly.dll+0x{resolvedRva:X} (preferredVA=0x{preferredVa:X}; matchesDiskDefaultSkillVA={matchesDefaultSkillVa})";
                        }
                        else
                        {
                            moduleInfo = "GameAssembly.dll base unavailable";
                        }
                    }

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] HARMONY-BOUNDARY-NATIVEFIELD; " +
                        $"field={f.Name}; methodInfoPtr=0x{ptr.ToInt64():X}; " +
                        $"methodPointerFieldReadOk={readOk}; methodPointerValue=0x{methodPointerValue:X}; location={moduleInfo}.");
                }

                if (matched == 0)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] HARMONY-BOUNDARY-NATIVEFIELD; " +
                        "no static IntPtr field on rstupdate matched name filter 'DefaultSkill'.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HARMONY-BOUNDARY-NATIVEFIELD dump failed safely: {ex.Message}");
            }
        }

        private static void DumpRuntimeVsDiskBytes()
        {
            try
            {
                IntPtr gaBase = GetModuleBase("GameAssembly.dll");
                if (gaBase == IntPtr.Zero)
                {
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES; GameAssembly.dll module base unavailable.");
                    return;
                }

                long rva = DefaultSkillVA - GameAssemblyPreferredBase;
                IntPtr runtimeAddr = new IntPtr(checked(gaBase.ToInt64() + rva));

                byte[] diskBytes = HexToBytes(DiskBytesHex);
                byte[] liveBytes = new byte[diskBytes.Length];
                Marshal.Copy(runtimeAddr, liveBytes, 0, liveBytes.Length);

                bool identical = true;
                int firstDiffIndex = -1;
                for (int i = 0; i < diskBytes.Length; i++)
                {
                    if (diskBytes[i] != liveBytes[i])
                    {
                        identical = false;
                        firstDiffIndex = i;
                        break;
                    }
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES; " +
                    $"diskVA=0x{DefaultSkillVA:X}; runtimeAddr=0x{runtimeAddr.ToInt64():X}; " +
                    $"identical={identical}; firstDiffIndex={firstDiffIndex}; " +
                    $"liveBytesHex={BytesToHex(liveBytes)}.");

                if (!identical)
                {
                    int remaining = liveBytes.Length - firstDiffIndex;
                    long instrAddr = runtimeAddr.ToInt64() + firstDiffIndex;

                    if (remaining >= 5 && (liveBytes[firstDiffIndex] == 0xE9 || liveBytes[firstDiffIndex] == 0xE8))
                    {
                        int rel = BitConverter.ToInt32(liveBytes, firstDiffIndex + 1);
                        long target = instrAddr + 5 + rel;
                        string kind = liveBytes[firstDiffIndex] == 0xE9 ? "jmp rel32" : "call rel32";
                        LogDetourTarget(kind, firstDiffIndex, target, gaBase);
                    }
                    else if (remaining >= 6 && liveBytes[firstDiffIndex] == 0xFF && liveBytes[firstDiffIndex + 1] == 0x25)
                    {
                        // FF 25 <disp32> = jmp qword ptr [rip+disp32] - an
                        // indirect jump through a memory-resident pointer
                        // slot (the classic x64 "jmp [rip+0]; dq target"-
                        // style absolute detour, needed on x64 because a
                        // direct jmp cannot reach an arbitrary 64-bit target
                        // in 5 bytes). The pointer SLOT address is
                        // instrAddr+6+disp32; the slot's CONTENT (read here,
                        // never written) is the actual final destination.
                        int disp32 = BitConverter.ToInt32(liveBytes, firstDiffIndex + 2);
                        long instrEnd = instrAddr + 6;
                        long pointerSlot = instrEnd + disp32;
                        long finalTarget = 0;
                        bool slotReadOk = false;
                        try
                        {
                            finalTarget = Marshal.ReadInt64(new IntPtr(pointerSlot));
                            slotReadOk = true;
                        }
                        catch (Exception exSlot)
                        {
                            MelonLogger.Warning(
                                "[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES-DETOUR; " +
                                $"jmp [rip+disp32] pointer slot 0x{pointerSlot:X} unreadable: {exSlot.Message}");
                        }

                        MelonLogger.Msg(
                            "[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES-DETOUR; " +
                            $"kind=jmp [rip+disp32] (indirect, absolute-via-memory); atOffset={firstDiffIndex}; " +
                            $"disp32=0x{disp32:X}; pointerSlot=0x{pointerSlot:X}; " +
                            $"slotReadOk={slotReadOk}; finalTarget=0x{finalTarget:X}; " +
                            $"pointerSlotLocation={DescribeAddress(pointerSlot)}.");

                        if (slotReadOk && finalTarget != 0)
                        {
                            LogDetourTarget("indirect-jmp-final-target", firstDiffIndex, finalTarget, gaBase);
                        }
                    }
                    else
                    {
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES-DETOUR; " +
                            "first differing byte is not a recognized jmp/call rel32 or jmp [rip+disp32] opcode; " +
                            "no automatic target interpretation performed.");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES dump failed safely: {ex.Message}");
            }
        }

        private static void LogDetourTarget(string kind, int atOffset, long target, IntPtr gaBase)
        {
            long targetRvaFromGa = target - gaBase.ToInt64();
            long targetPreferredVa = GameAssemblyPreferredBase + targetRvaFromGa;
            string location = DescribeAddress(target);
            bool insideGameAssembly = location.StartsWith("GameAssembly.dll", StringComparison.OrdinalIgnoreCase);

            MelonLogger.Msg(
                "[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES-DETOUR-TARGET; " +
                $"kind={kind}; atOffset={atOffset}; targetRuntimeAddr=0x{target:X}; " +
                $"insideGameAssembly={insideGameAssembly}; " +
                $"targetRvaFromGameAssemblyBase=0x{targetRvaFromGa:X}; targetPreferredVaIfInGameAssembly=0x{targetPreferredVa:X}; " +
                $"location={location}.");

            if (insideGameAssembly)
            {
                try
                {
                    byte[] preview = new byte[32];
                    Marshal.Copy(new IntPtr(target), preview, 0, preview.Length);
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES-DETOUR-PREVIEW; " +
                        $"kind={kind}; previewBytesHex={BytesToHex(preview)}.");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] HARMONY-BOUNDARY-BYTES-DETOUR-PREVIEW read failed: {ex.Message}");
                }
            }
        }

        private static string DescribeAddress(long addr)
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                foreach (ProcessModule m in proc.Modules)
                {
                    long baseAddr = m.BaseAddress.ToInt64();
                    long size = m.ModuleMemorySize;
                    if (addr >= baseAddr && addr < baseAddr + size)
                    {
                        return $"{m.ModuleName}+0x{(addr - baseAddr):X}";
                    }
                }
            }
            catch
            {
                // fall through to UNKNOWN below
            }
            return "UNKNOWN-MODULE(heap/JIT-allocated or unmapped)";
        }

        private static byte[] HexToBytes(string hex)
        {
            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return result;
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static IntPtr GetModuleBase(string moduleName)
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                foreach (ProcessModule m in proc.Modules)
                {
                    if (string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                        return m.BaseAddress;
                }
            }
            catch
            {
                // fall through to IntPtr.Zero below
            }
            return IntPtr.Zero;
        }
    }
}
