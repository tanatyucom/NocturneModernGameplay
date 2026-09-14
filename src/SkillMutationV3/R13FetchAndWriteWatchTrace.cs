using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - writer identification for [r13+0x10] (the
    // loop-bound byte CONFIRMED this session to be 0 for Frost's failing
    // AddNew-bridge case and 1/2 for High Pixie's succeeding case,
    // CmpDrawSkillR13LoopBoundTrace).
    //
    // Technical note this class exists to work around: `r13` is NOT a
    // stable, long-lived object. It is fetched FRESH by
    // cmpDrawStatusComEx2 every single time cmpDrawSkill is about to be
    // called (`mov rcx,[0x182E46A50]; call 0x1800E6930` - VA 0x1822DB409-
    // 0x1822DB413 - a thin trampoline into generic IL2CPP runtime
    // dispatch/type-resolution machinery, confirmed by static disassembly
    // this session; NOT game-specific code). Confirmed via
    // CmpDrawSkillR13LoopBoundTrace's own log: the observed `r13` pointer
    // value is different on almost every single hit for both units, all
    // falling within the same overall heap address range (same kind of
    // allocation, answering the "same kind of object?" question directly
    // from existing log data without a fresh test). Because the object's
    // address does not exist before that fetch call returns, a
    // conventional STATIC hardware write-breakpoint (fixed address,
    // installed once) cannot watch it from before construction - by the
    // time we can read `r13` at all, any write that happened *inside* the
    // fetch call has already occurred.
    //
    // This class resolves that with a dynamic re-arm pattern instead of
    // trying to guess a fixed address:
    //   DR0 = execute breakpoint at cmpDrawStatusComEx2's fetch point (VA
    //         0x1822DB41A, "mov [rsp+0x60],rax" - at this instant RAX
    //         already holds the freshly-returned r13, read directly from
    //         context, no dereference needed for the pointer itself).
    //         On each hit: read RAX (=r13), read the CURRENT byte at
    //         [r13+0x10] right then (the earliest observable value - if
    //         this is already 1/2 vs 0 at this exact instant, the
    //         divergence is baked in DURING the fetch call itself, not by
    //         some later external writer), then dynamically reprogram DR1
    //         to watch [r13+0x10] for WRITES from this point forward.
    //   DR1 = dynamic 1-byte WRITE breakpoint, address reprogrammed every
    //         time DR0 fires (a fresh r13 each frame needs a fresh watch
    //         address; the previous frame's object is stale/possibly
    //         reused by then, so leaving a stale DR1 armed would be
    //         meaningless at best). Identified at fire time via DR6 status
    //         bit 1 (its target address is not fixed, so address-matching
    //         like DR0/DR2 below cannot be used for it). On hit: capture
    //         the writer's return-address-adjacent RIP (from the context's
    //         Rip field directly, since a data breakpoint traps AFTER the
    //         faulting instruction retires - Rip already points past it)
    //         and the byte value AFTER the write, for later ASLR-correction
    //         to a static VA and manual disassembly of the writer site.
    //   DR2 = execute breakpoint at cmpDrawSkill's own loop-bound read (VA
    //         0x1822DA05E, "test r13,r13" - the same point
    //         CmpDrawSkillR13LoopBoundTrace used), kept here too so a
    //         single test run yields BOTH the fetch-time value and the
    //         final value seen by the loop in one pass, without requiring
    //         two separate sessions.
    //
    // Read-only from the game's perspective: no GameAssembly.dll bytes are
    // written, only CPU debug registers (Dr0-Dr3/Dr7) on this thread's
    // CONTEXT, exactly like every other hardware-breakpoint probe already
    // in this mod.
    internal static class R13FetchAndWriteWatchTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;

        private const long FetchVa = 0x1822DB41AL; // cmpDrawStatusComEx2: "mov [rsp+0x60],rax"
        private static readonly byte[] FetchBytes = { 0x48, 0x89, 0x44, 0x24, 0x60 };

        private const long LoopReadVa = 0x1822DA05EL; // cmpDrawSkill: "test r13,r13"
        private static readonly byte[] LoopReadBytes = { 0x4D, 0x85, 0xED };

        private const int MaxArmedFrames = 36000; // ~10 minutes at 60fps
        private static int _armedAtFrame = -1;

        private enum HitKind { Fetch = 0, Write = 1, LoopRead = 2 }

        private struct PendingHit
        {
            internal int Frame;
            internal int Seq;
            internal bool BridgeActive;
            internal int Unit;
            internal int Target;
            internal HitKind Kind;
            internal long R13OrAddr; // Fetch/LoopRead: r13 pointer. Write: the watched address (r13+0x10).
            internal int ByteValue;  // the byte at +0x10, at the moment of this hit
            internal long WriterRip; // Write hits only: RIP right after the faulting instruction
        }

        private const int MaxHits = 96;
        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;
        private static long _totalFetchHits, _totalWriteHits, _totalLoopReadHits;

        private static int _cachedFrame;
        private static int _cachedSeq = -1;
        private static bool _cachedBridgeActive;
        private static int _cachedUnit = -1;
        private static int _cachedTarget = int.MinValue;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr1 = 0x50;
        private const int OffsetDr2 = 0x58;
        private const int OffsetDr6 = 0x68;
        private const int OffsetDr7 = 0x70;
        private const int OffsetRax = 0x78;
        private const int OffsetRip = 0xF8;
        private const int ResumeFlagBit = 0x10000;

        // L0,L1,L2 enabled (bits 0,2,4). RW/LEN nibbles: DR0=execute(00,00),
        // DR1=write-1-byte(01,00), DR2=execute(00,00). DR3 unused.
        private const long Dr7LocalEnableMask = 0x1L | 0x4L | 0x10L;
        private const long Dr7Rw1WriteLen1Byte = 0x1L << 20; // RW1=01 at bits 20-21, LEN1=00 at bits 22-23
        private const long Dr7RwLenAllUsedMask = 0xFFFL << 16; // clears RW/LEN nibbles for DR0,DR1,DR2 (bits 16-27) before setting

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private static long _actualModuleBase;
        private static IntPtr _addrFetch = IntPtr.Zero;
        private static IntPtr _addrLoopRead = IntPtr.Zero;
        private static IntPtr _vehHandle = IntPtr.Zero;
        private static VectoredHandlerDelegate? _handlerDelegate;
        private static bool _installed;
        private static bool _installAttempted;
        private static bool _uninstalled;

        internal static void Tick()
        {
            if (!Enabled || _uninstalled) return;

            try
            {
                _cachedFrame = UnityEngine.Time.frameCount;

                var gbwk = Il2Cpp.rstinit.GBWK;
                if (gbwk != null && gbwk.Pointer != IntPtr.Zero)
                {
                    try { _cachedSeq = gbwk.SeqInfo.Current; } catch { }
                    try
                    {
                        var stock = gbwk.pCurrentStock;
                        _cachedUnit = (stock != null && stock.Pointer != IntPtr.Zero) ? stock.id : -1;
                    }
                    catch { _cachedUnit = -1; }
                    try
                    {
                        var cursorInfo = gbwk.SkillCursor;
                        var cursorPos = cursorInfo?.CursorPos;
                        _cachedTarget = cursorPos != null ? cursorPos.Shift + cursorPos.Index : int.MinValue;
                    }
                    catch { _cachedTarget = int.MinValue; }
                }
                _cachedBridgeActive = FullCapacityAddNewBridgeState.Active;

                if (!_installed)
                {
                    var raw = UnityEngine.Object.FindObjectsOfType(
                        Il2CppInterop.Runtime.Il2CppType.Of<Il2Cpp.statusUI>());
                    if (raw == null || raw.Length == 0) return;

                    Install();
                    if (_installed) _armedAtFrame = _cachedFrame;
                    return;
                }

                int elapsed = _cachedFrame - _armedAtFrame;
                if (elapsed >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] R13WRITEWATCH-AUTOUNINSTALL; " +
                        $"fetchHits={_totalFetchHits}; writeHits={_totalWriteHits}; loopReadHits={_totalLoopReadHits}; armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] R13FetchAndWriteWatchTrace.Tick failed safely: {ex.Message}");
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

                _addrFetch = ResolveAndVerify(moduleBase, FetchVa, FetchBytes, "fetch-point");
                _addrLoopRead = ResolveAndVerify(moduleBase, LoopReadVa, LoopReadBytes, "loop-read-point");

                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointsOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] R13WRITEWATCH-INSTALLED; " +
                    $"fetch=0x{_addrFetch.ToInt64():X}; loopRead=0x{_addrLoopRead.ToInt64():X}; " +
                    "dr1=dynamic(reprogrammed each fetch hit); " +
                    "mechanism=hardware-execute-x2+write-x1(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] R13FetchAndWriteWatchTrace install refused safely: {ex}");
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
                if (_addrFetch != IntPtr.Zero || _addrLoopRead != IntPtr.Zero)
                {
                    try { RemoveHardwareBreakpointsOnCurrentThread(); }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning(
                            $"[NocturneModernGameplay] R13FetchAndWriteWatchTrace breakpoint removal failed: {ex.Message}");
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
                _addrFetch = IntPtr.Zero;
                _addrLoopRead = IntPtr.Zero;
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

                Marshal.WriteInt64(ctx, OffsetDr0, _addrFetch.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr1, 0); // no valid r13 yet - DR1 left disabled until first fetch hit
                Marshal.WriteInt64(ctx, OffsetDr2, _addrLoopRead.ToInt64());

                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                dr7 &= ~Dr7RwLenAllUsedMask;                 // DR0/DR1/DR2 RW/LEN nibbles -> 0 (execute-shaped)
                dr7 |= Dr7Rw1WriteLen1Byte;                   // then set DR1's nibble to write/1-byte
                // Only L0 and L2 enabled at install time - L1 (the dynamic
                // write watch) is turned on inside the VEH once a real r13
                // address is known, never before.
                dr7 = (dr7 & ~(0x1L | 0x4L | 0x10L)) | (0x1L | 0x10L);
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
                if ((dr7Rb & (0x1L | 0x10L)) != (0x1L | 0x10L))
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
                dr7 &= ~(0x1L | 0x4L | 0x10L);
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

        // Runs inside #DB exception dispatch. Address compares for the two
        // fixed execute points; DR6 status-bit check for the dynamic write
        // point (its address is not fixed, so it cannot be matched by
        // comparing exceptionAddress). A couple of plain register/memory
        // reads - no IL2CPP access, no allocation.
        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                IntPtr exceptionAddress = Marshal.ReadIntPtr(exceptionRecordPtr, 0x10);
                long dr6 = Marshal.ReadInt64(contextRecordPtr, OffsetDr6);
                bool bp1Fired = (dr6 & 0x2L) != 0;

                if (exceptionAddress == _addrFetch)
                {
                    _totalFetchHits++;
                    long rax = Marshal.ReadInt64(contextRecordPtr, OffsetRax); // = fresh r13
                    long watchAddr = rax != 0 ? rax + 0x10 : 0;
                    int byteNow = -1;
                    if (rax != 0)
                    {
                        try { byteNow = Marshal.ReadByte(new IntPtr(watchAddr)); } catch { byteNow = -1; }
                    }

                    if (_pendingCount < MaxHits)
                    {
                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Frame = _cachedFrame;
                        slot.Seq = _cachedSeq;
                        slot.BridgeActive = _cachedBridgeActive;
                        slot.Unit = _cachedUnit;
                        slot.Target = _cachedTarget;
                        slot.Kind = HitKind.Fetch;
                        slot.R13OrAddr = rax;
                        slot.ByteValue = byteNow;
                        slot.WriterRip = 0;
                        _pendingCount++;
                    }

                    // Reprogram DR1 to watch this frame's [r13+0x10], and
                    // make sure L1 is enabled (harmless if already set).
                    if (rax != 0)
                    {
                        Marshal.WriteInt64(contextRecordPtr, OffsetDr1, watchAddr);
                        long dr7 = Marshal.ReadInt64(contextRecordPtr, OffsetDr7);
                        dr7 |= 0x4L; // L1
                        Marshal.WriteInt64(contextRecordPtr, OffsetDr7, dr7);
                    }
                }
                else if (exceptionAddress == _addrLoopRead)
                {
                    _totalLoopReadHits++;
                    long r13 = Marshal.ReadInt64(contextRecordPtr, 0xE0); // R13 offset, same as CmpDrawSkillR13LoopBoundTrace
                    int byteNow = -1;
                    if (r13 != 0)
                    {
                        try { byteNow = Marshal.ReadByte(new IntPtr(r13 + 0x10)); } catch { byteNow = -1; }
                    }
                    if (_pendingCount < MaxHits)
                    {
                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Frame = _cachedFrame;
                        slot.Seq = _cachedSeq;
                        slot.BridgeActive = _cachedBridgeActive;
                        slot.Unit = _cachedUnit;
                        slot.Target = _cachedTarget;
                        slot.Kind = HitKind.LoopRead;
                        slot.R13OrAddr = r13;
                        slot.ByteValue = byteNow;
                        slot.WriterRip = 0;
                        _pendingCount++;
                    }
                }
                else if (bp1Fired)
                {
                    _totalWriteHits++;
                    long watchAddr = Marshal.ReadInt64(contextRecordPtr, OffsetDr1);
                    long rip = Marshal.ReadInt64(contextRecordPtr, OffsetRip);
                    int byteNow = -1;
                    try { byteNow = Marshal.ReadByte(new IntPtr(watchAddr)); } catch { byteNow = -1; }

                    if (_pendingCount < MaxHits)
                    {
                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Frame = _cachedFrame;
                        slot.Seq = _cachedSeq;
                        slot.BridgeActive = _cachedBridgeActive;
                        slot.Unit = _cachedUnit;
                        slot.Target = _cachedTarget;
                        slot.Kind = HitKind.Write;
                        slot.R13OrAddr = watchAddr;
                        slot.ByteValue = byteNow;
                        slot.WriterRip = rip;
                        _pendingCount++;
                    }
                }
                else
                {
                    return ExceptionContinueSearch;
                }

                // Clear DR6 status bits (required so future #DB dispatches
                // report cleanly) and set the resume flag.
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

        // Ordinary managed context only - called from ModMain.OnUpdate,
        // never from the VEH. Writer RIPs are logged raw (runtime/ASLR
        // address) - correct them to a static VA offline the same way
        // every other probe in this mod already does
        // (runtimeAddr - actualModuleBase + 0x180000000).
        internal static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            for (int i = 0; i < count; i++)
            {
                ref PendingHit hit = ref _pending[i];
                switch (hit.Kind)
                {
                    case HitKind.Fetch:
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] R13WRITEWATCH-FETCH; " +
                            $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                            $"target={hit.Target}; r13=0x{hit.R13OrAddr:X}; byteAtFetch={hit.ByteValue}.");
                        break;
                    case HitKind.Write:
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] R13WRITEWATCH-WRITE; " +
                            $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                            $"target={hit.Target}; addr=0x{hit.R13OrAddr:X}; byteAfterWrite={hit.ByteValue}; " +
                            $"writerRipRuntime=0x{hit.WriterRip:X}.");
                        break;
                    case HitKind.LoopRead:
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] R13WRITEWATCH-LOOPREAD; " +
                            $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                            $"target={hit.Target}; r13=0x{hit.R13OrAddr:X}; loopBound={hit.ByteValue}.");
                        break;
                }
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
