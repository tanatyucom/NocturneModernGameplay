using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - EVENTPARAM DIRECT WRITER CAPTURE.
    //
    // Purpose: repeated static-disassembly attempts this session to trace
    // the exact call chain that writes GBWK.EventParam (+0x32) from a stale
    // value (e.g. 16, the just-inserted Power-Up target) to the reoffered
    // curriculum skill (e.g. 349) - during the SAME frame the bridge's own
    // forget episode lands back at seq8 - repeatedly identified the WRONG
    // function (rstcalc.rstGetDefaultSkill turned out to never even be
    // called during a full reproduction, confirmed via
    // GetDefaultSkillCallBoundaryTrace logging zero invocations despite a
    // GETDEFAULTSKILL-PATCH-STATUS confirming the Harmony patch itself was
    // correctly installed). Rather than continue guessing at the call
    // graph from static disassembly alone, this class reuses the PROVEN
    // hardware write-breakpoint technique from SkillCntWriterCaptureProbe
    // (same VEH/Dr0-Dr7 mechanism, already used earlier this session to
    // CONFIRM the skill[]/skillcnt writer instructions at VA
    // 0x19675E084/0x19675E08C) - this time watching &GBWK.EventParam
    // directly. Whatever instruction trips this breakpoint IS the real
    // writer, by construction - no call-graph guessing involved.
    //
    // GBWK is a persistent singleton (unlike pCurrentStock, which changes
    // per processed unit) - so the watched address is armed ONCE, the
    // first time gbwk.Pointer is observed, and never re-armed unless the
    // pointer is somehow seen to change (defensive, not expected).
    //
    // Requires exclusive use of Dr0-Dr3 - mutually exclusive with
    // PowerUpMutationCfgDiagnostics (temporarily disabled while this runs,
    // same trade-off already made earlier this session for
    // SkillCntWriterCaptureProbe).
    //
    // VEH safety discipline unchanged from SkillCntWriterCaptureProbe: the
    // VectoredHandler/CaptureHit path does ONLY raw Marshal reads/writes
    // and GetCurrentThreadId, into a preallocated fixed-size struct array.
    // No MelonLogger, no UnityEngine, no IL2CPP object/property access, no
    // string formatting, no allocation, no lock, from inside VEH - all of
    // that is deferred to FlushPendingLogs, called from Postfix in
    // ordinary managed context.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class EventParamWriterCaptureProbe
    {
        // Disabled after a hard crash near a high-load scene transition
        // (Hi-Pixie battle entry) - the very next frame after this probe's
        // watchpoint was armed and the DefaultSkill iterator rebuilt its
        // batch (EventNums 0->2), the game crashed with NO managed
        // exception logged (log cuts off mid-ordinary-processing, unrelated
        // to any forget/Power-Up flow) - the classic signature of a native-
        // level crash from the VEH/debug-register mechanism, not a bug in
        // this probe's own log-reading logic. Leading hypothesis (NOT yet
        // confirmed): GBWK.EventParam is a general-purpose scratch field
        // written far more frequently than the curriculum-skill scenario
        // alone, and a hardware breakpoint on it firing at very high
        // frequency during an already load-heavy transition destabilized
        // the process. Do not re-enable without first narrowing the arm
        // window (e.g. only while seq is in the forget-adjacent range)
        // rather than watching continuously for the whole session.
        internal static readonly bool Enabled = false;

        private struct PendingHit
        {
            internal long Dr6;
            internal long Rip;
            internal long Rsp;
            internal long Rflags;
            internal long WatchedAddress;
            internal int ThreadId;
            internal long FrameId;
            internal long Rax, Rbx, Rcx, Rdx, Rsi, Rdi, Rbp;
            internal long R8, R9, R10, R11, R12, R13, R14, R15;

            internal ushort EventParamAtHit;
            internal sbyte EventNumsAtHit;
            internal sbyte EventOfsAtHit;
            internal sbyte DefSkillResultAtHit;
            internal byte SeqCurrentAtHit;
            internal byte SeqLastAtHit;
            internal sbyte FlagAtHit;
        }

        private const int PendingCapacity = 32;
        private static readonly PendingHit[] _pending = new PendingHit[PendingCapacity];
        private static int _pendingCount;
        private static int _captureErrorCount;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr6 = 0x68;
        private const int OffsetDr7 = 0x70;
        private const int OffsetRax = 0x78;
        private const int OffsetRcx = 0x80;
        private const int OffsetRdx = 0x88;
        private const int OffsetRbx = 0x90;
        private const int OffsetRsp = 0x98;
        private const int OffsetRbp = 0xA0;
        private const int OffsetRsi = 0xA8;
        private const int OffsetRdi = 0xB0;
        private const int OffsetR8 = 0xB8;
        private const int OffsetR9 = 0xC0;
        private const int OffsetR10 = 0xC8;
        private const int OffsetR11 = 0xD0;
        private const int OffsetR12 = 0xD8;
        private const int OffsetR13 = 0xE0;
        private const int OffsetR14 = 0xE8;
        private const int OffsetR15 = 0xF0;
        private const int OffsetRip = 0xF8;
        private const int ResumeFlagBit = 0x10000;

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private const long Dr7WriteRw = 0x1;
        private const long Dr7Len2Bytes = 0x1; // EventParam is ushort (2 bytes)

        private static IntPtr _vehHandle = IntPtr.Zero;
        private static VectoredHandlerDelegate? _handlerDelegate;
        private static bool _installFailedPermanently;
        private static bool _vehRegistered;

        private static long _armedDr0Address;
        private static bool _armed;

        private static long _cachedGbwkPtr;
        private static long _frameCounter;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VectoredHandlerDelegate(IntPtr exceptionPointers);

        private static void Prefix()
        {
            if (!Enabled) return;
            try
            {
                if (_installFailedPermanently) return;

                if (PowerUpMutationCfgDiagnostics.Enabled)
                {
                    _installFailedPermanently = true;
                    if (_armed) DisarmWatchpoint();
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] EVENTPARAM-WRITER-CAPTURE refused: " +
                        "PowerUpMutationCfgDiagnostics.Enabled is true and already owns Dr0-Dr3.");
                    return;
                }

                EnsureVehRegistered();

                _frameCounter++;

                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                long gbwkPtr = gbwk.Pointer.ToInt64();

                if (gbwkPtr != _cachedGbwkPtr)
                {
                    _cachedGbwkPtr = gbwkPtr;
                    ArmWatchpoint(gbwkPtr + 0x32);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] EventParamWriterCaptureProbe prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!Enabled || _installFailedPermanently) return;
            try
            {
                FlushPendingLogs();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] EventParamWriterCaptureProbe postfix failed safely: {ex.Message}");
            }
        }

        internal static void Uninstall()
        {
            try
            {
                if (_armed) DisarmWatchpoint();
                if (_vehHandle != IntPtr.Zero)
                {
                    RemoveVectoredExceptionHandler(_vehHandle);
                    _vehHandle = IntPtr.Zero;
                }
                _vehRegistered = false;
                _handlerDelegate = null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] EventParamWriterCaptureProbe uninstall failed: {ex.Message}");
            }
        }

        private static void EnsureVehRegistered()
        {
            if (_vehRegistered) return;
            _handlerDelegate = VectoredHandler;
            _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
            if (_vehHandle == IntPtr.Zero)
            {
                _installFailedPermanently = true;
                MelonLogger.Error("[NocturneModernGameplay] EVENTPARAM-WRITER-CAPTURE: AddVectoredExceptionHandler failed.");
                return;
            }
            _vehRegistered = true;
            MelonLogger.Msg(
                "[NocturneModernGameplay] EVENTPARAM-WRITER-CAPTURE VEH installed (watching GBWK+0x32).");
        }

        private static void ArmWatchpoint(long dr0Address)
        {
            IntPtr ctx = AllocAlignedContext(out IntPtr rawAlloc);
            try
            {
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                IntPtr thread = GetCurrentThread();
                if (!GetThreadContext(thread, ctx))
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] EVENTPARAM-WRITER-CAPTURE GetThreadContext (arm) failed; error={Marshal.GetLastWin32Error()}.");
                    return;
                }

                Marshal.WriteInt64(ctx, OffsetDr0, dr0Address);
                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                const long clearMask = 0x1L | (0xFL << 16);
                dr7 &= ~clearMask;
                dr7 |= 0x1L; // L0
                dr7 |= (Dr7WriteRw << 16) | (Dr7Len2Bytes << 18); // RW0/LEN0
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);

                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                if (!SetThreadContext(thread, ctx))
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] EVENTPARAM-WRITER-CAPTURE SetThreadContext (arm) failed; error={Marshal.GetLastWin32Error()}.");
                    return;
                }

                _armedDr0Address = dr0Address;
                _armed = true;
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] EVENTPARAM-WRITER-CAPTURE armed; address=0x{dr0Address:X}.");
            }
            finally
            {
                Marshal.FreeHGlobal(rawAlloc);
            }
        }

        private static void DisarmWatchpoint()
        {
            IntPtr ctx = AllocAlignedContext(out IntPtr rawAlloc);
            try
            {
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                IntPtr thread = GetCurrentThread();
                if (!GetThreadContext(thread, ctx)) return;
                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                dr7 &= ~0x1L;
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                SetThreadContext(thread, ctx);
            }
            finally
            {
                Marshal.FreeHGlobal(rawAlloc);
                _armed = false;
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

        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                int ctxFlags = Marshal.ReadInt32(contextRecordPtr, OffsetContextFlags);
                bool hasDebugRegisters =
                    (unchecked((uint)ctxFlags) & ContextDebugRegisters) == ContextDebugRegisters;
                if (!hasDebugRegisters) return ExceptionContinueSearch;

                long dr6 = Marshal.ReadInt64(contextRecordPtr, OffsetDr6);
                bool b0 = (dr6 & 0x1L) != 0;
                if (!b0) return ExceptionContinueSearch;

                try { CaptureHit(contextRecordPtr, dr6); }
                catch { _captureErrorCount++; }

                Marshal.WriteInt64(contextRecordPtr, OffsetDr6, 0);

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

        private static void CaptureHit(IntPtr contextRecordPtr, long dr6)
        {
            if (_pendingCount >= PendingCapacity) return;

            ref PendingHit slot = ref _pending[_pendingCount];
            slot.Dr6 = dr6;
            slot.Rip = Marshal.ReadInt64(contextRecordPtr, OffsetRip);
            slot.Rsp = Marshal.ReadInt64(contextRecordPtr, OffsetRsp);
            slot.Rflags = Marshal.ReadInt32(contextRecordPtr, OffsetEFlags);
            slot.WatchedAddress = _armedDr0Address;
            slot.ThreadId = unchecked((int)GetCurrentThreadId());
            slot.FrameId = _frameCounter;
            slot.Rax = Marshal.ReadInt64(contextRecordPtr, OffsetRax);
            slot.Rbx = Marshal.ReadInt64(contextRecordPtr, OffsetRbx);
            slot.Rcx = Marshal.ReadInt64(contextRecordPtr, OffsetRcx);
            slot.Rdx = Marshal.ReadInt64(contextRecordPtr, OffsetRdx);
            slot.Rsi = Marshal.ReadInt64(contextRecordPtr, OffsetRsi);
            slot.Rdi = Marshal.ReadInt64(contextRecordPtr, OffsetRdi);
            slot.Rbp = Marshal.ReadInt64(contextRecordPtr, OffsetRbp);
            slot.R8 = Marshal.ReadInt64(contextRecordPtr, OffsetR8);
            slot.R9 = Marshal.ReadInt64(contextRecordPtr, OffsetR9);
            slot.R10 = Marshal.ReadInt64(contextRecordPtr, OffsetR10);
            slot.R11 = Marshal.ReadInt64(contextRecordPtr, OffsetR11);
            slot.R12 = Marshal.ReadInt64(contextRecordPtr, OffsetR12);
            slot.R13 = Marshal.ReadInt64(contextRecordPtr, OffsetR13);
            slot.R14 = Marshal.ReadInt64(contextRecordPtr, OffsetR14);
            slot.R15 = Marshal.ReadInt64(contextRecordPtr, OffsetR15);

            try
            {
                if (_cachedGbwkPtr != 0)
                {
                    IntPtr gbwk = new IntPtr(_cachedGbwkPtr);
                    slot.EventParamAtHit = unchecked((ushort)Marshal.ReadInt16(gbwk, 0x32));
                    slot.EventNumsAtHit = unchecked((sbyte)Marshal.ReadByte(gbwk, 0x30));
                    slot.EventOfsAtHit = unchecked((sbyte)Marshal.ReadByte(gbwk, 0x31));
                    slot.DefSkillResultAtHit = unchecked((sbyte)Marshal.ReadByte(gbwk, 0x3C));
                    slot.FlagAtHit = unchecked((sbyte)Marshal.ReadByte(gbwk, 0x7E));
                    long seqInfoPtr = Marshal.ReadInt64(gbwk, 0x10);
                    if (seqInfoPtr != 0)
                    {
                        slot.SeqCurrentAtHit = Marshal.ReadByte(new IntPtr(seqInfoPtr), 0x11);
                        slot.SeqLastAtHit = Marshal.ReadByte(new IntPtr(seqInfoPtr), 0x13);
                    }
                }
            }
            catch
            {
                // leave whichever fields were not yet set at their default
            }

            _pendingCount++;
        }

        private static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            for (int i = 0; i < count; i++) EmitLog(in _pending[i]);

            int errors = _captureErrorCount;
            if (errors > 0)
            {
                _captureErrorCount = 0;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] EVENTPARAM-WRITER-CAPTURE capture errors (safely dropped): {errors}.");
            }
        }

        private static void EmitLog(in PendingHit hit)
        {
            try
            {
                string ripLocation = DescribeAddress(hit.Rip);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] EVENTPARAM-WRITER-HIT; " +
                    $"frame={hit.FrameId}; watchedAddress=0x{hit.WatchedAddress:X}; threadId={hit.ThreadId}; " +
                    $"dr6=0x{hit.Dr6:X}; trapRip=0x{hit.Rip:X}; trapRipLocation={ripLocation}; " +
                    $"rsp=0x{hit.Rsp:X}; rflags=0x{hit.Rflags:X}; " +
                    $"eventParamAtHit={hit.EventParamAtHit}; eventNumsAtHit={hit.EventNumsAtHit}; " +
                    $"eventOfsAtHit={hit.EventOfsAtHit}; defSkillResultAtHit={hit.DefSkillResultAtHit}; " +
                    $"seqCurrentAtHit={hit.SeqCurrentAtHit}; seqLastAtHit={hit.SeqLastAtHit}; flagAtHit={hit.FlagAtHit}; " +
                    $"rax=0x{hit.Rax:X}; rbx=0x{hit.Rbx:X}; rcx=0x{hit.Rcx:X}; rdx=0x{hit.Rdx:X}; " +
                    $"rsi=0x{hit.Rsi:X}; rdi=0x{hit.Rdi:X}; rbp=0x{hit.Rbp:X}; " +
                    $"r8=0x{hit.R8:X}; r9=0x{hit.R9:X}; r10=0x{hit.R10:X}; r11=0x{hit.R11:X}; " +
                    $"r12=0x{hit.R12:X}; r13=0x{hit.R13:X}; r14=0x{hit.R14:X}; r15=0x{hit.R15:X}.");

                try
                {
                    const int before = 48;
                    const int after = 16;
                    IntPtr windowStart = new IntPtr(hit.Rip - before);
                    byte[] window = new byte[before + after];
                    Marshal.Copy(windowStart, window, 0, window.Length);
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] EVENTPARAM-WRITER-HIT-BYTES; " +
                        $"frame={hit.FrameId}; windowStart=0x{windowStart.ToInt64():X}; " +
                        $"tripRipOffsetInWindow={before}; bytesHex={BytesToHex(window)}.");
                }
                catch (Exception exBytes)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] EVENTPARAM-WRITER-HIT-BYTES read failed: {exBytes.Message}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] EVENTPARAM-WRITER-HIT emit failed safely: {ex.Message}");
            }
        }

        private static string DescribeAddress(long addr)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                foreach (System.Diagnostics.ProcessModule m in proc.Modules)
                {
                    long baseAddr = m.BaseAddress.ToInt64();
                    long size = m.ModuleMemorySize;
                    if (addr >= baseAddr && addr < baseAddr + size)
                    {
                        long rva = addr - baseAddr;
                        return $"{m.ModuleName}+0x{rva:X}";
                    }
                }
            }
            catch
            {
                // fall through
            }
            return "UNKNOWN-MODULE(heap/JIT-allocated or unmapped)";
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandlerDelegate handler);

        [DllImport("kernel32.dll")]
        private static extern uint RemoveVectoredExceptionHandler(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadContext(IntPtr hThread, IntPtr lpContext);
    }
}
