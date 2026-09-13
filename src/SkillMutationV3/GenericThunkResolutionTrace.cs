using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - DEFAULTSKILL OWNERSHIP WRITER
    // RUNTIME GENERIC THUNK RESOLUTION. Read-only observer only. Never
    // writes any field, never calls anything, never redirects control flow.
    //
    // Purpose: exhaustive static CFG tracing of rstUpdateSeqDefaultSkill
    // (this session) found no skill[]/skillcnt write anywhere in its own
    // body or in the callees confirmed clean so far
    // (0x182285b50 = a pure sourceObj+0xC8 notification counter,
    // 0x182169960 = a pure state-guard reading a wrapper object's +0x1c/
    // +0x1d/+0x18 fields, rstAddSkill = confirmed HP/MP recalc only,
    // 0x1815583e0->0x181674d60->0x1800950b0 (itself a thunk resolving to
    // 0x1800AB890, a generic exception/thread-context helper that ignores
    // its caller's argument entirely), 0x1827c0dd1 = confirmed pure
    // skillId-to-string/message formatting, no ownership touch).
    //
    // Two calls remain unresolved because disassembly shows they are NOT
    // fixed function bodies - they are IL2CPP lazily-resolved generic
    // method call thunks: each loads a cached function pointer from its
    // own static slot, resolves it via a metadata-driven runtime helper on
    // first use, caches the result, then tail-jumps to it. The actual
    // resolved targets cannot be determined from on-disk disassembly of
    // the thunks alone:
    //   - 0x1816F9470 (called directly from rstUpdateSeqDefaultSkill)
    //   - 0x182842C10 (called from within 0x1815583e0, feeding into the
    //     0x1816F9470 call's first argument)
    //
    // This class reads (never writes) both cache slots' live values,
    // before and after rstUpdateSeqDefaultSkill runs, to empirically
    // obtain the resolved pointers once both thunks have been exercised by
    // real gameplay - turning two opaque indirections into concrete VAs
    // that can then be disassembled statically like any other candidate.
    //
    // Cache slot VA derivation (confirmed via capstone disassembly of each
    // thunk, see .analysis/compute_thunk_cache_slot.py and
    // .analysis/disasm_0x182842c10_full.py):
    //   thunk 0x1816F9470:
    //     instruction VA    = 0x1816F947F  (mov rax, qword ptr [rip+0x172CAE2])
    //     instruction bytes = 48 8B 05 E2 CA 72 01 (7 bytes)
    //     instruction end   = 0x1816F9486
    //     cache slot VA     = 0x1816F9486 + 0x172CAE2 = 0x182E25F68
    //     (falls inside .debug, 0x182CD9000-0x18313D000 - writable, as
    //     expected for an IL2CPP resolved-pointer cache slot)
    //   thunk 0x182842C10:
    //     instruction VA    = 0x182842C1F  (mov rax, qword ptr [rip+0x5EBAA2])
    //     instruction bytes = 48 8B 05 A2 BA 5E 00 (7 bytes)
    //     instruction end   = 0x182842C26
    //     cache slot VA     = 0x182842C26 + 0x5EBAA2 = 0x182E2E6C8
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class GenericThunkResolutionTrace
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why.
        internal static readonly bool Enabled = false;
        private const long PreferredImageBase = 0x180000000;

        private struct ThunkSlot
        {
            internal string Name;
            internal long ThunkVA;
            internal long CacheSlotVA;
            internal bool LoggedOnce;
            internal long LastValue;
        }

        private static ThunkSlot[] _slots =
        {
            new ThunkSlot { Name = "DefaultSkill-direct", ThunkVA = 0x1816F9470, CacheSlotVA = 0x182E25F68 },
            new ThunkSlot { Name = "Via-0x1815583e0",     ThunkVA = 0x182842C10, CacheSlotVA = 0x182E2E6C8 },
        };

        private static IntPtr _moduleBase = IntPtr.Zero;
        private static long _moduleSize;

        private static void EnsureModule()
        {
            if (_moduleBase != IntPtr.Zero) return;
            try
            {
                var proc = Process.GetCurrentProcess();
                foreach (ProcessModule m in proc.Modules)
                {
                    if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        _moduleBase = m.BaseAddress;
                        _moduleSize = m.ModuleMemorySize;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] GenericThunkResolutionTrace module lookup failed safely: {ex.Message}");
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
            return "UNKNOWN-MODULE";
        }

        private static long ReadSlot(long cacheSlotVA)
        {
            long runtimeSlot = _moduleBase.ToInt64() + (cacheSlotVA - PreferredImageBase);
            return Marshal.ReadInt64(new IntPtr(runtimeSlot));
        }

        private static void Prefix()
        {
            if (!Enabled) return;
            try
            {
                EnsureModule();
                if (_moduleBase == IntPtr.Zero) return;
                for (int i = 0; i < _slots.Length; i++)
                {
                    _slots[i].LastValue = ReadSlot(_slots[i].CacheSlotVA);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] GenericThunkResolutionTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                if (_moduleBase == IntPtr.Zero) return;
                long moduleBaseVal = _moduleBase.ToInt64();

                for (int i = 0; i < _slots.Length; i++)
                {
                    long valueAfter = ReadSlot(_slots[i].CacheSlotVA);
                    long before = _slots[i].LastValue;

                    bool changed = valueAfter != before;
                    bool firstRun = !_slots[i].LoggedOnce;
                    _slots[i].LoggedOnce = true;
                    if (!changed && !firstRun) continue;

                    long resolvedRva = valueAfter == 0 ? 0 : valueAfter - moduleBaseVal;
                    long resolvedPreferredVA = valueAfter == 0 ? 0 : PreferredImageBase + resolvedRva;
                    bool isNull = valueAfter == 0;
                    bool isSelf = valueAfter == moduleBaseVal + (_slots[i].ThunkVA - PreferredImageBase);
                    string location = isNull ? "NULL" : DescribeAddress(valueAfter);
                    bool inGameAssembly = !isNull && location.StartsWith("GameAssembly.dll", StringComparison.OrdinalIgnoreCase);

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] GENERIC-THUNK-RESOLVE; " +
                        $"name={_slots[i].Name}; thunkVA=0x{_slots[i].ThunkVA:X}; cacheSlotVA=0x{_slots[i].CacheSlotVA:X}; " +
                        $"moduleBase=0x{moduleBaseVal:X}; " +
                        $"cacheValueBefore=0x{before:X}; cacheValueAfter=0x{valueAfter:X}; " +
                        $"isNull={isNull}; isSelf={isSelf}; inGameAssembly={inGameAssembly}; location={location}; " +
                        $"resolvedRva=0x{resolvedRva:X}; resolvedPreferredVA=0x{resolvedPreferredVA:X}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] GenericThunkResolutionTrace postfix failed safely: {ex.Message}");
            }
        }
    }
}
