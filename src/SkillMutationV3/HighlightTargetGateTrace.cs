using System;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - direct capture of the highlight-on decision
    // gate's real runtime values (read-only, no writes). Session
    // continuation after SkillCurObjNativeCallerProbe (hardware breakpoint
    // on cmpUpdate.cmpSetupObject's own entry) captured a real-machine 60s
    // window where indices 0/1/2/3/4/5/6 (7 of the 8 normal skillCurObj[]
    // rows) ALL called cmpSetupObject(true) from the exact same return
    // address (staticVa 0x1822D9C6B), while index=7 never appeared with
    // value=true at all - despite the user's input sequence passing through
    // it.
    //
    // Static disassembly of the function containing 0x1822D9C6B (starts at
    // VA 0x1822D97C0; .analysis/disasm_0x1822d9c6b_context.py,
    // disasm_0x1822d9c6b_funcstart.py) found the exact decision gate this
    // loop uses, byte-confirmed:
    //   0x1822D9AF3  movzx ecx, byte ptr [rax+0x14]   ; CursorPos.Shift-shaped field
    //   0x1822D9AF7  movsx eax, word ptr [rax+0x12]   ; CursorPos.Index-shaped field (sign-extended)
    //   0x1822D9AFB  add   ecx, eax                    ; ecx = Shift + Index ("target")
    //   0x1822D9AFD  cmp   ecx, ebx                     ; target vs ebx (the row this loop iteration is considering)
    //   0x1822D9AFF  jne   <skip - do not call cmpSetupObject(skillCurObj[ebx], true) this row>
    // Offsets +0x12 (Index, short)/+0x14 (Shift, byte) match the shape
    // CmpMenuCursorTrace.cs already reads as CursorPos.Index/CursorPos.Shift.
    // ebx is bounded by the loop's upper limit, [r15+0x48] (r15 = this
    // function's 4th argument), read once into edi before the loop and not
    // clobbered before this comparison.
    //
    // Per User's 2026-09-14 design agreement: this settles the one number
    // this investigation still needs - what `target` (Shift+Index) actually
    // equals at the exact moment the cursor sits on the hidden entry, when
    // no ebx in [0, loopUpper) ever matches it. Breaking exactly on
    // 0x1822D9AFD (the `cmp` itself, one instruction) and reading
    // ECX/EBX/EAX straight from the CONTEXT record - plus [R15+0x48] for
    // the loop's upper bound (R15 itself, not EDI - see the 2026-09-14 fix
    // note on loopUpper below) - all plain register/memory reads, no
    // IL2CPP access, no allocation - gives that without needing a Harmony
    // patch (this native loop has no known managed method to patch) and
    // without walking the stack.
    //
    // Frequency discipline (explicit User requirement, since this gate runs
    // every loop iteration - up to loopUpper times - every frame this
    // function executes, not just during the forget UI): the VEH itself
    // still does only the cheap read-and-compare on every hit (matching
    // this mod's established discipline), but LOGGING is gated hard behind
    // two independent filters, both required:
    //   1. `_forgetUiActive` - a plain bool cached by Tick() (ordinary
    //      managed context, main thread) from gbwk.SeqInfo.Current == 21/22
    //      (the same forget-UI window every sibling trace in this
    //      investigation scopes itself to) - read by the VEH as a plain
    //      field, never computed there.
    //   2. Dedup on the (target, ebx) pair - the same fixed-array linear-
    //      scan technique SkillCurObjNativeCallerProbe.cs already uses, so
    //      a given (target, ebx) combination is only ever logged once per
    //      arm cycle, however many frames/iterations repeat it.
    internal static class HighlightTargetGateTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;
        // The `cmp ecx, ebx` instruction itself (2 bytes: 3B CB), inside
        // the function starting at VA 0x1822D97C0 that
        // SkillCurObjNativeCallerProbe's returnAddress capture (0x1822D9C6B)
        // resolved to. Breaking here (rather than the call site itself)
        // captures the comparison's own inputs before the jne decides
        // whether to skip.
        private const long TargetVa = 0x1822D9AFDL;
        private static readonly byte[] ExpectedBytesEntry = { 0x3B, 0xCB };

        // 2026-09-14 recalibration: a real-machine run hit this exact
        // capacity (32) after observing only 4 distinct `target` values
        // (8 ebx entries each, dedup-recorded once per (target, ebx) pair =
        // 32), auto-uninstalling at armedFrames=3151 - well before the
        // full 60s window, and before the user's requested navigation
        // (rows 5/6/7, then a deliberate stay on the hidden entry) could
        // all be captured. Sized generously higher so a full session's
        // worth of distinct target values (realistically well under 20,
        // times up to 16 ebx values each) fits with margin; the linear
        // dedup scan stays cheap at this size (only runs on an actual
        // match, not the miss path).
        private const int MaxHits = 256;
        private const int SeenCapacity = 256;
        private const int MaxArmedFrames = 3600; // ~60s at 60fps - hard safety cap regardless of hits
        // First-guess safety cap for THIS address (no prior real-machine
        // measurement exists yet, unlike SkillCurObjNativeCallerProbe's
        // recalibrated 20000 - see that file's 2026-09-14 note for why an
        // uncalibrated guess is dangerous). Each hit here is a handful of
        // register reads/compares, cheaper than that probe's per-hit work,
        // so this is set generously high to avoid repeating the same
        // premature-cutoff mistake; RawHitCount is logged at uninstall so
        // the real rate can be read back afterward and this recalibrated
        // if needed.
        private const int MaxTotalHitsSafety = 300000;
        private static int _armedAtFrame = -1;

        // Updated once per Tick() call (ordinary managed context, main
        // thread) so the VEH can gate logging by reading these as plain
        // fields - never by calling into IL2CPP/Unity itself.
        private static int _cachedFrame;
        private static bool _forgetUiActive;

        private struct PendingHit
        {
            internal int Frame;
            internal int Target; // ecx = Shift + Index at the cmp
            internal int Ebx;    // row this loop iteration is considering
            internal int Index;  // eax = CursorPos.Index-shaped field alone
            internal int Shift;  // derived: Target - Index
            internal int LoopUpper; // [r15+0x48] read directly (NOT edi - see 2026-09-14 fix below), the loop's exclusive upper bound
        }

        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;

        // Raw VEH hits regardless of the forgetUiActive/dedup filters -
        // logged at uninstall time so the real firing rate of this address
        // (unknown before this session) can be read back and
        // MaxTotalHitsSafety recalibrated if it turns out to be wrong, the
        // same way SkillCurObjNativeCallerProbe's was.
        private static long _rawHitCount;
        private static long _loggedHitCount;

        // Dedup key = (target << 32) | (uint)ebx. Both target and ebx are
        // small (single-digit row indices in practice), so this never
        // risks collision; fixed-size array + linear scan (<=32 entries),
        // matching this mod's no-allocation-in-the-VEH discipline.
        private static readonly long[] _seenKeys = new long[SeenCapacity];
        private static int _seenCount;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr7 = 0x70;
        private const int OffsetRax = 0x78;
        private const int OffsetRcx = 0x80;
        private const int OffsetRbx = 0x90;
        private const int OffsetR15 = 0xF0;
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
        // context only). Installs once GameAssembly.dll is available,
        // refreshes the forgetUiActive gate every frame, and enforces the
        // auto-uninstall safety valve.
        internal static void Tick()
        {
            if (!Enabled || _uninstalled) return;

            try
            {
                _cachedFrame = UnityEngine.Time.frameCount;

                var gbwk = rstinit.GBWK;
                int seq = -1;
                try { if (gbwk != null && gbwk.Pointer != IntPtr.Zero) seq = gbwk.SeqInfo.Current; } catch { }
                _forgetUiActive = seq == 21 || seq == 22;

                if (!_installed)
                {
                    // 2026-09-14 fix: a real-machine run showed this trace
                    // installing (and starting its 60s/3600-frame clock)
                    // the instant GameAssembly.dll became available - i.e.
                    // at the title screen, before the player had even
                    // finished loading a save. By the time they reached the
                    // forget UI, MaxArmedFrames had already elapsed during
                    // the title/load screens (rawHits=0 the whole run).
                    // SkillCurObjNativeCallerProbe never had this problem
                    // because it already gated its own Install() on a live
                    // statusUI instance existing; mirror that same gate
                    // here (this trace doesn't need statusUI itself, only
                    // proof gameplay has actually started).
                    var raw = UnityEngine.Object.FindObjectsOfType(
                        Il2CppInterop.Runtime.Il2CppType.Of<statusUI>());
                    if (raw == null || raw.Length == 0) return; // not in gameplay yet - try again next frame

                    Install();
                    if (_installed) _armedAtFrame = UnityEngine.Time.frameCount;
                    return;
                }

                int elapsed = UnityEngine.Time.frameCount - _armedAtFrame;
                if (_seenCount >= SeenCapacity ||
                    _rawHitCount >= MaxTotalHitsSafety ||
                    elapsed >= MaxArmedFrames)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] HIGHLIGHTGATE-AUTOUNINSTALL; " +
                        $"distinctPairs={_seenCount}; rawHits={_rawHitCount}; loggedHits={_loggedHitCount}; " +
                        $"armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HighlightTargetGateTrace.Tick failed safely: {ex.Message}");
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
                long rva = TargetVa - GameAssemblyPreferredBase;
                IntPtr address = new IntPtr(checked(moduleBase.ToInt64() + rva));

                // Nothing in this mod patches this address - fail closed
                // (00_PROJECT_RULES.md) rather than install on an
                // unrecognized byte pattern, per this project's discipline
                // of never assuming a computed address is what static
                // analysis said it was.
                byte[] actual = new byte[ExpectedBytesEntry.Length];
                Marshal.Copy(address, actual, 0, actual.Length);
                if (!BytesEqual(actual, ExpectedBytesEntry))
                    throw new InvalidOperationException(
                        $"highlight-gate cmp unrecognized bytes; expected=[{FormatBytes(ExpectedBytesEntry)}]; " +
                        $"actual={FormatBytes(actual)} - refusing to install");

                _targetAddress = address;
                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HIGHLIGHTGATE-INSTALLED; " +
                    $"targetVa=0x{_targetAddress.ToInt64():X}; " +
                    "mechanism=hardware-breakpoint(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] HighlightTargetGateTrace install refused safely: {ex}");
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
                            $"[NocturneModernGameplay] HighlightTargetGateTrace breakpoint removal failed: {ex.Message}");
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

        // Runs inside #DB exception dispatch. Miss path (exceptionAddress
        // != our one target) does nothing but the address compare and an
        // immediate resume. A match reads four plain 32-bit registers,
        // checks the plain _forgetUiActive bool, and (only if new) records
        // one fixed-size struct - no logging, no IL2CPP access, no
        // allocation here.
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

                _rawHitCount++;

                if (_forgetUiActive)
                {
                    int target = Marshal.ReadInt32(contextRecordPtr, OffsetRcx); // ecx
                    int ebx = Marshal.ReadInt32(contextRecordPtr, OffsetRbx);
                    int index = Marshal.ReadInt32(contextRecordPtr, OffsetRax); // eax
                    int shift = target - index;

                    // 2026-09-14 fix: EDI does NOT hold the loop's upper
                    // bound at this address - `mov rdi, [rax+0x88]` at
                    // 0x1822D9ABF (part of the gate-1 check, between the
                    // original `mov edi,[r15+0x48]` load and this cmp)
                    // clobbers it with an unrelated pointer's low 32 bits
                    // first (confirmed by a real-machine run that logged a
                    // nonsense loopUpper=539766576 on every hit). R15
                    // itself (this function's 4th argument) is never
                    // reassigned between the loop's start and this
                    // instruction, so read [R15+0x48] directly from memory
                    // instead - the same value EDI held right after that
                    // original load, before it got overwritten. Guarded in
                    // its own try/catch so a bad pointer here can never
                    // skip the resume-flag write below (which would leave
                    // the process to crash on an unhandled #DB).
                    int loopUpper = -1;
                    try
                    {
                        long r15 = Marshal.ReadInt64(contextRecordPtr, OffsetR15);
                        if (r15 != 0) loopUpper = Marshal.ReadInt32(new IntPtr(r15), 0x48);
                    }
                    catch { loopUpper = -1; }

                    long key = ((long)target << 32) | (uint)ebx;

                    bool alreadySeen = false;
                    for (int k = 0; k < _seenCount; k++)
                    {
                        if (_seenKeys[k] == key) { alreadySeen = true; break; }
                    }

                    if (!alreadySeen && _seenCount < SeenCapacity && _pendingCount < MaxHits)
                    {
                        _seenKeys[_seenCount++] = key;

                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Frame = _cachedFrame;
                        slot.Target = target;
                        slot.Ebx = ebx;
                        slot.Index = index;
                        slot.Shift = shift;
                        slot.LoopUpper = loopUpper;
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
                _loggedHitCount++;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HIGHLIGHTGATE-HIT; " +
                    $"frame={hit.Frame}; ebx={hit.Ebx}; target={hit.Target}; index={hit.Index}; " +
                    $"shift={hit.Shift}; loopUpper={hit.LoopUpper}; match={hit.Target == hit.Ebx}.");
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
