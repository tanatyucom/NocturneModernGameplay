using System;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - GBWK.SelectSkillID (real offset GBWK+0x9C,
    // measured empirically this session via SelectSkillIdOffsetProbe.cs -
    // 5 samples converged to exactly one candidate) WRITER capture
    // (read-only, no writes).
    //
    // Session continuation after AddNewHighlightCorrection.cs's own header
    // comment revealed that the "cursor==8(hidden slot) -> SelectSkillID =
    // pending skill" mapping HIDDEN-SLOT-ARRAY-CHECK observed is done by
    // THIS MOD's own C# code, only while FullCapacityAddNewBridgeState.
    // Active - NOT confirmed to be native's own behavior. For native's OWN
    // forget flow (bridgeActive=False - CursorPosShiftWriteWatchTrace
    // already caught native itself writing CursorPos.Shift=8
    // unconditionally at VA 0x182288AC1), whether/how native resolves
    // SelectSkillID for that same cursor position 8 is the open question
    // this class exists to answer, per User's 2026-09-14 direction: not
    // "what does Shift=8 mean" (already CONFIRMED - see 01_CURRENT_STATE.md
    // / PLAN.md) but "what processing treats the hidden/pending skill as
    // the logical selection target, and how do description/confirm
    // interpret it".
    //
    // Same technique as CursorPosShiftWriteWatchTrace.cs (hardware DATA
    // write breakpoint, not execute - traps AFTER the write, RIP at trap
    // already points past the writing instruction, Dr6 B0 identifies the
    // hit since ExceptionAddress is a code address here, not the watched
    // data address), adapted for a 2-byte field (LEN=01) at a fixed GBWK
    // offset instead of a resolved sub-object pointer chain. Logs ONLY on
    // an actual value transition, alongside the cursor (CursorPos.Shift)
    // value at that same moment so a native write of SelectSkillID while
    // cursor==8 can be directly correlated with the pending skill's real
    // ID.
    internal static class SelectSkillIdWriteWatchTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;
        private const int SelectSkillIdOffset = 0x9C; // GBWK+0x9C, confirmed via SelectSkillIdOffsetProbe.cs

        private const int MaxHits = 64;
        private const int MaxArmedFrames = 3600; // ~60s at 60fps - hard safety cap regardless of hits
        // First-guess safety cap for THIS address - no prior real-machine
        // measurement exists yet. Recalibrate from the logged totalHits if
        // this trips early (the same lesson learned twice already this
        // session with SkillCurObjNativeCallerProbe/HighlightTargetGateTrace).
        private const long MaxTotalHitsSafety = 300000;
        private static int _armedAtFrame = -1;

        private struct PendingHit
        {
            internal int Frame;
            internal int Seq;
            internal int OldValue;
            internal int NewValue;
            internal int CursorShift; // CursorPos.Shift at this moment - 8 = the hidden slot
            internal bool BridgeActive;
            internal long WriterNextInsnAddress; // RIP at trap - the instruction AFTER the writer
        }

        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;
        private static long _totalHitCount;

        // Cached once per Tick() call (ordinary managed context, main
        // thread) so the VEH can read them as plain fields - never by
        // calling into IL2CPP/Unity itself.
        private static int _cachedFrame;
        private static int _cachedSeq = -1;
        private static int _cachedCursorShift;
        private static bool _cachedBridgeActive;

        private static int _lastValue = int.MinValue;

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

        private const long Dr7EnableMask = 0x1L; // L0 only (bit 0)
        // R/W0 = 01 (writes only), LEN0 = 01 (2 bytes, matching
        // SelectSkillID's real ushort size) - bits 16-19.
        private const long Dr7WriteTwoByteNibble = 0x5L << 16;
        private const long Dr7RwLenMask = 0xFL << 16;

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private static long _actualModuleBase;
        private static IntPtr _targetAddress = IntPtr.Zero;
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
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                try { _cachedSeq = gbwk.SeqInfo.Current; } catch { }
                _cachedBridgeActive = FullCapacityAddNewBridgeState.Active;
                try
                {
                    var cursorPos = gbwk.SkillCursor?.CursorPos;
                    if (cursorPos != null) _cachedCursorShift = cursorPos.Shift;
                }
                catch { }

                if (!_installed)
                {
                    Install(gbwk.Pointer);
                    if (_installed) _armedAtFrame = _cachedFrame;
                    return;
                }

                int elapsed = _cachedFrame - _armedAtFrame;
                if (_totalHitCount >= MaxTotalHitsSafety || elapsed >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SELECTSKILLIDWATCH-AUTOUNINSTALL; " +
                        $"totalHits={_totalHitCount}; armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SelectSkillIdWriteWatchTrace.Tick failed safely: {ex.Message}");
            }
        }

        private static void Install(IntPtr gbwkPointer)
        {
            if (_installed || _installAttempted) return;
            _installAttempted = true;
            try
            {
                _targetAddress = new IntPtr(gbwkPointer.ToInt64() + SelectSkillIdOffset);

                IntPtr moduleBase = GetModuleHandle("GameAssembly.dll");
                if (moduleBase == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"GameAssembly.dll module base unavailable; error={Marshal.GetLastWin32Error()}");
                _actualModuleBase = moduleBase.ToInt64();

                _lastValue = unchecked((ushort)Marshal.ReadInt16(_targetAddress));

                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SELECTSKILLIDWATCH-INSTALLED; " +
                    $"targetAddress=0x{_targetAddress.ToInt64():X}; initialValue={_lastValue}; " +
                    "mechanism=hardware-write-breakpoint(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SelectSkillIdWriteWatchTrace install refused safely: {ex}");
                Uninstall();
            }
        }

        internal static void Uninstall()
        {
            try
            {
                if (_targetAddress != IntPtr.Zero)
                {
                    try { RemoveHardwareBreakpointOnCurrentThread(); }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning(
                            $"[NocturneModernGameplay] SelectSkillIdWriteWatchTrace breakpoint removal failed: {ex.Message}");
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
                _targetAddress = IntPtr.Zero;
            }
        }

        private static void InstallHardwareBreakpointOnCurrentThread()
        {
            IntPtr ctx = AllocAlignedContext(out IntPtr rawAlloc);
            try
            {
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                IntPtr thread = GetCurrentThread();
                if (!GetThreadContext(thread, ctx))
                    throw new InvalidOperationException(
                        $"GetThreadContext failed; error={Marshal.GetLastWin32Error()}");

                Marshal.WriteInt64(ctx, OffsetDr0, _targetAddress.ToInt64());

                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                long mask = Dr7EnableMask | Dr7RwLenMask;
                dr7 = (dr7 & ~mask) | Dr7EnableMask | Dr7WriteTwoByteNibble;
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);

                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                if (!SetThreadContext(thread, ctx))
                    throw new InvalidOperationException(
                        $"SetThreadContext failed; error={Marshal.GetLastWin32Error()}");

                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                if (!GetThreadContext(thread, ctx))
                    throw new InvalidOperationException(
                        $"post-install GetThreadContext readback failed; error={Marshal.GetLastWin32Error()}");

                long dr0Rb = Marshal.ReadInt64(ctx, OffsetDr0);
                long dr7Rb = Marshal.ReadInt64(ctx, OffsetDr7);
                if (dr0Rb != _targetAddress.ToInt64() || (dr7Rb & 0x1L) == 0)
                    throw new InvalidOperationException(
                        $"hardware breakpoint readback mismatch; dr0=0x{dr0Rb:X} dr7=0x{dr7Rb:X}");
            }
            finally
            {
                Marshal.FreeHGlobal(rawAlloc);
            }
        }

        private static void RemoveHardwareBreakpointOnCurrentThread()
        {
            IntPtr ctx = AllocAlignedContext(out IntPtr rawAlloc);
            try
            {
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                IntPtr thread = GetCurrentThread();
                if (!GetThreadContext(thread, ctx)) return;
                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                dr7 &= ~Dr7EnableMask;
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

        // Runs inside #DB exception dispatch. A data breakpoint traps
        // AFTER the write completes, so the word at _targetAddress already
        // holds the new value - reading it back is a single plain memory
        // read, no IL2CPP access, no allocation. Only an actual value
        // transition does the extra (still cheap) work of reading RIP and
        // storing one fixed-size struct.
        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                // Data breakpoints report the trapping RIP as
                // ExceptionAddress (a code address), not our watched data
                // address - confirm THIS breakpoint via Dr6's B0 bit
                // instead of an address compare.
                long dr6 = Marshal.ReadInt64(contextRecordPtr, OffsetDr6);
                if ((dr6 & 0x1L) == 0) return ExceptionContinueSearch;

                _totalHitCount++;

                int newValue = unchecked((ushort)Marshal.ReadInt16(_targetAddress));

                if (newValue != _lastValue && _pendingCount < MaxHits)
                {
                    long rip = Marshal.ReadInt64(contextRecordPtr, OffsetRip);

                    ref PendingHit slot = ref _pending[_pendingCount];
                    slot.Frame = _cachedFrame;
                    slot.Seq = _cachedSeq;
                    slot.OldValue = _lastValue;
                    slot.NewValue = newValue;
                    slot.CursorShift = _cachedCursorShift;
                    slot.BridgeActive = _cachedBridgeActive;
                    slot.WriterNextInsnAddress = rip;
                    _pendingCount++;
                }

                _lastValue = newValue;

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

        // Ordinary managed context only - called from ModMain.OnUpdate,
        // never from the VEH.
        internal static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            for (int i = 0; i < count; i++)
            {
                ref PendingHit hit = ref _pending[i];
                long staticVa = GameAssemblyPreferredBase + (hit.WriterNextInsnAddress - _actualModuleBase);
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SELECTSKILLIDWATCH-HIT; " +
                    $"frame={hit.Frame}; seq={hit.Seq}; oldValue={hit.OldValue}; newValue={hit.NewValue}; " +
                    $"cursorShift={hit.CursorShift}; bridgeActive={hit.BridgeActive}; " +
                    $"writerNextInsnVa=0x{staticVa:X}; totalHits={_totalHitCount}.");
            }
        }

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
