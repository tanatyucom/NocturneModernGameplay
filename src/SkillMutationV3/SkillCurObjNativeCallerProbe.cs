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
    // (PowerUpMutationBit6RawProbe.cs) and point it at ONE address so a
    // hit's return address (read directly off the stack at raw function
    // entry, before any prologue runs - RSP itself IS the return address
    // slot at that exact instant) identifies the REAL native caller
    // regardless of whether the call reaching the target was direct or
    // indirect.
    //
    // 2026-09-14 CORRECTION (important, supersedes the original design):
    // this class originally broke on UnityEngine.GameObject.SetActive's own
    // entry (VA 0x182842EB0, the same address SkillCurObjSetActiveTrace.cs
    // Harmony-patches). A real-machine run captured EVERY hit across
    // indices 0/4/5/6/7/8 resolving to the exact same static VA,
    // 0x182620AFD - but fresh disassembly of that address this session
    // (.analysis/disasm_cmpmenucursor_0x1826207f0.py) proved it is NOT
    // inside cmpUpdate.cmpMenuCursor as an earlier session had recorded;
    // it is the instruction immediately after `call 0x182842eb0` INSIDE
    // cmpUpdate.cmpSetupObject(GameObject Obj, bool set) (VA 0x182620A80),
    // which is a thin wrapper: it checks the object's current active state
    // and only calls the real SetActive if `set` actually differs from it.
    // Since skillCurObj[i].SetActive always goes through this wrapper, a
    // breakpoint on SetActive's own entry can only ever see cmpSetupObject
    // as "the caller" - it is one stack frame short of the real
    // decision-making logic (cmpMenuCursor or whatever else calls
    // cmpSetupObject). Per User's 2026-09-14 direction, the breakpoint now
    // sits on cmpSetupObject's own entry instead, one hop further up the
    // call chain, using the exact same technique.
    //
    // Explicitly scoped to stay cheap despite the target sitting one call
    // below a hot, general-purpose Unity wrapper (called constantly for
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
        // cmpUpdate.cmpSetupObject(GameObject Obj, bool set) - a thin
        // SetActive wrapper, one call below the real logic caller (see
        // 2026-09-14 correction above). RCX=Obj, RDX(low byte)=set, same
        // calling-convention shape SetActive itself has, so the rest of
        // this probe's RCX-filter / RSP-return-address technique carries
        // over unchanged.
        private const long CmpSetupObjectVa = 0x182620A80L;
        // "mov qword ptr [rsp+8], rbx" - cmpSetupObject's real first
        // instruction, confirmed via fresh capstone disassembly of
        // GameAssembly.dll read directly from disk this session
        // (.analysis/disasm_cmpmenucursor_0x1826207f0.py), not assumed.
        private static readonly byte[] ExpectedBytesEntry = { 0x48, 0x89, 0x5C, 0x24, 0x08 };

        private const int MaxTargets = 16;
        private static readonly long[] _targetPointers = new long[MaxTargets];
        private static readonly int[] _targetIndex = new int[MaxTargets];
        private static int _targetCount;

        // Per User's 2026-09-14 design agreement: don't stop at the first
        // hit (that caught only one special-case caller for index=8, a
        // rare burst, not the everyday row-highlight path). Dedup keeps
        // repeated hits of the SAME (index, set, returnAddress) triple
        // (e.g. a blinking row firing SetActive(true)/SetActive(false)
        // dozens of times a second from the same site, or - as discovered
        // this session - a per-frame reset loop calling cmpSetupObject
        // (false) for all 16 skillCurObj[] elements every single frame)
        // from burning through capacity before a genuinely different
        // index/set/caller ever gets a chance to be seen.
        //
        // 2026-09-14 REMOVED the index/caller-convergence early-stop
        // (DistinctIndexGoal / DistinctCallerGoal, and the
        // MinArmedFramesBeforeEarlyStop dwell-time guard added to patch it)
        // after TWO consecutive real-machine runs proved it fires from
        // background noise, not genuine signal: the forget UI's own
        // per-frame loop calls cmpSetupObject(false) on all 16
        // skillCurObj[] elements from one single caller VA, so
        // distinctIndexCount and distinctCallerCount are BOTH already
        // maxed out before the user gets any chance to move the cursor.
        // A dwell-time guard only delays this false-positive by a fixed
        // amount; it does not distinguish it from genuine post-navigation
        // diversity. Dedup already makes the noise cheap (it collapses to
        // the same 16 (index, false, returnAddress) keys forever, never
        // consuming more of SeenCapacity no matter how many frames pass),
        // so the two remaining stop conditions below (capacity exhaustion,
        // hard timeout) are sufficient and reliable on their own -
        // distinctIndexCount/distinctCallerCount are still computed and
        // logged at uninstall time purely as an analysis aid.
        private const int MaxHits = 64;
        private const int SeenCapacity = 64;
        private const int MaxArmedFrames = 3600; // ~60s at 60fps - hard safety cap regardless of hits
        // 2026-09-14 recalibration: a real-machine run measured the
        // per-frame reset-loop noise (see above) at totalHits=4007 over
        // armedFrames=1167 - about 3.4 matched (pre-dedup) hits per frame,
        // sustained. _totalHitCount increments on every VEH match
        // regardless of dedup (it bounds total exception-handling
        // overhead, not distinct data), so the OLD value of 4000 tripped
        // this "hard cap even if dedup somehow never matches" safety valve
        // from ordinary background noise alone at ~19.5s - cutting the
        // probe's window to under a third of MaxArmedFrames's intended 60s
        // before the user had time to reach the hidden-entry moment.
        // Extrapolating that same noise rate across the full 60s
        // (3.4 * 3600 =~ 12,360) with a comfortable margin for genuine
        // navigation hits on top:
        private const int MaxTotalHitsSafety = 20000;
        private static int _armedAtFrame = -1;

        // Updated once per Tick() call (ordinary managed context, main
        // thread, every frame via ModMain.OnUpdate) so the VEH can attach a
        // frame number to a hit by reading this plain field - NOT by
        // calling UnityEngine.Time.frameCount itself, which would be an
        // IL2CPP/engine call from exception-handler context and violates
        // this class's no-IL2CPP-access-in-the-VEH discipline.
        private static int _cachedFrame;

        private struct PendingHit
        {
            internal int Index;
            internal long ReturnAddress;
            internal long Rcx;
            internal bool Value; // cmpSetupObject's `set` argument (rdx low byte at entry)
            internal int Frame;
        }

        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;
        private static int _totalHitCount;

        // Dedup key = (index << 49) | (value << 48) | (returnAddress &
        // 48-bit mask) - the full (index, set, returnAddress) triple (see
        // VectoredHandler); x64 user-mode canonical addresses fit in 48
        // bits, so no information is lost. Plain fixed-size array + linear
        // scan (<=64 entries) - no allocation, no managed collection,
        // matching this class's stated VEH discipline.
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
                _cachedFrame = UnityEngine.Time.frameCount;

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
                if (_seenCount >= SeenCapacity ||
                    _totalHitCount >= MaxTotalHitsSafety ||
                    elapsed >= MaxArmedFrames)
                {
                    // distinctIndexCount/distinctCallerCount are analysis
                    // aids only (see 2026-09-14 note above) - they no
                    // longer influence when this probe stops.
                    int distinctCallerCount = ComputeDistinctCallerCount();
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLCUROBJ-NATIVE-CALLER-AUTOUNINSTALL; " +
                        $"distinctIndexCount={_distinctIndexCount}; distinctCallerCount={distinctCallerCount}; " +
                        $"distinctPairs={_seenCount}; totalHits={_totalHitCount}; armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCurObjNativeCallerProbe.Tick failed safely: {ex.Message}");
            }
        }

        // Ordinary managed context only (called from Tick, never the VEH).
        // _seenCount is capped at SeenCapacity (<=64), so an O(n^2) scan
        // here is negligible - this is the same discipline the VEH's own
        // dedup scan already uses, just run outside the hot path.
        private static int ComputeDistinctCallerCount()
        {
            int distinct = 0;
            for (int i = 0; i < _seenCount; i++)
            {
                long callerI = _seenKeys[i] & 0xFFFFFFFFFFFFL;
                bool seenBefore = false;
                for (int j = 0; j < i; j++)
                {
                    if ((_seenKeys[j] & 0xFFFFFFFFFFFFL) == callerI) { seenBefore = true; break; }
                }
                if (!seenBefore) distinct++;
            }
            return distinct;
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
                long rva = CmpSetupObjectVa - GameAssemblyPreferredBase;
                IntPtr address = new IntPtr(checked(moduleBase.ToInt64() + rva));

                // Unlike GameObject.SetActive (which SkillCurObjSetActiveTrace.cs
                // Harmony-patches, so its prologue is overwritten with a
                // jmp-stub at runtime), nothing in this mod currently
                // Harmony-patches cmpUpdate.cmpSetupObject, so the pristine
                // prologue is expected. Still accept a jmp-stub defensively
                // (harmless if that ever changes) and refuse only on a
                // genuinely unrecognized third pattern.
                byte[] actual = new byte[ExpectedBytesEntry.Length];
                Marshal.Copy(address, actual, 0, actual.Length);
                bool isPristine = BytesEqual(actual, ExpectedBytesEntry);
                bool isHarmonyJmpStub = actual[0] == 0xFF && actual[1] == 0x25;
                if (!isPristine && !isHarmonyJmpStub)
                    throw new InvalidOperationException(
                        $"cmpSetupObject entry unrecognized bytes; expectedEither=[{FormatBytes(ExpectedBytesEntry)}] " +
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
                    $"cmpSetupObjectVa=0x{_targetAddress.ToInt64():X}; targetCount={_targetCount}; " +
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
                    long rdx = Marshal.ReadInt64(contextRecordPtr, OffsetRdx);
                    bool value = (rdx & 0xFF) != 0;

                    // Dedup key MUST be the full (index, set, returnAddress)
                    // triple, not just (index, returnAddress). A blinking
                    // row calls SetActive(true) then SetActive(false) from
                    // the exact same call site - a key that dropped `value`
                    // would record only whichever of the two fired first
                    // from that site and silently drop the other forever
                    // (2026-09-14 fix; the prior key shape did this).
                    // matchedIndex fits in <=16 (4 bits) -> bits 49+, value
                    // -> bit 48, returnAddress low 48 bits -> bits 0-47
                    // (x64 user-mode canonical addresses fit in 48 bits, so
                    // no information is lost).
                    long key = ((long)matchedIndex << 49) | ((value ? 1L : 0L) << 48) |
                        (returnAddress & 0xFFFFFFFFFFFFL);

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

                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Index = matchedIndex;
                        slot.ReturnAddress = returnAddress;
                        slot.Rcx = rcx;
                        slot.Value = value;
                        slot.Frame = _cachedFrame;
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
                    $"frame={hit.Frame}; index={hit.Index}; returnAddress=0x{hit.ReturnAddress:X}; staticVa=0x{staticVa:X}; " +
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
