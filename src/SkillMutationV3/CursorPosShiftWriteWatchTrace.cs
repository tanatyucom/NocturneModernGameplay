using System;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - CursorPos.Shift WRITER capture (read-only,
    // no writes to game state - the hardware breakpoint itself only
    // observes; it never intercepts or modifies the write it detects).
    //
    // Session continuation after HighlightTargetGateTrace CONFIRMED the
    // root cause of the missing highlight: while the cursor sits on the
    // hidden entry, CursorPos.Index=0 / CursorPos.Shift=8, so the
    // highlight-decision loop's `target` (Shift+Index) equals 8, which
    // never matches any `ebx` in [0, loopUpper) (loopUpper=8) - so
    // cmpSetupObject(skillCurObj[ebx], true) is never called for any row.
    // That answers "why does the highlight never show" (CONFIRMED, User-
    // approved Canonical State, see 01_CURRENT_STATE.md / PLAN.md). It does
    // NOT answer "why is Shift 8 in the first place" - this class exists
    // to answer that, by catching the exact writer.
    //
    // Per User's 2026-09-14 design agreement: place a hardware DATA
    // (write) breakpoint - not an execute breakpoint like this
    // investigation's earlier probes - directly on
    // SkillCursor.CursorPos's own `Shift` byte field (native address
    // `cursorPos.Pointer + 0x14`; see SkillCursorFieldTrace.cs for the
    // confirmed field layout this offset comes from). Unlike an execute
    // breakpoint (which traps BEFORE the target instruction runs, so RSP
    // still holds the caller's return address), a data breakpoint traps
    // AFTER the write has already completed, with RIP already pointing at
    // the instruction immediately following the one that performed the
    // write - so the trap's own RIP (not [RSP]) is what identifies the
    // writer here, and the byte at the watched address already holds the
    // NEW value by the time we read it.
    //
    // A DATA breakpoint's ExceptionRecord.ExceptionAddress is the trapping
    // RIP (a code address), NOT the watched data address - so unlike this
    // investigation's execute-breakpoint probes, this handler cannot
    // filter hits by comparing ExceptionAddress to the watched address.
    // Instead it reads Dr6 (the debug status register) straight from the
    // CONTEXT record and checks bit 0 (B0, "breakpoint 0 condition
    // detected") - the architecturally correct way to confirm THIS
    // breakpoint fired, independent of which instruction address is
    // reported.
    //
    // Per User's explicit frequency requirement: log ONLY on an actual
    // Shift value transition (old != new), not on every write (Shift may
    // legitimately get rewritten with the SAME value far more often than
    // it changes) - a plain `_lastShift` comparison, no dedup array
    // needed. All other context fields (frame/seq/index/listNums/
    // eventParam/bridgeActive) are cached once per frame by Tick()
    // (ordinary managed context, main thread) and read by the VEH as
    // plain fields only - never computed there.
    internal static class CursorPosShiftWriteWatchTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;

        private const int MaxHits = 64;
        private const int MaxArmedFrames = 3600; // ~60s at 60fps - hard safety cap regardless of hits
        // First-guess safety cap: CursorPos may be a general-purpose
        // structure reused by menus outside the forget UI (status screen,
        // item list, etc.), so this write could fire far more often than
        // this investigation's earlier execute-breakpoint probes ever saw.
        // No real-machine measurement exists yet for this address -
        // totalHits is logged at uninstall so this can be recalibrated
        // from evidence rather than guessed twice, the same lesson learned
        // from SkillCurObjNativeCallerProbe/HighlightTargetGateTrace.
        private const long MaxTotalHitsSafety = 300000;
        private static int _armedAtFrame = -1;

        private struct PendingHit
        {
            internal int Frame;
            internal int Seq;
            internal int OldShift;
            internal int NewShift;
            internal int Index;
            internal int ListNums;
            internal int EventParam;
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
        private static int _cachedIndex;
        private static int _cachedListNums;
        private static int _cachedEventParam;
        private static bool _cachedBridgeActive;

        // Updated only from ordinary managed context (Install(), and once
        // more each Tick() as a safety re-read in case something else
        // legitimately changed Shift between our own hits) - the VEH only
        // ever reads/writes this via plain field access.
        private static int _lastShift = int.MinValue;

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
        // R/W0 = 01 (break on data writes only), LEN0 = 00 (1 byte,
        // matching Shift's real size) - bits 16-19. Deliberately NOT the
        // execute-breakpoint shape (0x0) this investigation's earlier
        // probes used.
        private const long Dr7WriteOneByteNibble = 0x1L << 16;
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
                try { _cachedEventParam = gbwk.EventParam; } catch { }
                _cachedBridgeActive = FullCapacityAddNewBridgeState.Active;

                var cursorInfo = gbwk.SkillCursor;
                var cursorPos = cursorInfo?.CursorPos;
                if (cursorPos == null || cursorPos.Pointer == IntPtr.Zero) return; // not ready yet - try again next frame

                try
                {
                    _cachedIndex = cursorPos.Index;
                    _cachedListNums = cursorPos.ListNums;
                }
                catch { }

                if (!_installed)
                {
                    Install(cursorPos.Pointer);
                    if (_installed) _armedAtFrame = _cachedFrame;
                    return;
                }

                int elapsed = _cachedFrame - _armedAtFrame;
                if (_totalHitCount >= MaxTotalHitsSafety || elapsed >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SHIFTWATCH-AUTOUNINSTALL; " +
                        $"totalHits={_totalHitCount}; armedFrames={elapsed}.");
                    Uninstall();
                    return;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] CursorPosShiftWriteWatchTrace.Tick failed safely: {ex.Message}");
            }
        }

        private static void Install(IntPtr cursorPosPointer)
        {
            if (_installed || _installAttempted) return;
            _installAttempted = true;
            try
            {
                _targetAddress = new IntPtr(cursorPosPointer.ToInt64() + 0x14);

                IntPtr moduleBase = GetModuleHandle("GameAssembly.dll");
                if (moduleBase == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"GameAssembly.dll module base unavailable; error={Marshal.GetLastWin32Error()}");
                _actualModuleBase = moduleBase.ToInt64();

                _lastShift = Marshal.ReadByte(_targetAddress);

                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SHIFTWATCH-INSTALLED; " +
                    $"targetAddress=0x{_targetAddress.ToInt64():X}; initialShift={_lastShift}; " +
                    "mechanism=hardware-write-breakpoint(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] CursorPosShiftWriteWatchTrace install refused safely: {ex}");
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
                            $"[NocturneModernGameplay] CursorPosShiftWriteWatchTrace breakpoint removal failed: {ex.Message}");
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
                dr7 = (dr7 & ~mask) | Dr7EnableMask | Dr7WriteOneByteNibble;
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
        // AFTER the write completes, so the byte at _targetAddress already
        // holds the new value - reading it back is a single plain memory
        // read, no IL2CPP access, no allocation. Only an actual value
        // transition (vs the cached _lastShift) does the extra (still
        // cheap) work of reading RIP and storing one fixed-size struct.
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
                // address - so confirm THIS breakpoint via Dr6's B0 bit
                // instead of an address compare.
                long dr6 = Marshal.ReadInt64(contextRecordPtr, OffsetDr6);
                if ((dr6 & 0x1L) == 0) return ExceptionContinueSearch;

                _totalHitCount++;

                byte newShiftByte = Marshal.ReadByte(_targetAddress);
                int newShift = newShiftByte;

                if (newShift != _lastShift && _pendingCount < MaxHits)
                {
                    long rip = Marshal.ReadInt64(contextRecordPtr, OffsetRip);

                    ref PendingHit slot = ref _pending[_pendingCount];
                    slot.Frame = _cachedFrame;
                    slot.Seq = _cachedSeq;
                    slot.OldShift = _lastShift;
                    slot.NewShift = newShift;
                    slot.Index = _cachedIndex;
                    slot.ListNums = _cachedListNums;
                    slot.EventParam = _cachedEventParam;
                    slot.BridgeActive = _cachedBridgeActive;
                    slot.WriterNextInsnAddress = rip;
                    _pendingCount++;
                }

                _lastShift = newShift;

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
                    "[NocturneModernGameplay] SHIFTWATCH-HIT; " +
                    $"frame={hit.Frame}; seq={hit.Seq}; oldShift={hit.OldShift}; newShift={hit.NewShift}; " +
                    $"index={hit.Index}; listNums={hit.ListNums}; eventParam={hit.EventParam}; " +
                    $"bridgeActive={hit.BridgeActive}; writerNextInsnVa=0x{staticVa:X}; " +
                    $"totalHits={_totalHitCount}.");
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
