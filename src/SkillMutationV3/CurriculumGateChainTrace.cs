using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - gate1/gate2 vs gate3 split for
    // rstcalc.rstCreateBeforeSkillList's per-candidate scan loop (VA
    // 0x182280460-0x182280820, identified this session as the owner of
    // [r13+0x10]/outList.count, see R13FetchAndWriteWatchTrace and
    // investigations/HIDDEN_SKILL_ENTRY/PLAN.md).
    //
    // Prior probe (R13WRITEWATCH-GATE3, this mod's R13FetchAndWriteWatchTrace)
    // CONFIRMED runtime this session: unit=60(Frost) essentially never
    // reaches the gate3 ("already owned?") check at all (1/333 combined
    // hits, a known seq21->22 boundary noise frame), while unit=59(High
    // Pixie) reaches it 332 times and ALWAYS passes (gate3Al=-1,
    // appended=True every single time, for exactly two stable candidate
    // slots: ebx=10/skillId=353 and ebx=12/skillId=72). This rules gate3
    // OUT as Frost's blocker - Frost's candidates are failing upstream, at
    // gate1(tag) or gate2(level threshold), before ever reaching the
    // ownership check.
    //
    // This class narrows that further with a SECOND fixed execute point
    // placed right after gate1(tag) has already passed (if tag failed, this
    // address is never reached for that ebx - control jumps straight to the
    // loop increment instead):
    //   GateCheckVa (existing)   = 0x1822805CC "test al,al"      (gate3 outcome)
    //   Gate1PassVa (new)        = 0x1822805B4 "cmp ebp,ecx"     (gate1 passed,
    //                              about to evaluate gate2; entry pointer is
    //                              in RDX at this instant, so the entry's raw
    //                              tag/level/skillId fields are read directly
    //                              from memory rather than trusted to survive
    //                              in a register)
    //   LoopReadVa (kept)        = 0x1822DA05E "test r13,r13"    (final
    //                              loopBound, same as CmpDrawSkillR13LoopBoundTrace/
    //                              R13FetchAndWriteWatchTrace, kept here purely
    //                              for cross-checking in the same run)
    //
    // Classification per ebx (0..23), derivable offline from these two
    // event streams plus the fixed 24-iteration loop bound:
    //   - ebx never appears in EITHER stream -> gate1(tag) failed.
    //   - ebx appears in Gate1Pass but never in GateCheck -> gate1 passed,
    //     gate2(level) failed.
    //   - ebx appears in both -> gate1+gate2 passed, gate3 outcome is
    //     GateCheck's appended field (already known to be True whenever it
    //     fires, per the prior probe's finding above).
    //
    // No dynamic DR reprogramming needed this time (all three points are
    // fixed addresses known ahead of time) - simpler VEH than
    // R13FetchAndWriteWatchTrace's DR0/DR1 fetch+write-watch pair, which
    // this class does not reuse (that question - "who resets/increments the
    // counter" - is already answered).
    //
    // Read-only from the game's perspective: no GameAssembly.dll bytes are
    // written, only CPU debug registers (Dr0-Dr3/Dr7) on this thread's
    // CONTEXT, exactly like every other hardware-breakpoint probe already in
    // this mod.
    internal static class CurriculumGateChainTrace
    {
        internal static readonly bool Enabled = true;

        private const long GameAssemblyPreferredBase = 0x180000000L;

        private const long LoopReadVa = 0x1822DA05EL; // cmpDrawSkill: "test r13,r13"
        private static readonly byte[] LoopReadBytes = { 0x4D, 0x85, 0xED };

        private const long Gate1PassVa = 0x1822805B4L; // rstCreateBeforeSkillList: "cmp ebp,ecx" (post gate1/tag)
        private static readonly byte[] Gate1PassBytes = { 0x3B, 0xE9 };

        private const long GateCheckVa = 0x1822805CCL; // rstCreateBeforeSkillList: "test al,al" (post gate3 call)
        private static readonly byte[] GateCheckBytes = { 0x84, 0xC0 };

        // User hypothesis (2026-09-15, cross-checked against this project's
        // own cpp2il dump, .analysis/cpp2il_cs/DiffableCs/Assembly-CSharp/
        // newdata_H/datUnitWork_s.cs): if r15 in rstCreateBeforeSkillList
        // really is pStock/datUnitWork_s*, then [r15+0x88] is not an
        // arbitrary flag - it is the named field `hensinmae` (ushort,
        // "pre-transformation [species]"), and [r15+0x24]/[r15+0x14] (both
        // already read by this function, confirmed by static disassembly
        // this session) line up exactly with datUnitWork_s.level(0x24,
        // ushort)/id(0x14, ushort). This probe captures all three PLUS
        // skillcnt(0x48, int) - a field already independently CONFIRMED in
        // this project (01_CURRENT_STATE.md, PowerUp candidate-scan
        // investigation) to belong to pStock - in one shot, at the earliest
        // point in the function where r15 is valid (right after `mov
        // r15,rdx`), so a mismatch with the known-good skillcnt reading
        // would immediately disprove "r15==pStock" rather than requiring a
        // separate check.
        private const long StockFieldProbeVa = 0x182280480L; // rstCreateBeforeSkillList: "movsx r14,cl" (right after r15 is set)
        private static readonly byte[] StockFieldProbeBytes = { 0x4C, 0x0F, 0xBE, 0xF1 };

        private const int MaxArmedFrames = 36000; // ~10 minutes at 60fps
        private static int _armedAtFrame = -1;

        private enum HitKind { LoopRead = 0, Gate1Pass = 1, GateCheck = 2, StockFields = 3 }

        private struct PendingHit
        {
            internal int Frame;
            internal int Seq;
            internal bool BridgeActive;
            internal int Unit;
            internal int Target;
            internal HitKind Kind;
            internal int Ebx;
            internal int SkillId;
            internal int Tag;         // Gate1Pass only: byte[entry+0x11]
            internal int LevelThresh; // Gate1Pass only: sbyte[entry+0x10]
            internal int LevelBase;   // Gate1Pass only: ecx at that instant ([r15+0x24]+levelParam)
            internal int Gate3Al;     // GateCheck only
            internal long R13;        // LoopRead only
            internal int LoopBound;   // LoopRead only
            internal long ByteArrayPtr;  // LoopRead only: [r13+0x18] (outList's byte[] - levelThresh/type per candidate; CONFIRMED this session to be read NOWHERE in cmpDrawSkill's full body)
            internal int ByteArrayLen;   // LoopRead only: IL2CPP array Length header, [ByteArrayPtr+0x18]
            internal long WordArrayPtr;  // LoopRead only: [r13+0x20] (outList's skillId array - the only outList sub-array cmpDrawSkill actually reads)
            internal int WordArrayLen;   // LoopRead only: IL2CPP array Length header, [WordArrayPtr+0x18]
            internal long R15;        // StockFields only: the raw pStock pointer itself
            internal int StockId;     // StockFields only: [r15+0x14] (candidate: datUnitWork_s.id)
            internal int StockLevel;  // StockFields only: [r15+0x24] (candidate: datUnitWork_s.level)
            internal int StockSkillCnt; // StockFields only: [r15+0x48] (candidate: datUnitWork_s.skillcnt, independently CONFIRMED elsewhere in this project)
            internal int StockHensinmae; // StockFields only: [r15+0x88] (candidate: datUnitWork_s.hensinmae)
        }

        private const int MaxHits = 96;
        private static readonly PendingHit[] _pending = new PendingHit[MaxHits];
        private static int _pendingCount;
        private static long _totalLoopReadHits, _totalGate1PassHits, _totalGateCheckHits, _totalStockFieldsHits;

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
        private const int OffsetDr3 = 0x60;
        private const int OffsetDr6 = 0x68;
        private const int OffsetDr7 = 0x70;
        private const int OffsetRax = 0x78;
        private const int OffsetRcx = 0x80;
        private const int OffsetRdx = 0x88;
        private const int OffsetRbx = 0x90;
        private const int OffsetR13 = 0xE0;
        private const int OffsetR14 = 0xE8;
        private const int OffsetR15 = 0xF0;
        private const int OffsetRip = 0xF8;
        private const int ResumeFlagBit = 0x10000;

        // L0,L1,L2,L3 enabled (bits 0,2,4,6), all execute-shaped (RW/LEN
        // nibbles stay 0 for all four).
        private const long Dr7LocalEnableMask = 0x1L | 0x4L | 0x10L | 0x40L;
        private const long Dr7RwLenAllUsedMask = 0xFFFFL << 16; // clears RW/LEN nibbles for DR0,DR1,DR2,DR3 (bits 16-31)

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private static long _actualModuleBase;
        private static IntPtr _addrLoopRead = IntPtr.Zero;
        private static IntPtr _addrGate1Pass = IntPtr.Zero;
        private static IntPtr _addrGateCheck = IntPtr.Zero;
        private static IntPtr _addrStockFieldProbe = IntPtr.Zero;
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
                        "[NocturneModernGameplay] CURRICULUMGATE-AUTOUNINSTALL; " +
                        $"loopReadHits={_totalLoopReadHits}; gate1PassHits={_totalGate1PassHits}; " +
                        $"gateCheckHits={_totalGateCheckHits}; stockFieldsHits={_totalStockFieldsHits}; armedFrames={elapsed}.");
                    Uninstall();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] CurriculumGateChainTrace.Tick failed safely: {ex.Message}");
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

                _addrLoopRead = ResolveAndVerify(moduleBase, LoopReadVa, LoopReadBytes, "loop-read-point");
                _addrGate1Pass = ResolveAndVerify(moduleBase, Gate1PassVa, Gate1PassBytes, "gate1-pass-point");
                _addrGateCheck = ResolveAndVerify(moduleBase, GateCheckVa, GateCheckBytes, "gate3-check-point");
                _addrStockFieldProbe = ResolveAndVerify(moduleBase, StockFieldProbeVa, StockFieldProbeBytes, "stock-field-probe-point");

                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointsOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] CURRICULUMGATE-INSTALLED; " +
                    $"loopRead=0x{_addrLoopRead.ToInt64():X}; gate1Pass=0x{_addrGate1Pass.ToInt64():X}; " +
                    $"gateCheck=0x{_addrGateCheck.ToInt64():X}; stockFieldProbe=0x{_addrStockFieldProbe.ToInt64():X}; " +
                    "mechanism=hardware-execute-x4(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    $"[NocturneModernGameplay] CurriculumGateChainTrace install refused safely: {ex}");
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
                if (_addrLoopRead != IntPtr.Zero || _addrGate1Pass != IntPtr.Zero || _addrGateCheck != IntPtr.Zero || _addrStockFieldProbe != IntPtr.Zero)
                {
                    try { RemoveHardwareBreakpointsOnCurrentThread(); }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning(
                            $"[NocturneModernGameplay] CurriculumGateChainTrace breakpoint removal failed: {ex.Message}");
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
                _addrLoopRead = IntPtr.Zero;
                _addrGate1Pass = IntPtr.Zero;
                _addrGateCheck = IntPtr.Zero;
                _addrStockFieldProbe = IntPtr.Zero;
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

                Marshal.WriteInt64(ctx, OffsetDr0, _addrLoopRead.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr1, _addrGate1Pass.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr2, _addrGateCheck.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr3, _addrStockFieldProbe.ToInt64());

                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                dr7 &= ~Dr7RwLenAllUsedMask;                  // DR0/DR1/DR2 RW/LEN nibbles -> 0 (execute-shaped)
                dr7 = (dr7 & ~(0x1L | 0x4L | 0x10L)) | Dr7LocalEnableMask;
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
                if ((dr7Rb & Dr7LocalEnableMask) != Dr7LocalEnableMask)
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
                dr7 &= ~(0x1L | 0x4L | 0x10L | 0x40L);
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

        // Runs inside #DB exception dispatch. All three points are fixed
        // addresses (no dynamic reprogramming this time), so a plain
        // exceptionAddress compare identifies each one. A couple of plain
        // register/memory reads - no IL2CPP access, no allocation.
        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                IntPtr exceptionAddress = Marshal.ReadIntPtr(exceptionRecordPtr, 0x10);

                if (exceptionAddress == _addrLoopRead)
                {
                    _totalLoopReadHits++;
                    long r13 = Marshal.ReadInt64(contextRecordPtr, OffsetR13);
                    int byteNow = -1;
                    long byteArrayPtr = 0, wordArrayPtr = 0;
                    int byteArrayLen = -1, wordArrayLen = -1;
                    if (r13 != 0)
                    {
                        try { byteNow = Marshal.ReadByte(new IntPtr(r13 + 0x10)); } catch { byteNow = -1; }
                        try { byteArrayPtr = Marshal.ReadInt64(new IntPtr(r13 + 0x18)); } catch { }
                        try { wordArrayPtr = Marshal.ReadInt64(new IntPtr(r13 + 0x20)); } catch { }
                        if (byteArrayPtr != 0)
                        {
                            try { byteArrayLen = Marshal.ReadInt32(new IntPtr(byteArrayPtr + 0x18)); } catch { }
                        }
                        if (wordArrayPtr != 0)
                        {
                            try { wordArrayLen = Marshal.ReadInt32(new IntPtr(wordArrayPtr + 0x18)); } catch { }
                        }
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
                        slot.R13 = r13;
                        slot.LoopBound = byteNow;
                        slot.ByteArrayPtr = byteArrayPtr;
                        slot.ByteArrayLen = byteArrayLen;
                        slot.WordArrayPtr = wordArrayPtr;
                        slot.WordArrayLen = wordArrayLen;
                        _pendingCount++;
                    }
                }
                else if (exceptionAddress == _addrGate1Pass)
                {
                    _totalGate1PassHits++;
                    long rbx = Marshal.ReadInt64(contextRecordPtr, OffsetRbx);
                    long rdx = Marshal.ReadInt64(contextRecordPtr, OffsetRdx); // curriculum entry pointer
                    long rcx = Marshal.ReadInt64(contextRecordPtr, OffsetRcx); // level baseline ([r15+0x24]+levelParam)

                    int tag = -1, levelThresh = -999, skillId = -1;
                    if (rdx != 0)
                    {
                        try { tag = Marshal.ReadByte(new IntPtr(rdx + 0x11)); } catch { }
                        try { levelThresh = unchecked((sbyte)Marshal.ReadByte(new IntPtr(rdx + 0x10))); } catch { }
                        try { skillId = Marshal.ReadInt16(new IntPtr(rdx + 0x12)); } catch { }
                    }

                    if (_pendingCount < MaxHits)
                    {
                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Frame = _cachedFrame;
                        slot.Seq = _cachedSeq;
                        slot.BridgeActive = _cachedBridgeActive;
                        slot.Unit = _cachedUnit;
                        slot.Target = _cachedTarget;
                        slot.Kind = HitKind.Gate1Pass;
                        slot.Ebx = unchecked((int)(rbx & 0xFFFFFFFF));
                        slot.SkillId = skillId;
                        slot.Tag = tag;
                        slot.LevelThresh = levelThresh;
                        slot.LevelBase = unchecked((int)(rcx & 0xFFFFFFFF));
                        _pendingCount++;
                    }
                }
                else if (exceptionAddress == _addrGateCheck)
                {
                    _totalGateCheckHits++;
                    long rax = Marshal.ReadInt64(contextRecordPtr, OffsetRax);
                    long rbx = Marshal.ReadInt64(contextRecordPtr, OffsetRbx);
                    long r14 = Marshal.ReadInt64(contextRecordPtr, OffsetR14);
                    sbyte al = unchecked((sbyte)(rax & 0xFF));

                    if (_pendingCount < MaxHits)
                    {
                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Frame = _cachedFrame;
                        slot.Seq = _cachedSeq;
                        slot.BridgeActive = _cachedBridgeActive;
                        slot.Unit = _cachedUnit;
                        slot.Target = _cachedTarget;
                        slot.Kind = HitKind.GateCheck;
                        slot.Ebx = unchecked((int)(rbx & 0xFFFFFFFF));
                        slot.SkillId = unchecked((int)(r14 & 0xFFFF));
                        slot.Gate3Al = al;
                        _pendingCount++;
                    }
                }
                else if (exceptionAddress == _addrStockFieldProbe)
                {
                    _totalStockFieldsHits++;
                    long r15 = Marshal.ReadInt64(contextRecordPtr, OffsetR15);
                    int stockId = -1, stockLevel = -1, stockSkillCnt = -1, stockHensinmae = -1;
                    if (r15 != 0)
                    {
                        try { stockId = Marshal.ReadInt16(new IntPtr(r15 + 0x14)); } catch { }
                        try { stockLevel = Marshal.ReadInt16(new IntPtr(r15 + 0x24)); } catch { }
                        try { stockSkillCnt = Marshal.ReadInt32(new IntPtr(r15 + 0x48)); } catch { }
                        try { stockHensinmae = Marshal.ReadInt16(new IntPtr(r15 + 0x88)); } catch { }
                    }

                    if (_pendingCount < MaxHits)
                    {
                        ref PendingHit slot = ref _pending[_pendingCount];
                        slot.Frame = _cachedFrame;
                        slot.Seq = _cachedSeq;
                        slot.BridgeActive = _cachedBridgeActive;
                        slot.Unit = _cachedUnit;
                        slot.Target = _cachedTarget;
                        slot.Kind = HitKind.StockFields;
                        slot.R15 = r15;
                        slot.StockId = stockId & 0xFFFF;
                        slot.StockLevel = stockLevel & 0xFFFF;
                        slot.StockSkillCnt = stockSkillCnt;
                        slot.StockHensinmae = stockHensinmae & 0xFFFF;
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
        // never from the VEH.
        internal static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            for (int i = 0; i < count; i++)
            {
                ref PendingHit hit = ref _pending[i];
                switch (hit.Kind)
                {
                    case HitKind.LoopRead:
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] CURRICULUMGATE-LOOPREAD; " +
                            $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                            $"target={hit.Target}; r13=0x{hit.R13:X}; loopBound={hit.LoopBound}; " +
                            $"byteArrayPtr=0x{hit.ByteArrayPtr:X}; byteArrayLen={hit.ByteArrayLen}; " +
                            $"wordArrayPtr=0x{hit.WordArrayPtr:X}; wordArrayLen={hit.WordArrayLen}.");
                        break;
                    case HitKind.Gate1Pass:
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] CURRICULUMGATE-GATE1PASS; " +
                            $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                            $"target={hit.Target}; ebx={hit.Ebx}; skillId={hit.SkillId}; tag={hit.Tag}; " +
                            $"levelThresh={hit.LevelThresh}; levelBase={hit.LevelBase}.");
                        break;
                    case HitKind.GateCheck:
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] CURRICULUMGATE-GATE3; " +
                            $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                            $"target={hit.Target}; ebx={hit.Ebx}; skillId={hit.SkillId}; gate3Al={hit.Gate3Al}; " +
                            $"appended={(hit.Gate3Al < 0)}.");
                        break;
                    case HitKind.StockFields:
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] CURRICULUMGATE-STOCKFIELDS; " +
                            $"frame={hit.Frame}; seq={hit.Seq}; bridgeActive={hit.BridgeActive}; unit={hit.Unit}; " +
                            $"target={hit.Target}; r15=0x{hit.R15:X}; stockId={hit.StockId}; stockLevel={hit.StockLevel}; " +
                            $"stockSkillCnt={hit.StockSkillCnt}; stockHensinmae={hit.StockHensinmae}.");
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
