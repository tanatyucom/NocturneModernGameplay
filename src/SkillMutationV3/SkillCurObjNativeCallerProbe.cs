using System;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - native caller capture for skillCurObj[i].
    // SetActive() (read-only, no writes). Session continuation after an
    // exhaustive static search (all ~1000 direct call sites to
    // UnityEngine.GameObject.SetActive's real native VA, cross-checked
    // against the RUNTIME-MEASURED skillCurObj field offset 0x128 -
    // StatusUiFieldOffsetProbe confirmed cpp2il's reported offset was
    // correct all along) found ZERO direct callers anywhere in the
    // confirmed-live draw chain (cmpStatus.cmpUpdateStatus ->
    // cmpDrawStatus.cmpDrawStatusCom -> ComEx -> ComEx2 -> cmpDrawSkill).
    // That combination (correct offset, exhaustive direct-call scan, zero
    // hits) means the real caller almost certainly reaches SetActive
    // through an indirect call (virtual dispatch / delegate / function
    // pointer) that a plain E8-xref scan cannot find - the same wall this
    // investigation hit earlier with cmpDrawSkillList.
    //
    // Per this session's explicit design agreement: do NOT chase that
    // indirection statically (vtable resolution across the whole binary
    // is a much larger undertaking). Instead, borrow the hardware-
    // breakpoint + VEH mechanism already proven reliable in this mod
    // (PowerUpMutationBit6RawProbe.cs) and point it at ONE address -
    // UnityEngine.GameObject.SetActive's own native entry (VA
    // 0x182842EB0, the same address SkillCurObjSetActiveTrace.cs already
    // Harmony-patches) - filtered, INSIDE the VEH itself, to only the 8
    // known skillCurObj[0..7] GameObject pointers, so a hit's return
    // address (read directly off the stack at raw function entry, before
    // any prologue runs - RSP itself IS the return address slot at that
    // exact instant) identifies the REAL native caller regardless of
    // whether the call reaching SetActive was direct or indirect.
    //
    // Explicitly scoped to stay cheap despite SetActive being an
    // extremely hot, general-purpose Unity API (called constantly for
    // unrelated UI all over the game, unlike PowerUpMutationBit6RawProbe's
    // four points which only sit inside one narrow gameplay-specific
    // function): the VEH does nothing but a linear scan of at most 16
    // plain longs against RCX for every non-matching call (the
    // overwhelming majority) before immediately resuming - no logging, no
    // IL2CPP access, no allocation on the miss path, matching this
    // project's stated concern about hitches from broader/heavier
    // high-frequency diagnostics. Auto-uninstalls itself (from ordinary
    // managed context, never from the VEH) after either
    // MaxHits captures or MaxArmedFrames frames, whichever comes first, so
    // the total exposure window is bounded even if skillCurObj[i] never
    // fires again during a given session.
    internal static class SkillCurObjNativeCallerProbe
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;
        private const long SetActiveVa = 0x182842EB0L;
        // "mov qword ptr [rsp+8], rbx" - SetActive's real first instruction,
        // confirmed via fresh capstone disassembly of GameAssembly.dll read
        // directly from disk this session (not assumed).
        private static readonly byte[] ExpectedBytesEntry = { 0x48, 0x89, 0x5C, 0x24, 0x08 };

        private const int MaxTargets = 16;
        private static readonly long[] _targetPointers = new long[MaxTargets];
        private static readonly int[] _targetIndex = new int[MaxTargets];
        private static int _targetCount;

        // Per User's 2026-09-14 design agreement: don't stop at the first
        // hit (that caught only one special-case caller for index=8, a
        // rare burst, not the everyday row-highlight path). Keep the
        // breakpoint armed until either DISTINCT (index, caller) pairs
        // across a reasonable spread of indices have been captured, or a
        // hard time/count safety cap is hit - whichever first. Dedup keeps
        // repeated hits of the SAME (index, returnAddress) pair (e.g. a
        // blinking row firing SetActive dozens of times a second) from
        // burning through capacity before a genuinely different index/
        // caller ever gets a chance to be seen.
        private const int MaxHits = 64;
        private const int SeenCapacity = 64;
        private const int DistinctIndexGoal = 6; // stop early once this many distinct indices seen
        private const int MaxArmedFrames = 3600; // ~60s at 60fps - hard safety cap regardless of hits
        private const int MaxTotalHitsSafety = 4000; // hard cap even if dedup somehow never matches
        private static int _armedAtFrame = -1;

        private struct PendingHit
        {
            internal int Index;
            internal long ReturnAddress;
            internal long Rcx;
            internal bool Value; // SetActive's bool argument (rdx low byte at entry)
        }

        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;
        private static int _totalHitCount;

        // Dedup key = (index << 48) | (returnAddress & 48-bit mask) - x64
        // user-mode canonical addresses fit in 48 bits, so no information
        // is lost. Plain fixed-size array + linear scan (<=64 entries) -
        // no allocation, no managed collection, matching this class's
        // stated VEH discipline.
        private static readonly long[] _seenKeys = new long[SeenCapacity];
        private static int _seenCount;
        private static readonly bool[] _indexSeen = new bool[MaxTargets];
        private static int _distinctIndexCount;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr7 = 0x70;
        private const int OffsetRcx = 0x80;
        private const int OffsetRdx = 0x88;
        private const int OffsetRsp = 0x98;
        private const int OffsetRip = 0xF8;
        private const int ResumeFlagBit = 0x10000;

        private const long Dr7EnableMask = 0x1L; // L0 only (bit 0)
        private const long Dr7RwLenMask = 0xFL << 16; // R/W0+LEN0 = bits 16-19, execute = 0

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
        // context only). Builds the target-pointer cache (needs IL2CPP
        // access, so must happen here, never from the VEH), arms on first
        // success, and enforces the auto-uninstall safety valve.
        internal static void Tick()
        {
            if (!Enabled || _uninstalled) return;

            try
            {
                if (!_installed)
                {
                    RefreshTargets();
                    if (_targetCount == 0) return; // statusUI/skillCurObj not ready yet - try again next frame
                    Install();
                    if (_installed) _armedAtFrame = UnityEngine.Time.frameCount;
                    return;
                }

                // Refresh target pointers periodically in case the
                // statusUI instance is recreated (scene/menu reopen) -
                // cheap, ordinary managed context only.
                RefreshTargets();

                int elapsed = UnityEngine.Time.frameCount - _armedAtFrame;
                if (_distinctIndexCount >= DistinctIndexGoal ||
                    _totalHitCount >= MaxTotalHitsSafety ||
                    elapsed >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLCUROBJ-NATIVE-CALLER-AUTOUNINSTALL; " +
                        $"distinctIndexCount={_distinctIndexCount}; distinctPairs={_seenCount}; " +
                        $"totalHits={_totalHitCount}; armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCurObjNativeCallerProbe.Tick failed safely: {ex.Message}");
            }
        }

        private static void RefreshTargets()
        {
            try
            {
                var raw = UnityEngine.Object.FindObjectsOfType(
                    Il2CppInterop.Runtime.Il2CppType.Of<statusUI>());
                if (raw == null || raw.Length == 0) return;

                var ui = raw[0].Cast<statusUI>();
                var arr = ui?.skillCurObj;
                if (arr == null) return;

                int n = Math.Min(arr.Length, MaxTargets);
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    var go = arr[i];
                    if (go != null && go.Pointer != IntPtr.Zero)
                    {
                        _targetPointers[count] = go.Pointer.ToInt64();
                        _targetIndex[count] = i;
                        count++;
                    }
                }
                _targetCount = count;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCurObjNativeCallerProbe.RefreshTargets failed: {ex.Message}");
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
                long rva = SetActiveVa - GameAssemblyPreferredBase;
                IntPtr address = new IntPtr(checked(moduleBase.ToInt64() + rva));

                // Real-machine testing this session showed the LIVE process's
                // bytes here are "FF 25 xx xx xx xx" (jmp [rip+disp32]) -
                // NOT the original prologue read from the on-disk file
                // earlier. Root cause: SkillCurObjSetActiveTrace.cs already
                // Harmony-patches this exact method, and Harmony detours by
                // overwriting the target's own prologue with a jump to its
                // generated trampoline. Native callers still `call` this
                // same fixed address regardless (only the bytes AT it
                // changed, not who targets it), so the hardware breakpoint
                // still correctly captures their return address either way.
                // Accept both the pristine prologue (if this ever runs
                // before that other patch applies) and the jmp-stub form;
                // refuse only on a genuinely unrecognized third pattern.
                byte[] actual = new byte[ExpectedBytesEntry.Length];
                Marshal.Copy(address, actual, 0, actual.Length);
                bool isPristine = BytesEqual(actual, ExpectedBytesEntry);
                bool isHarmonyJmpStub = actual[0] == 0xFF && actual[1] == 0x25;
                if (!isPristine && !isHarmonyJmpStub)
                    throw new InvalidOperationException(
                        $"SetActive entry unrecognized bytes; expectedEither=[{FormatBytes(ExpectedBytesEntry)}] " +
                        $"or [FF 25 xx xx xx xx]; actual={FormatBytes(actual)} - refusing to install");

                _targetAddress = address;
                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLCUROBJ-NATIVE-CALLER-INSTALLED; " +
                    $"setActiveVa=0x{_targetAddress.ToInt64():X}; targetCount={_targetCount}; " +
                    "mechanism=hardware-breakpoint(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] SkillCurObjNativeCallerProbe install refused safely: {ex}");
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
                            $"[NocturneModernGameplay] SkillCurObjNativeCallerProbe breakpoint removal failed: {ex.Message}");
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

        // Runs inside #DB exception dispatch. Miss path (the overwhelming
        // majority of SetActive calls, unrelated to skillCurObj) does
        // nothing but a linear scan of <=16 plain longs and an immediate
        // resume - no logging, no IL2CPP access, no allocation. Only a
        // match does the (still cheap) extra work of reading RSP/RDX and
        // storing one fixed-size struct.
        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                IntPtr exceptionAddress = Marshal.ReadIntPtr(exceptionRecordPtr, 0x10);
                if (exceptionAddress != _targetAddress) return ExceptionContinueSearch;

                long rcx = Marshal.ReadInt64(contextRecordPtr, OffsetRcx);
                int matchedIndex = -1;
                int n = _targetCount;
                for (int i = 0; i < n; i++)
                {
                    if (_targetPointers[i] == rcx) { matchedIndex = _targetIndex[i]; break; }
                }

                if (matchedIndex >= 0)
                {
                    _totalHitCount++;

                    long rsp = Marshal.ReadInt64(contextRecordPtr, OffsetRsp);
                    long returnAddress = Marshal.ReadInt64(new IntPtr(rsp), 0);
                    long key = ((long)matchedIndex << 48) | (returnAddress & 0xFFFFFFFFFFFFL);

                    bool alreadySeen = false;
                    for (int k = 0; k < _seenCount; k++)
                    {
                        if (_seenKeys[k] == key) { alreadySeen = true; break; }
                    }

                    if (!alreadySeen && _seenCount < SeenCapacity && _pendingCount < MaxHits)
                    {
                        _seenKeys[_seenCount++] = key;
                        if (!_indexSeen[matchedIndex])
                        {
                            _indexSeen[matchedIndex] = true;
                            _distinctIndexCount++;
                        }

                        long rdx = Marshal.ReadInt64(contextRecordPtr, OffsetRdx);
                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Index = matchedIndex;
                        slot.ReturnAddress = returnAddress;
                        slot.Rcx = rcx;
                        slot.Value = (rdx & 0xFF) != 0;
                        _pendingCount++;
                    }
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
                long staticVa = GameAssemblyPreferredBase + (hit.ReturnAddress - _actualModuleBase);
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLCUROBJ-NATIVE-CALLER-HIT; " +
                    $"index={hit.Index}; returnAddress=0x{hit.ReturnAddress:X}; staticVa=0x{staticVa:X}; " +
                    $"rcx=0x{hit.Rcx:X}; value={hit.Value}.");
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
