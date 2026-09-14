using System;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - direct runtime confirmation of the
    // dedicated target==8 presentation path (read-only, no writes).
    //
    // Session continuation after full static disassembly of VA
    // 0x1822DA3FB (.analysis/ - the function containing the caller
    // SkillCurObjNativeCallerProbe already found for index=8,
    // staticVa=0x1822DA5FB) found this is NOT part of the ebx=0..7
    // highlight loop at VA 0x1822D97C0 - it is a SEPARATE, dedicated code
    // path, gated on a literal `cmp ecx, 8` (VA 0x1822DA483), that
    // resolves and renders a pending/hidden candidate's name+icon and
    // then calls cmpSetupObject(skillCurObj[8], true) (skillCurObj[8],
    // NOT part of the 0..7 "obtained" row set) - i.e. the game genuinely
    // has its own dedicated "9th slot" presentation pathway. Per User's
    // 2026-09-14 decision tree, this class exists to distinguish WHY it
    // doesn't visibly work in some cases (e.g. Frost) despite a real
    // runtime hit for it having been captured before (High-Pixie-shaped
    // case):
    //   A. skillCurObj[8] genuinely gets SetActive(true) but its own
    //      GameObject/hierarchy/geometry is what's actually broken
    //      (StatusUiArrayFieldTrace.cs now logs skillCurObj[8]'s
    //      activeSelf/activeInHierarchy/alpha/geometry unconditionally,
    //      to be read alongside this trace's hits).
    //   B. target==8 never even reaches VA 0x1822DA48C (the branch body)
    //      in the failing case - something upstream of the literal `cmp
    //      ecx,8` gates entry.
    //   C. the branch is entered but never reaches the actual
    //      cmpSetupObject call (VA 0x1822DA5FB) - an internal gate (array
    //      bounds, null pending-data pointer, etc.) inside the branch
    //      itself is the real blocker.
    //
    // Two EXECUTE hardware breakpoints (DR0 = branch entry, DR1 = call
    // completed) - unlike CursorPosShiftWriteWatchTrace/
    // SelectSkillIdWriteWatchTrace's DATA breakpoints, these report their
    // watched address correctly as ExceptionRecord.ExceptionAddress, so
    // hits are told apart by a plain address compare (no Dr6 read
    // needed). Both addresses sit inside a rarely-executed dedicated
    // branch (not the hot general-purpose SetActive path
    // SkillCurObjNativeCallerProbe had to worry about), so no dedup/rate
    // limiting beyond a simple hit cap and timeout is needed - every hit
    // is logged.
    internal static class Hidden9thSlotPathTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;
        // Entry of the target==8 branch body - the instruction
        // immediately after `cmp ecx,8; jne <skip>` when NOT skipped, i.e.
        // proof the literal target==8 check passed.
        private const long BranchEntryVa = 0x1822DA48CL;
        // The instruction immediately after `call cmpSetupObject(skillCurObj[8], true)`
        // (VA 0x1822DA5F6) - proof the call actually completed. Same
        // address SkillCurObjNativeCallerProbe already resolved this
        // session as this branch's own caller of cmpSetupObject.
        private const long CallDoneVa = 0x1822DA5FBL;

        private const int MaxHits = 64;
        // 2026-09-14 recalibration: a real-machine run measured this path
        // firing roughly every ~5 frames continuously while dwelling on
        // the hidden entry (128 branch-entry + 128 call-done hits inside a
        // ~607-frame dwell window) - the OLD cap (MaxHits*4=256) exhausted
        // within the FIRST of three back-to-back test scenarios the User
        // ran in one sitting, before the AddNew-bridge and Frost cases
        // (the actually decision-relevant ones) could be captured at all.
        // Sized generously for a multi-scenario session, the same lesson
        // learned repeatedly this session with the other probes in this
        // investigation.
        private const long MaxTotalHitsSafety = 50000;
        private const int MaxArmedFrames = 36000; // ~10 minutes at 60fps - generous for a multi-scenario test session
        private static int _armedAtFrame = -1;

        private struct PendingHit
        {
            internal int Frame;
            internal int Seq;
            internal bool BridgeActive;
            internal bool IsCallDone; // false = branch entered only, true = call completed
        }

        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;
        private static long _branchEntryHitCount;
        private static long _callDoneHitCount;

        // Cached once per Tick() call (ordinary managed context, main
        // thread) so the VEH can read them as plain fields - never by
        // calling into IL2CPP/Unity itself.
        private static int _cachedFrame;
        private static int _cachedSeq = -1;
        private static bool _cachedBridgeActive;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr1 = 0x50;
        private const int OffsetDr7 = 0x70;
        private const int ResumeFlagBit = 0x10000;

        // L0 (bit0) + L1 (bit2) enabled; RW0/LEN0 and RW1/LEN1 left at 00
        // (execute, matching this mod's existing single-breakpoint execute
        // probes) - bits 16-23 cover both nibbles.
        private const long Dr7EnableL0L1Mask = 0x1L | 0x4L;
        private const long Dr7RwLenBothMask = 0xFFL << 16;

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private static long _actualModuleBase;
        private static IntPtr _branchEntryAddress = IntPtr.Zero;
        private static IntPtr _callDoneAddress = IntPtr.Zero;
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
                }
                _cachedBridgeActive = FullCapacityAddNewBridgeState.Active;

                if (!_installed)
                {
                    // 2026-09-14 fix: this trace was missing the same
                    // "wait for a live statusUI instance" gate
                    // HighlightTargetGateTrace already learned it needed
                    // (see that file's 2026-09-14 note) - without it,
                    // Install() fires at the title screen/load, wasting
                    // part of the exposure window before real gameplay.
                    var raw = UnityEngine.Object.FindObjectsOfType(
                        Il2CppInterop.Runtime.Il2CppType.Of<statusUI>());
                    if (raw == null || raw.Length == 0) return; // not in gameplay yet - try again next frame

                    Install();
                    if (_installed) _armedAtFrame = _cachedFrame;
                    return;
                }

                int elapsed = _cachedFrame - _armedAtFrame;
                if (_branchEntryHitCount + _callDoneHitCount >= MaxTotalHitsSafety || elapsed >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] HIDDEN9THPATH-AUTOUNINSTALL; " +
                        $"branchEntryHits={_branchEntryHitCount}; callDoneHits={_callDoneHitCount}; " +
                        $"armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] Hidden9thSlotPathTrace.Tick failed safely: {ex.Message}");
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

                _branchEntryAddress = new IntPtr(checked(moduleBase.ToInt64() + (BranchEntryVa - GameAssemblyPreferredBase)));
                _callDoneAddress = new IntPtr(checked(moduleBase.ToInt64() + (CallDoneVa - GameAssemblyPreferredBase)));

                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointsOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HIDDEN9THPATH-INSTALLED; " +
                    $"branchEntryVa=0x{_branchEntryAddress.ToInt64():X}; callDoneVa=0x{_callDoneAddress.ToInt64():X}; " +
                    "mechanism=hardware-execute-breakpoint(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] Hidden9thSlotPathTrace install refused safely: {ex}");
                Uninstall();
            }
        }

        internal static void Uninstall()
        {
            try
            {
                if (_branchEntryAddress != IntPtr.Zero || _callDoneAddress != IntPtr.Zero)
                {
                    try { RemoveHardwareBreakpointsOnCurrentThread(); }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning(
                            $"[NocturneModernGameplay] Hidden9thSlotPathTrace breakpoint removal failed: {ex.Message}");
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
                _branchEntryAddress = IntPtr.Zero;
                _callDoneAddress = IntPtr.Zero;
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

                Marshal.WriteInt64(ctx, OffsetDr0, _branchEntryAddress.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr1, _callDoneAddress.ToInt64());

                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                long mask = Dr7EnableL0L1Mask | Dr7RwLenBothMask;
                dr7 = (dr7 & ~mask) | Dr7EnableL0L1Mask;
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
                long dr1Rb = Marshal.ReadInt64(ctx, OffsetDr1);
                long dr7Rb = Marshal.ReadInt64(ctx, OffsetDr7);
                if (dr0Rb != _branchEntryAddress.ToInt64() || dr1Rb != _callDoneAddress.ToInt64() ||
                    (dr7Rb & 0x5L) != 0x5L)
                    throw new InvalidOperationException(
                        $"hardware breakpoint readback mismatch; dr0=0x{dr0Rb:X} dr1=0x{dr1Rb:X} dr7=0x{dr7Rb:X}");
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
                dr7 &= ~Dr7EnableL0L1Mask;
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

        // Runs inside #DB exception dispatch. Both watched addresses sit
        // inside a rarely-executed dedicated branch, so every hit is
        // logged directly - no dedup, no allocation, no IL2CPP access.
        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                IntPtr exceptionAddress = Marshal.ReadIntPtr(exceptionRecordPtr, 0x10);
                bool isBranchEntry = exceptionAddress == _branchEntryAddress;
                bool isCallDone = exceptionAddress == _callDoneAddress;
                if (!isBranchEntry && !isCallDone) return ExceptionContinueSearch;

                if (isBranchEntry) _branchEntryHitCount++;
                if (isCallDone) _callDoneHitCount++;

                if (_pendingCount < MaxHits)
                {
                    ref PendingHit slot = ref _pending[_pendingCount];
                    slot.Frame = _cachedFrame;
                    slot.Seq = _cachedSeq;
                    slot.BridgeActive = _cachedBridgeActive;
                    slot.IsCallDone = isCallDone;
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
                    "[NocturneModernGameplay] HIDDEN9THPATH-HIT; " +
                    $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; " +
                    $"event={(hit.IsCallDone ? "CALL-DONE(cmpSetupObject(skillCurObj[8],true) completed)" : "BRANCH-ENTRY(target==8 confirmed)")}.");
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
