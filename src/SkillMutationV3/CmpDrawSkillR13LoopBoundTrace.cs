using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - minimal read-only capture of `r13` (and its
    // `+0x10` loop-bound byte) at the entry of the candidate-scan loop
    // inside cmpDrawStatus.cmpDrawSkill (VA 0x1822D97C0), the same loop
    // this investigation has been tracing since the "target==8 dedicated
    // presentation path" discovery.
    //
    // Session context (2026-09-14): FclChkMessageResultTrace's runtime
    // comparison REJECTED the working hypothesis that fclMisc.fclChkMessage
    // (0)'s return value explains the Frost/High Pixie asymmetry - both
    // units showed fclChkMessageResult=0 (not suppressed) throughout their
    // entire AddNew-bridge episode, yet only High Pixie reaches the
    // dedicated target==8 branch (VA 0x1822DA48C) at runtime
    // (Hidden9thSlotPathTrace, earlier this session). So the real
    // divergence lies further down the same function, in code this
    // session had not yet instrumented: the loop at VA 0x1822DA051-
    //   mov  r13, qword ptr [rsp+0xE0]   ; r13 = some pointer, static-slot-
    //                                       derived (see below), NOT a
    //                                       pass-through UI parameter
    //   xor  r15d, r15d                   ; r15d = loop index i = 0
    //   xor  eax, eax
    //   test r13, r13                     ; VA 0x1822DA05E - THIS class's
    //                                       observation point
    //   je   <skip whole loop>            ; VA 0x1822DA061
    //   ...
    //   movsx ebx, byte ptr [r13+0x10]    ; VA 0x1822DA070 - loop bound
    //   cmp  eax, ebx                     ; i < bound?
    //   jge  <exit>
    // Full static tracing (this session) established that `r13` is NOT
    // handed down through the cmpDrawSkill<-cmpDrawStatusComEx2<-
    // cmpDrawStatusComEx<-rstDrawSeqDestroySkill<-rstDraw parameter chain
    // at all - cmpDrawStatusComEx2 fetches it itself, fresh, via a generic
    // IL2CPP "lazily-init then fetch static field" idiom (`mov rcx,
    // [0x182E46A50]; call 0x1800E6930`) that recurs verbatim elsewhere in
    // rstcalc's own decompiled code (per the cpp2il ISIL dump), and whose
    // target static slot (0x182E46A50) sits in the SAME address cluster as
    // the already-known `real GBWK` (0x182e464b8) and `ACTION`
    // (0x182e46930) statics - i.e. very likely another field of the same
    // class's static-fields block, not (yet) independently named. Rather
    // than sink further effort into resolving that field's exact IL2CPP
    // type/name, this class answers the more decisive question directly:
    // does the VALUE of r13 (null or not) or its `+0x10` byte (the loop
    // bound - zero or not) actually differ between Frost and High Pixie at
    // this exact native instant?
    //
    // Read-only: no GameAssembly.dll bytes are written. The `+0x10`
    // dereference happens only when r13 is non-null and is wrapped so a
    // bad pointer can never crash the game (falls back to a sentinel
    // value, logged as such).
    internal static class CmpDrawSkillR13LoopBoundTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;

        // "test r13,r13" immediately after r13 is loaded and the loop
        // index is zeroed, inside cmpDrawStatus.cmpDrawSkill's candidate
        // scan setup (VA range 0x1822DA051-0x1822DA061).
        private const long ObservationVa = 0x1822DA05EL;
        private static readonly byte[] ObservationBytes = { 0x4D, 0x85, 0xED }; // "test r13,r13"

        private const int MaxArmedFrames = 36000; // ~10 minutes at 60fps
        private static int _armedAtFrame = -1;

        private const long NullPointerLoopBoundSentinel = long.MinValue;
        private const int UnreadableLoopBoundSentinel = -1;

        private struct PendingHit
        {
            internal int Frame;
            internal int Seq;
            internal bool BridgeActive;
            internal int Unit;
            internal int Target; // cached CursorPos.Shift + CursorPos.Index, from managed context
            internal long R13;
            internal int LoopBound; // byte at [r13+0x10], or UnreadableLoopBoundSentinel if r13==0 or the read failed
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
        private static int _cachedTarget = int.MinValue;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr7 = 0x70;
        private const int OffsetR13 = 0xE0;
        private const int ResumeFlagBit = 0x10000;

        private const long Dr7EnableMask = 0x1L;
        private const long Dr7RwLenMask = 0xFL << 16;

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private static long _actualModuleBase;
        private static IntPtr _addrObservation = IntPtr.Zero;
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
                    if (raw == null || raw.Length == 0) return; // not in gameplay yet - try again next frame

                    Install();
                    if (_installed) _armedAtFrame = _cachedFrame;
                    return;
                }

                int elapsed = _cachedFrame - _armedAtFrame;
                if (elapsed >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] R13LOOPBOUND-AUTOUNINSTALL; " +
                        $"totalHits={_totalHitCount}; armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] CmpDrawSkillR13LoopBoundTrace.Tick failed safely: {ex.Message}");
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

                _addrObservation = ResolveAndVerify(moduleBase, ObservationVa, ObservationBytes, "observation-point");

                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] R13LOOPBOUND-INSTALLED; " +
                    $"observation=0x{_addrObservation.ToInt64():X}; " +
                    "mechanism=hardware-execute-breakpoint x1(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] CmpDrawSkillR13LoopBoundTrace install refused safely: {ex}");
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
                if (_addrObservation != IntPtr.Zero)
                {
                    try { RemoveHardwareBreakpointOnCurrentThread(); }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning(
                            $"[NocturneModernGameplay] CmpDrawSkillR13LoopBoundTrace breakpoint removal failed: {ex.Message}");
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
                _addrObservation = IntPtr.Zero;
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

                Marshal.WriteInt64(ctx, OffsetDr0, _addrObservation.ToInt64());

                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                long mask = Dr7EnableMask | Dr7RwLenMask;
                dr7 = (dr7 & ~mask) | Dr7EnableMask;
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
                if ((dr7Rb & Dr7EnableMask) != Dr7EnableMask)
                    throw new InvalidOperationException(
                        $"hardware breakpoint readback mismatch; dr7=0x{dr7Rb:X}");
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

        // Runs inside #DB exception dispatch. One address compare, a
        // register read, and (only when r13 is non-null) a single guarded
        // raw-memory byte read of [r13+0x10] - no IL2CPP access, no
        // allocation, no logging.
        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                IntPtr exceptionAddress = Marshal.ReadIntPtr(exceptionRecordPtr, 0x10);
                if (exceptionAddress != _addrObservation) return ExceptionContinueSearch;

                _totalHitCount++;

                if (_pendingCount < MaxHits)
                {
                    long r13 = Marshal.ReadInt64(contextRecordPtr, OffsetR13);
                    int loopBound = UnreadableLoopBoundSentinel;
                    if (r13 != 0)
                    {
                        try { loopBound = Marshal.ReadByte(new IntPtr(r13 + 0x10)); }
                        catch { loopBound = UnreadableLoopBoundSentinel; }
                    }
                    else
                    {
                        loopBound = unchecked((int)NullPointerLoopBoundSentinel);
                    }

                    ref PendingHit slot = ref _pending[_pendingCount];
                    slot.Frame = _cachedFrame;
                    slot.Seq = _cachedSeq;
                    slot.BridgeActive = _cachedBridgeActive;
                    slot.Unit = _cachedUnit;
                    slot.Target = _cachedTarget;
                    slot.R13 = r13;
                    slot.LoopBound = loopBound;
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

        // Ordinary managed context only - called from ModMain.OnUpdate,
        // never from the VEH.
        internal static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            for (int i = 0; i < count; i++)
            {
                ref PendingHit hit = ref _pending[i];
                string r13Str = hit.R13 == 0 ? "null" : $"0x{hit.R13:X}";
                string loopBoundStr = hit.R13 == 0
                    ? "n/a(r13=null)"
                    : (hit.LoopBound == UnreadableLoopBoundSentinel ? "unreadable" : hit.LoopBound.ToString());
                MelonLogger.Msg(
                    "[NocturneModernGameplay] R13LOOPBOUND-HIT; " +
                    $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                    $"target={hit.Target}; r13={r13Str}; loopBound={loopBoundStr}.");
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
