using System;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - full gate-cascade capture for the dedicated
    // target==8 presentation path (read-only, no writes). Session
    // continuation after Hidden9thSlotPathTrace CONFIRMED a decisive
    // asymmetry: for High Pixie's AddNew-bridge case, target==8 reliably
    // reached the branch body (VA 0x1822DA48C) and completed the
    // cmpSetupObject(skillCurObj[8], true) call; for Frost's AddNew-bridge
    // case, CursorPos.Shift stayed 8 for the ENTIRE seq21/22 episode
    // (~850 frames) yet the branch entry point was hit ZERO times.
    //
    // 2026-09-14 CORRECTION (important): the first version of this class
    // tried to replicate gates 3-7 (the ones hanging off GBWK+0xB8's own
    // object chain) purely in Tick() (ordinary managed context) by
    // independently re-reading that chain every frame, reasoning that
    // "gate 1's operand is GBWK's own static slot, so the rest is just
    // memory GBWK already exposes." A real-machine run proved that
    // reasoning wrong: even during High Pixie's CONFIRMED-successful
    // episode, the independently-read byte at chainC+0x11 was 0x6F, never
    // the expected 0x15 - the SAME shared "active window/dialog type tag"
    // this exact byte-offset check pattern also gates the normal ebx=0..7
    // highlight loop with (VA 0x1822D97C0) gets read and compared by MANY
    // different draw calls for MANY different UI subsystems within a
    // single frame, so its value is highly volatile moment-to-moment - an
    // out-of-band Tick() read samples some OTHER subsystem's momentary
    // check, not the one this exact branch saw at its own exact instant.
    // Native register/argument values (like r12) don't have this problem
    // (they're this call's own local state, not a shared mutable global),
    // but this chain does.
    //
    // Fix: read every volatile gate with its OWN hardware EXECUTE
    // breakpoint, placed at the fallthrough (pass) address immediately
    // after that gate's conditional jump - so a hit is real proof, at the
    // exact native instant, that every gate up to and including that one
    // passed. Full disassembly of VA 0x1822DA3FC-0x1822DA486 (this
    // session's earlier work) found the gate order:
    //   1. r12 != null                              (VA 0x1822DA3FC/3FF)
    //   2. r12.Length(dword @+0x18) > 0              (VA 0x1822DA401/407)
    //   3. GBWK.field_0xb8.field_0x0 != null         (VA 0x1822DA41E/421)
    //   4. that_object.field_0x10 != null            (VA 0x1822DA427/42E)
    //   5. that_object.byte_0x11 == 0x15 (volatile)  (VA 0x1822DA434/438)
    //   6. (...).field_0x88 != null                  (VA 0x1822DA463/466)
    //   7. (...).field_0x20 != null                  (VA 0x1822DA46C/473)
    //   8. Shift(byte@+0x14)+Index(word@+0x12) == 8  (VA 0x1822DA483/486)
    // Windows exposes exactly 4 hardware breakpoint slots (DR0-DR3), one
    // short of 8 distinct checkpoints, so this uses a ladder of 4 that
    // still fully disambiguates every gate:
    //   DR0 = 0x1822DA3FC (cascade start - also captures r12 itself, the
    //         one input no global memory read can substitute for)
    //   DR1 = 0x1822DA434 (proves gates 1-4 passed, BEFORE the volatile
    //         tag comparison - reached regardless of what the tag turns
    //         out to be)
    //   DR2 = 0x1822DA463 (proves gates 1-5 passed, i.e. the volatile tag
    //         really was 0x15 at this exact native instant)
    //   DR3 = 0x1822DA46C (proves gates 1-6 passed, i.e. field_0x88 was
    //         non-null too)
    // Gate 7/8 are already answered by Hidden9thSlotPathTrace's own
    // BRANCH-ENTRY checkpoint at this same VA 0x1822DA48C (gates 1-8 all
    // passed) - if DR3 here fires but that one never does in the same
    // test run, gate 7 (field_0x20 null) is the culprit by elimination
    // (gate 8/target==8 is independently already confirmed true via
    // SkillCursorFieldTrace's own CursorPos.Shift readings for both
    // cases).
    internal static class Hidden9thGateCascadeTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;

        private const long CascadeStartVa = 0x1822DA3FCL; // "test r12,r12"
        private static readonly byte[] CascadeStartBytes = { 0x4D, 0x85, 0xE4 };

        private const long Gate4PassVa = 0x1822DA434L; // "cmp byte[rcx+0x11],0x15" - gates 1-4 passed to reach here
        private static readonly byte[] Gate4PassBytes = { 0x80, 0x79, 0x11, 0x15 };

        private const long Gate5PassVa = 0x1822DA463L; // "test rbx,rbx" - gates 1-5 passed to reach here
        private static readonly byte[] Gate5PassBytes = { 0x48, 0x85, 0xDB };

        private const long Gate6PassVa = 0x1822DA46CL; // "mov rax,[rbx+0x20]" - gates 1-6 passed to reach here
        private static readonly byte[] Gate6PassBytes = { 0x48, 0x8B, 0x43, 0x20 };

        private const int MaxArmedFrames = 36000; // ~10 minutes at 60fps - generous for a multi-scenario test session
        private static int _armedAtFrame = -1;

        private struct PendingHit
        {
            internal int Frame;
            internal int Seq;
            internal bool BridgeActive;
            internal int Unit;
            internal int Checkpoint; // 0=cascadeStart(r12), 1=gate4Pass, 2=gate5Pass, 3=gate6Pass
            internal long R12;
        }

        private const int MaxHits = 64;
        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;
        private static long _totalHitCount;

        // Cached once per Tick() call (ordinary managed context, main
        // thread) so the VEH can read them as plain fields - never by
        // calling into IL2CPP/Unity itself.
        private static int _cachedFrame;
        private static int _cachedSeq = -1;
        private static bool _cachedBridgeActive;
        private static int _cachedUnit = -1;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr1 = 0x50;
        private const int OffsetDr2 = 0x58;
        private const int OffsetDr3 = 0x60;
        private const int OffsetR12 = 0xD8;
        private const int OffsetDr7 = 0x70;
        private const int ResumeFlagBit = 0x10000;

        // L0-L3 (bits 0,2,4,6) enabled; all four RW/LEN nibbles (bits
        // 16-31) left at 00 (execute), matching this mod's existing
        // execute-breakpoint probes.
        private const long Dr7EnableAllFourMask = 0x1L | 0x4L | 0x10L | 0x40L;
        private const long Dr7RwLenAllFourMask = 0xFFFFL << 16;

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private static long _actualModuleBase;
        private static IntPtr _addrCascadeStart = IntPtr.Zero;
        private static IntPtr _addrGate4Pass = IntPtr.Zero;
        private static IntPtr _addrGate5Pass = IntPtr.Zero;
        private static IntPtr _addrGate6Pass = IntPtr.Zero;
        private static IntPtr _vehHandle = IntPtr.Zero;
        private static VectoredHandlerDelegate? _handlerDelegate;
        private static bool _installed;
        private static bool _installAttempted;
        private static bool _uninstalled;

        // Called from ModMain.OnUpdate every frame (ordinary managed
        // context only).
        internal static void Tick()
        {
            if (!Enabled || _uninstalled) return;

            try
            {
                _cachedFrame = UnityEngine.Time.frameCount;

                var gbwk = rstinit.GBWK;
                if (gbwk != null && gbwk.Pointer != IntPtr.Zero)
                {
                    try { _cachedSeq = gbwk.SeqInfo.Current; } catch { }
                    try
                    {
                        var stock = gbwk.pCurrentStock;
                        _cachedUnit = (stock != null && stock.Pointer != IntPtr.Zero) ? stock.id : -1;
                    }
                    catch { _cachedUnit = -1; }
                }
                _cachedBridgeActive = FullCapacityAddNewBridgeState.Active;

                if (!_installed)
                {
                    var raw = UnityEngine.Object.FindObjectsOfType(
                        Il2CppInterop.Runtime.Il2CppType.Of<statusUI>());
                    if (raw == null || raw.Length == 0) return; // not in gameplay yet - try again next frame

                    Install();
                    if (_installed) _armedAtFrame = _cachedFrame;
                    return;
                }

                int elapsed = _cachedFrame - _armedAtFrame;
                if (elapsed >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] HIDDEN9THGATE-AUTOUNINSTALL; " +
                        $"totalHits={_totalHitCount}; armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Hidden9thGateCascadeTrace.Tick failed safely: {ex.Message}");
            }
        }

        private static void Install()
        {
            if (_installed || _installAttempted) return;
            _installAttempted = true;
            try
            {
                IntPtr moduleBase = GetModuleHandle("GameAssembly.dll");
                if (moduleBase == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"GameAssembly.dll module base unavailable; error={Marshal.GetLastWin32Error()}");
                _actualModuleBase = moduleBase.ToInt64();

                _addrCascadeStart = ResolveAndVerify(moduleBase, CascadeStartVa, CascadeStartBytes, "cascade-start");
                _addrGate4Pass = ResolveAndVerify(moduleBase, Gate4PassVa, Gate4PassBytes, "gate4-pass");
                _addrGate5Pass = ResolveAndVerify(moduleBase, Gate5PassVa, Gate5PassBytes, "gate5-pass");
                _addrGate6Pass = ResolveAndVerify(moduleBase, Gate6PassVa, Gate6PassBytes, "gate6-pass");

                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointsOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HIDDEN9THGATE-INSTALLED; " +
                    $"cascadeStart=0x{_addrCascadeStart.ToInt64():X}; gate4Pass=0x{_addrGate4Pass.ToInt64():X}; " +
                    $"gate5Pass=0x{_addrGate5Pass.ToInt64():X}; gate6Pass=0x{_addrGate6Pass.ToInt64():X}; " +
                    "mechanism=hardware-execute-breakpoint x4(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] Hidden9thGateCascadeTrace install refused safely: {ex}");
                Uninstall();
            }
        }

        private static IntPtr ResolveAndVerify(IntPtr moduleBase, long va, byte[] expected, string label)
        {
            IntPtr address = new IntPtr(checked(moduleBase.ToInt64() + (va - GameAssemblyPreferredBase)));
            byte[] actual = new byte[expected.Length];
            Marshal.Copy(address, actual, 0, actual.Length);
            if (!BytesEqual(actual, expected))
                throw new InvalidOperationException(
                    $"{label} unrecognized bytes; expected=[{FormatBytes(expected)}]; actual={FormatBytes(actual)} - refusing to install");
            return address;
        }

        internal static void Uninstall()
        {
            try
            {
                if (_addrCascadeStart != IntPtr.Zero)
                {
                    try { RemoveHardwareBreakpointsOnCurrentThread(); }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning(
                            $"[NocturneModernGameplay] Hidden9thGateCascadeTrace breakpoint removal failed: {ex.Message}");
                    }
                }
                if (_vehHandle != IntPtr.Zero)
                {
                    RemoveVectoredExceptionHandler(_vehHandle);
                    _vehHandle = IntPtr.Zero;
                }
            }
            finally
            {
                _installed = false;
                _uninstalled = true;
                _handlerDelegate = null;
                _addrCascadeStart = IntPtr.Zero;
                _addrGate4Pass = IntPtr.Zero;
                _addrGate5Pass = IntPtr.Zero;
                _addrGate6Pass = IntPtr.Zero;
            }
        }

        private static void InstallHardwareBreakpointsOnCurrentThread()
        {
            IntPtr ctx = AllocAlignedContext(out IntPtr rawAlloc);
            try
            {
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                IntPtr thread = GetCurrentThread();
                if (!GetThreadContext(thread, ctx))
                    throw new InvalidOperationException(
                        $"GetThreadContext failed; error={Marshal.GetLastWin32Error()}");

                Marshal.WriteInt64(ctx, OffsetDr0, _addrCascadeStart.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr1, _addrGate4Pass.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr2, _addrGate5Pass.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr3, _addrGate6Pass.ToInt64());

                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                long mask = Dr7EnableAllFourMask | Dr7RwLenAllFourMask;
                dr7 = (dr7 & ~mask) | Dr7EnableAllFourMask;
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);

                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                if (!SetThreadContext(thread, ctx))
                    throw new InvalidOperationException(
                        $"SetThreadContext failed; error={Marshal.GetLastWin32Error()}");

                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                if (!GetThreadContext(thread, ctx))
                    throw new InvalidOperationException(
                        $"post-install GetThreadContext readback failed; error={Marshal.GetLastWin32Error()}");

                long dr7Rb = Marshal.ReadInt64(ctx, OffsetDr7);
                if ((dr7Rb & Dr7EnableAllFourMask) != Dr7EnableAllFourMask)
                    throw new InvalidOperationException(
                        $"hardware breakpoint readback mismatch; dr7=0x{dr7Rb:X}");
            }
            finally
            {
                Marshal.FreeHGlobal(rawAlloc);
            }
        }

        private static void RemoveHardwareBreakpointsOnCurrentThread()
        {
            IntPtr ctx = AllocAlignedContext(out IntPtr rawAlloc);
            try
            {
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                IntPtr thread = GetCurrentThread();
                if (!GetThreadContext(thread, ctx)) return;
                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                dr7 &= ~Dr7EnableAllFourMask;
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                SetThreadContext(thread, ctx);
            }
            finally
            {
                Marshal.FreeHGlobal(rawAlloc);
            }
        }

        private static IntPtr AllocAlignedContext(out IntPtr rawAlloc)
        {
            rawAlloc = Marshal.AllocHGlobal(ContextBufferSize + 16);
            long aligned = (rawAlloc.ToInt64() + 15L) & ~15L;
            IntPtr ctx = new IntPtr(aligned);
            for (int i = 0; i < ContextBufferSize; i++) Marshal.WriteByte(ctx, i, 0);
            return ctx;
        }

        // Runs inside #DB exception dispatch. Four plain address compares
        // and (on match) a couple of plain register/struct writes - no
        // logging, no IL2CPP access, no allocation.
        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                IntPtr exceptionAddress = Marshal.ReadIntPtr(exceptionRecordPtr, 0x10);
                int checkpoint;
                if (exceptionAddress == _addrCascadeStart) checkpoint = 0;
                else if (exceptionAddress == _addrGate4Pass) checkpoint = 1;
                else if (exceptionAddress == _addrGate5Pass) checkpoint = 2;
                else if (exceptionAddress == _addrGate6Pass) checkpoint = 3;
                else return ExceptionContinueSearch;

                _totalHitCount++;

                if (_pendingCount < MaxHits)
                {
                    long r12 = checkpoint == 0 ? Marshal.ReadInt64(contextRecordPtr, OffsetR12) : 0;

                    ref PendingHit slot = ref _pending[_pendingCount];
                    slot.Frame = _cachedFrame;
                    slot.Seq = _cachedSeq;
                    slot.BridgeActive = _cachedBridgeActive;
                    slot.Unit = _cachedUnit;
                    slot.Checkpoint = checkpoint;
                    slot.R12 = r12;
                    _pendingCount++;
                }

                int eflags = Marshal.ReadInt32(contextRecordPtr, OffsetEFlags);
                eflags |= ResumeFlagBit;
                Marshal.WriteInt32(contextRecordPtr, OffsetEFlags, eflags);

                return ExceptionContinueExecution;
            }
            catch
            {
                return ExceptionContinueSearch;
            }
        }

        private static readonly string[] CheckpointNames =
        {
            "0:cascadeStart(gate1/2=r12)",
            "1:gate4Pass(chainC!=null)",
            "2:gate5Pass(stateTag==0x15)",
            "3:gate6Pass(field0x88!=null)",
        };

        // Ordinary managed context only - called from ModMain.OnUpdate,
        // never from the VEH.
        internal static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            for (int i = 0; i < count; i++)
            {
                ref PendingHit hit = ref _pending[i];
                string r12Str = hit.Checkpoint == 0 ? $"; r12=0x{hit.R12:X}" : "";
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HIDDEN9THGATE-HIT; " +
                    $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                    $"checkpoint={CheckpointNames[hit.Checkpoint]}{r12Str}.");
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static string FormatBytes(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", " ");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VectoredHandlerDelegate(IntPtr exceptionPointers);

        [DllImport("kernel32.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandlerDelegate handler);

        [DllImport("kernel32.dll")]
        private static extern uint RemoveVectoredExceptionHandler(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadContext(IntPtr hThread, IntPtr lpContext);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }
}
