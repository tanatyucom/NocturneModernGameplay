using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Mutation AddNew investigation (investigations/ACQUISITION_LEARNASNEW/
    // PLAN.md) - EVENTPARAM ACTUAL WRITER TRACE (2026-09-15).
    //
    // Purpose: MutationAddNewCalcUpdateTimingTrace's point1 (Harmony Postfix
    // on rstcalc.rstCalcEventInfo) failed to capture the write that produced
    // the FINAL EventParam value consumed later (observed: point1's last
    // logged value was 387, but the next UpdateSeqDefaultSkill.Prefix showed
    // 396 - same gap independently confirmed by the pre-existing
    // DefaultSkillIteratorTrace, which also missed it). This means either
    // rstCalcEventInfo has an uncaptured call path, or a second writer
    // exists. A hardware write-breakpoint on the field itself resolves this
    // unambiguously, by construction, regardless of which managed method
    // performs the write.
    //
    // This is a NARROWED reattempt of EventParamWriterCaptureProbe (same
    // codebase, currently Enabled=false after a hard crash near a high-load
    // scene transition during that probe's UNBOUNDED whole-session arming).
    // That probe's own postmortem comment already names the likely cause:
    // GBWK.EventParam is written far more often than just the curriculum
    // scenario, and holding the hardware breakpoint armed continuously for
    // the entire session raised the odds of it firing during a load-heavy
    // transition. This class keeps the exact same proven VEH/Dr0-Dr7
    // mechanism (same struct layout, same context offsets) but bounds the
    // armed window tightly: auto-disarm after MaxHits captures OR
    // MaxArmedFrames frames, whichever comes first - a single test episode
    // is all that is needed (User instruction, 2026-09-15), not
    // whole-session monitoring.
    //
    // Read-only from the game's perspective: no GameAssembly.dll bytes are
    // written, only this thread's CPU debug registers (Dr0/Dr7), exactly
    // like every other hardware-breakpoint probe already in this mod.
    [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.rstupdate), nameof(Il2Cpp.rstupdate.rstUpdate))]
    internal static class EventParamActualWriterTrace
    {
        // Disabled after a crash during the first test episode (2026-09-15,
        // User report) - even with the bounded arm window (MaxHits=8,
        // MaxArmedFrames=1800), the hardware write-breakpoint mechanism
        // crashed the game. Do not re-enable without first understanding
        // what happened (see investigations/ACQUISITION_LEARNASNEW/PLAN.md).
        internal static readonly bool Enabled = false;

        private const int MaxHits = 8;
        private const int MaxArmedFrames = 1800; // ~30s at 60fps - one test episode, not whole-session

        private struct PendingHit
        {
            internal long TrapRip;
            internal ushort OldValue;
            internal ushort NewValue;
            internal int Seq;
            internal int Unit;
            internal sbyte DefSkillResult;
            internal sbyte PUpSkillResult;
        }

        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;
        private static int _totalHits;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr6 = 0x68;
        private const int OffsetDr7 = 0x70;
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
        private static bool _autoDisarmed;

        private static long _armedDr0Address;
        private static bool _armed;
        private static int _armedAtFrame = -1;

        private static long _cachedGbwkPtr;
        private static ushort _lastKnownEventParam;
        private static bool _hasLastKnown;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VectoredHandlerDelegate(IntPtr exceptionPointers);

        private static void Prefix()
        {
            if (!Enabled || _installFailedPermanently || _autoDisarmed) return;
            try
            {
                // Dr0-Dr3 ownership check, same discipline as every other
                // hardware-breakpoint probe in this mod:
                // PowerUpMutationBit6RawProbe only installs when
                // PowerUpMutationCfgDiagnostics.Enabled (currently false),
                // and CurriculumGateChainTrace's Tick()/FlushPendingLogs()
                // calls are commented out in ModMain (retired, never
                // installs) - so this probe has exclusive access.
                EnsureVehRegistered();
                if (_installFailedPermanently) return;

                int frame = UnityEngine.Time.frameCount;

                var gbwk = Il2Cpp.rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                long gbwkPtr = gbwk.Pointer.ToInt64();

                if (!_armed && gbwkPtr != 0)
                {
                    _cachedGbwkPtr = gbwkPtr;
                    _lastKnownEventParam = unchecked((ushort)Marshal.ReadInt16(new IntPtr(gbwkPtr), 0x32));
                    _hasLastKnown = true;
                    ArmWatchpoint(gbwkPtr + 0x32);
                    _armedAtFrame = frame;
                    return;
                }

                if (_armed && _armedAtFrame >= 0 && (frame - _armedAtFrame) >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] EVENTPARAM-ACTUALWRITER-AUTODISARM; " +
                        $"reason=frame-budget-exhausted; totalHits={_totalHits}; armedFrames={frame - _armedAtFrame}.");
                    DisarmWatchpoint();
                    _autoDisarmed = true;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] EventParamActualWriterTrace prefix failed safely: {ex.Message}");
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
                    $"[NocturneModernGameplay] EventParamActualWriterTrace postfix failed safely: {ex.Message}");
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
                MelonLogger.Warning($"[NocturneModernGameplay] EventParamActualWriterTrace uninstall failed: {ex.Message}");
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
                MelonLogger.Error("[NocturneModernGameplay] EVENTPARAM-ACTUALWRITER: AddVectoredExceptionHandler failed.");
                return;
            }
            _vehRegistered = true;
            MelonLogger.Msg(
                "[NocturneModernGameplay] EVENTPARAM-ACTUALWRITER VEH installed " +
                $"(watching GBWK+0x32, maxHits={MaxHits}, maxArmedFrames={MaxArmedFrames}).");
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
                        $"[NocturneModernGameplay] EVENTPARAM-ACTUALWRITER GetThreadContext (arm) failed; error={Marshal.GetLastWin32Error()}.");
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
                        $"[NocturneModernGameplay] EVENTPARAM-ACTUALWRITER SetThreadContext (arm) failed; error={Marshal.GetLastWin32Error()}.");
                    return;
                }

                _armedDr0Address = dr0Address;
                _armed = true;
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] EVENTPARAM-ACTUALWRITER-ARMED; address=0x{dr0Address:X}.");
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

        // Runs inside #DB exception dispatch. Only raw Marshal reads and
        // GetCurrentThreadId - no MelonLogger, no IL2CPP object access, no
        // allocation, no string formatting, matching every other VEH probe
        // in this mod.
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

                try { CaptureHit(contextRecordPtr); }
                catch { /* drop this hit, never crash the handler */ }

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

        private static void CaptureHit(IntPtr contextRecordPtr)
        {
            if (_pendingCount >= MaxHits) return;

            long rip = Marshal.ReadInt64(contextRecordPtr, OffsetRip);
            ushort oldValue = _hasLastKnown ? _lastKnownEventParam : (ushort)0;
            ushort newValue = 0;
            int seq = -1;
            int unit = -1;
            sbyte defSkillResult = 0;
            sbyte pUpSkillResult = 0;

            if (_cachedGbwkPtr != 0)
            {
                IntPtr gbwk = new IntPtr(_cachedGbwkPtr);
                newValue = unchecked((ushort)Marshal.ReadInt16(gbwk, 0x32));
                defSkillResult = unchecked((sbyte)Marshal.ReadByte(gbwk, 0x3C));
                pUpSkillResult = unchecked((sbyte)Marshal.ReadByte(gbwk, 0x4B));
                long stockPtr = Marshal.ReadInt64(gbwk, 0x60);
                if (stockPtr != 0)
                {
                    unit = Marshal.ReadInt16(new IntPtr(stockPtr), 0x14) & 0xFFFF;
                }
                long seqInfoPtr = Marshal.ReadInt64(gbwk, 0x10);
                if (seqInfoPtr != 0)
                {
                    seq = Marshal.ReadByte(new IntPtr(seqInfoPtr), 0x11);
                }
            }

            ref PendingHit slot = ref _pending[_pendingCount];
            slot.TrapRip = rip;
            slot.OldValue = oldValue;
            slot.NewValue = newValue;
            slot.Seq = seq;
            slot.Unit = unit;
            slot.DefSkillResult = defSkillResult;
            slot.PUpSkillResult = pUpSkillResult;
            _pendingCount++;
            _lastKnownEventParam = newValue;
            _totalHits++;
            // Actual disarm (DR7 write) happens back in managed context
            // (Postfix -> FlushPendingLogs), never from inside the VEH.
        }

        private static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            int frameNow = UnityEngine.Time.frameCount;
            for (int i = 0; i < count; i++)
            {
                ref PendingHit hit = ref _pending[i];
                string ripLocation = DescribeAddress(hit.TrapRip);
                MelonLogger.Msg(
                    "[NocturneModernGameplay] EVENTPARAM-ACTUALWRITER-HIT; " +
                    $"frame={frameNow}; trapRip=0x{hit.TrapRip:X}; trapRipLocation={ripLocation}; " +
                    $"oldValue={hit.OldValue}; newValue={hit.NewValue}; seq={hit.Seq}; unit={hit.Unit}; " +
                    $"defSkillResult={hit.DefSkillResult}; pUpSkillResult={hit.PUpSkillResult}.");
            }

            if (count > 0 && _totalHits >= MaxHits && _armed)
            {
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] EVENTPARAM-ACTUALWRITER-AUTODISARM; reason=hit-budget-exhausted; totalHits={_totalHits}.");
                DisarmWatchpoint();
                _autoDisarmed = true;
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
                        long staticVa = 0x180000000L + rva;
                        return $"{m.ModuleName}+0x{rva:X} (staticVaGuess=0x{staticVa:X})";
                    }
                }
            }
            catch
            {
                // fall through
            }
            return "UNKNOWN-MODULE(heap/JIT-allocated or unmapped)";
        }

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
    }
}
