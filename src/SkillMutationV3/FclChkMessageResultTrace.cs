using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - minimal read-only capture of
    // fclMisc.fclChkMessage(0)'s return value at the exact point it is
    // consumed inside rstdraw.rstDrawSeqDestroySkill (VA 0x1822824D0).
    //
    // Session context (2026-09-14): full static disassembly of the call
    // chain cmpDrawSkill <- cmpDrawStatusComEx2 <- cmpDrawStatusComEx <-
    // rstDrawSeqDestroySkill <- rstDraw (switch dispatch on
    // [source object+0x10]+0x11, i.e. the same seq-like discriminant used
    // elsewhere in this investigation) established that:
    //   - seq=21 (normal skill-select browsing) calls
    //     rstDrawSeqDestroySkill with param2=1; seq=22 (the "are you sure"
    //     confirm dialog) calls it with param2=0.
    //   - Inside rstDrawSeqDestroySkill, when param2!=0 (seq21, the case
    //     that actually matters for the reported bug - the player is
    //     actively browsing and moving the cursor):
    //       esi = cmpMisc.cmpGetCursorIndex(pStock)   ; VA 0x18227E100... no,
    //                                                    call site VA 0x18228255E
    //       bMsg = fclMisc.fclChkMessage(0)            ; call site VA 0x18228258F
    //       suppress = (bMsg != 0)                      ; VA 0x18138BE10, confirmed
    //                                                      to be a trivial
    //                                                      "test cl,cl;setne al;ret"
    //                                                      bool-normalize thunk with
    //                                                      no other semantics
    //       sil = suppress ? 0xFF : (byte)esi
    //   - sil then propagates unchanged through
    //     cmpDrawStatusComEx's param3 -> cmpDrawStatusComEx2's stack arg5
    //     ([rsp+0x20], becomes callee `bpl`) -> cmpDrawSkill's `bpl`, which
    //     gates (`cmp bpl,8`, VA 0x1822DA3D8) the dedicated target==8
    //     hidden-entry presentation path this whole investigation has been
    //     chasing.
    // So whether the hidden-entry highlight can even be ATTEMPTED during
    // normal browsing (seq21) collapses to a single question: does
    // fclChkMessage(0) return 0 or non-zero at that exact moment? This
    // class answers exactly that, nothing more - one hardware EXECUTE
    // breakpoint at the instruction immediately after the fclChkMessage
    // call site (VA 0x182282594, still inside rstDrawSeqDestroySkill),
    // reading AL (fclChkMessage's raw return byte, RAX low 8 bits) and ESI
    // (cmpGetCursorIndex's earlier result, cached in esi at that same
    // point - useful as a cross-check that the cursor really is on the
    // hidden entry, expected value 8, when comparing High Pixie vs Frost).
    //
    // Read-only: no GameAssembly.dll bytes are written, no game state is
    // written. Matches the existing hardware-breakpoint probes in this mod
    // (PowerUpMutationBit6RawProbe, Hidden9thGateCascadeTrace, etc.) in
    // mechanism and safety posture.
    internal static class FclChkMessageResultTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;

        // Return address immediately after "call fclMisc.fclChkMessage"
        // (VA 0x18228258F) inside rstdraw.rstDrawSeqDestroySkill. AL still
        // holds fclChkMessage's raw return value here; ESI still holds
        // cmpGetCursorIndex's earlier result (set at VA 0x182282563 and
        // not clobbered before this point).
        private const long ObservationVa = 0x182282594L;
        private static readonly byte[] ObservationBytes =
            { 0x48, 0x8B, 0x0D, 0x8D, 0x76, 0xBE, 0x00 }; // "mov rcx,[rip+0xbe768d]"

        private const int MaxArmedFrames = 36000; // ~10 minutes at 60fps
        private static int _armedAtFrame = -1;

        private struct PendingHit
        {
            internal int Frame;
            internal int Seq;
            internal bool BridgeActive;
            internal int Unit;
            internal byte Al; // fclChkMessage(0) raw return byte
            internal int Esi; // cmpGetCursorIndex()'s earlier result
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
        private const int OffsetDr7 = 0x70;
        private const int OffsetRax = 0x78;
        private const int OffsetRsi = 0xA8;
        private const int ResumeFlagBit = 0x10000;

        // L0 (bit 0) enabled only; RW/LEN nibble for DR0 left at 00
        // (execute). One breakpoint only - leaves DR1-DR3 free for any
        // other probe that might be composed with this one later.
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
                        "[NocturneModernGameplay] FCLCHKMSG-AUTOUNINSTALL; " +
                        $"totalHits={_totalHitCount}; armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] FclChkMessageResultTrace.Tick failed safely: {ex.Message}");
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
                    "[NocturneModernGameplay] FCLCHKMSG-INSTALLED; " +
                    $"observation=0x{_addrObservation.ToInt64():X}; " +
                    "mechanism=hardware-execute-breakpoint x1(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] FclChkMessageResultTrace install refused safely: {ex}");
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
                            $"[NocturneModernGameplay] FclChkMessageResultTrace breakpoint removal failed: {ex.Message}");
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

        // Runs inside #DB exception dispatch. One address compare and
        // (on match) a couple of plain register reads - no logging, no
        // IL2CPP access, no allocation.
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
                    long rax = Marshal.ReadInt64(contextRecordPtr, OffsetRax);
                    long rsi = Marshal.ReadInt64(contextRecordPtr, OffsetRsi);

                    ref PendingHit slot = ref _pending[_pendingCount];
                    slot.Frame = _cachedFrame;
                    slot.Seq = _cachedSeq;
                    slot.BridgeActive = _cachedBridgeActive;
                    slot.Unit = _cachedUnit;
                    slot.Al = unchecked((byte)(rax & 0xFF));
                    slot.Esi = unchecked((int)(rsi & 0xFFFFFFFF));
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
                MelonLogger.Msg(
                    "[NocturneModernGameplay] FCLCHKMSG-HIT; " +
                    $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                    $"fclChkMessageResult={hit.Al}; cursorIndex={hit.Esi}.");
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
