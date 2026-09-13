using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - DIRECT OWNERSHIP WRITER CAPTURE,
    // full-capacity forget+replace episode edition.
    //
    // Revision history: the prior revision of this file armed/disarmed
    // Dr0/Dr1 per-invocation around rstupdate.rstUpdateSeqDefaultSkill
    // alone, and CONFIRMED (4/4 empty-slot cases) the exact writer
    // instructions for the simple "skillcnt N->N+1" case:
    //   VA 0x19675E084  mov dword ptr [rdx + rax*4 + 0x20], ebx   (skill[index] = pending)
    //   VA 0x19675E08C  inc dword ptr [r10 + 0x48]                 (skillcnt++)
    // with RBX==GBWK+0x32 pending skill, RAX/R8==old skillcnt (insertion
    // index), R10==pCurrentStock, all 4/4.
    //
    // This revision watches CONTINUOUSLY across the whole full-capacity
    // forget+replace episode (seq21 delete-selection -> seq22 confirm ->
    // native delete/compact -> eventual re-insertion), which prior runtime
    // evidence (ForgetFlowRuntimeTrace, DestroyConfirmImmediateTrace) shows
    // can span multiple native calls and possibly multiple frames - a
    // single Prefix/Postfix-scoped watch around one method call is not
    // enough to catch a delete in rstUpdateSeqDestroyConfirm and a later
    // re-insertion elsewhere. Patches rstupdate.rstUpdate (the per-frame
    // outer dispatcher) instead of one specific seq handler, and keeps Dr0
    // armed on the CURRENT pCurrentStock+0x48 continuously, re-arming only
    // when pCurrentStock itself changes (tracked every frame). Known risk,
    // accepted for this manually-driven single-unit test: if pCurrentStock
    // briefly points to a DIFFERENT unit mid-episode (e.g. while other
    // party members are processed in the same batch), the watch follows
    // the new pointer and could miss a write that lands back on the
    // original unit's memory through a still-cached native pointer
    // elsewhere. Not observed as a problem in prior single-unit forget-flow
    // telemetry, but noted for report-time interpretation if results look
    // incomplete.
    //
    // Dr1 is NOT used this round (per explicit instruction: the
    // full-capacity destination slot is not known in advance - the
    // DestroyConfirm delete/compact could rewrite several slots before an
    // eventual insertion lands anywhere in [0,8), so guessing one slot
    // address to watch would likely miss the event entirely). Dr0
    // (skillcnt) alone is sufficient to catch every skillcnt-changing
    // write in the episode; the writer instruction and its neighborhood
    // (read from the trap RIP's surrounding bytes) tells us what slot was
    // touched without needing a second breakpoint.
    //
    // Register/VEH safety discipline unchanged from the prior revision and
    // from PowerUpMutationBit6RawProbe: the VEH callback (VectoredHandler/
    // CaptureHit) does ONLY raw Marshal reads/writes and GetCurrentThreadId,
    // writing into a preallocated fixed-size struct array. No MelonLogger,
    // no UnityEngine, no IL2CPP object/property access, no string
    // formatting, no allocation, no lock, from inside VEH. All of that is
    // deferred to FlushPendingLogs, called from Postfix in ordinary managed
    // context. Additional per-hit context this round (GBWK+0x32 pending,
    // SeqInfo.Current/Last, Flag, live skill[0..7]) is captured via plain
    // Marshal reads against RAW POINTERS already cached in ordinary
    // (Prefix) context into plain static long fields - never via a
    // property getter, and never allocating/formatting inside the handler.
    //
    // Continuously armed (not disarmed every Postfix) for the duration of
    // this diagnostic session, so a multi-frame delete-then-reinsert
    // sequence is not missed between calls. Call Uninstall() (wired to
    // ModMain.OnDeinitializeMelon) to clear Dr0 at shutdown.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class SkillCntWriterCaptureProbe
    {
        // Core question answered (empty-slot AND full-capacity insertion
        // writer both CONFIRMED at instruction level - see class-level
        // comment history). Disabled for the Full-Capacity Bridge PoC work
        // so PowerUpMutationCfgDiagnostics can reclaim Dr0-Dr3.
        internal static readonly bool Enabled = false;

        private struct PendingHit
        {
            internal long Dr6;
            internal long Rip;
            internal long Rsp;
            internal long Rflags;
            internal long WatchedAddress;
            internal int ThreadId;
            internal long FrameId;
            internal long Rax, Rbx, Rcx, Rdx, Rsi, Rdi, Rbp;
            internal long R8, R9, R10, R11, R12, R13, R14, R15;

            // Extra context, read via raw pointers cached in ordinary
            // (Prefix) context - see _cachedGbwkPtr/_cachedPCurrentStock/
            // _cachedSkillArrayFieldPtr below.
            internal long SkillCntAtHit;
            internal ushort Pending32AtHit;
            internal byte SeqCurrentAtHit;
            internal byte SeqLastAtHit;
            internal sbyte FlagAtHit;
            internal bool SkillsReadOk;
            internal int Skill0, Skill1, Skill2, Skill3, Skill4, Skill5, Skill6, Skill7;
        }

        private const int PendingCapacity = 32;
        private static readonly PendingHit[] _pending = new PendingHit[PendingCapacity];
        private static int _pendingCount;
        private static int _captureErrorCount;

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232;
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr6 = 0x68;
        private const int OffsetDr7 = 0x70;
        private const int OffsetRax = 0x78;
        private const int OffsetRcx = 0x80;
        private const int OffsetRdx = 0x88;
        private const int OffsetRbx = 0x90;
        private const int OffsetRsp = 0x98;
        private const int OffsetRbp = 0xA0;
        private const int OffsetRsi = 0xA8;
        private const int OffsetRdi = 0xB0;
        private const int OffsetR8 = 0xB8;
        private const int OffsetR9 = 0xC0;
        private const int OffsetR10 = 0xC8;
        private const int OffsetR11 = 0xD0;
        private const int OffsetR12 = 0xD8;
        private const int OffsetR13 = 0xE0;
        private const int OffsetR14 = 0xE8;
        private const int OffsetR15 = 0xF0;
        private const int OffsetRip = 0xF8;
        private const int ResumeFlagBit = 0x10000;

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private const long Dr7WriteRw = 0x1;
        private const long Dr7Len4Bytes = 0x3;

        private static IntPtr _vehHandle = IntPtr.Zero;
        private static VectoredHandlerDelegate? _handlerDelegate;
        private static bool _installFailedPermanently;
        private static bool _vehRegistered;

        private static long _armedDr0Address;
        private static bool _armed;

        // Raw pointers/values cached every Prefix (ordinary managed
        // context) so the VEH callback can dereference them with plain
        // Marshal reads only - never re-fetched via a property getter from
        // inside the handler.
        private static long _cachedGbwkPtr;
        private static long _cachedPCurrentStock;
        private static long _cachedSkillArrayFieldPtr;
        private static long _frameCounter;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VectoredHandlerDelegate(IntPtr exceptionPointers);

        private static void Prefix()
        {
            if (!Enabled) return;
            try
            {
                if (_installFailedPermanently) return;

                if (PowerUpMutationCfgDiagnostics.Enabled)
                {
                    _installFailedPermanently = true;
                    if (_armed) DisarmWatchpoint();
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] SKILLCNT-WRITER-CAPTURE refused: " +
                        "PowerUpMutationCfgDiagnostics.Enabled is true and already owns Dr0-Dr3.");
                    return;
                }

                EnsureVehRegistered();

                _frameCounter++;

                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                long gbwkPtr = gbwk.Pointer.ToInt64();
                long pCurrentStock = Marshal.ReadInt64(gbwk.Pointer, 0x60);

                _cachedGbwkPtr = gbwkPtr;

                if (pCurrentStock == 0)
                {
                    return; // leave any existing watch armed as-is
                }

                if (pCurrentStock != _cachedPCurrentStock)
                {
                    _cachedPCurrentStock = pCurrentStock;
                    _cachedSkillArrayFieldPtr = Marshal.ReadInt64(new IntPtr(pCurrentStock), 0x50);
                    ArmWatchpoint(pCurrentStock + 0x48);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCntWriterCaptureProbe prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!Enabled || _installFailedPermanently) return;
            try
            {
                FlushPendingLogs();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCntWriterCaptureProbe postfix failed safely: {ex.Message}");
            }
        }

        // Wired from ModMain.OnDeinitializeMelon.
        internal static void Uninstall()
        {
            try
            {
                if (_armed) DisarmWatchpoint();
                if (_vehHandle != IntPtr.Zero)
                {
                    RemoveVectoredExceptionHandler(_vehHandle);
                    _vehHandle = IntPtr.Zero;
                }
                _vehRegistered = false;
                _handlerDelegate = null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] SkillCntWriterCaptureProbe uninstall failed: {ex.Message}");
            }
        }

        private static void EnsureVehRegistered()
        {
            if (_vehRegistered) return;
            _handlerDelegate = VectoredHandler;
            _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
            if (_vehHandle == IntPtr.Zero)
            {
                _installFailedPermanently = true;
                MelonLogger.Error("[NocturneModernGameplay] SKILLCNT-WRITER-CAPTURE: AddVectoredExceptionHandler failed.");
                return;
            }
            _vehRegistered = true;
            MelonLogger.Msg(
                "[NocturneModernGameplay] SKILLCNT-WRITER-CAPTURE VEH installed " +
                "(continuous write-breakpoint mode, full-capacity episode edition).");
        }

        private static void ArmWatchpoint(long dr0Address)
        {
            IntPtr ctx = AllocAlignedContext(out IntPtr rawAlloc);
            try
            {
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                IntPtr thread = GetCurrentThread();
                if (!GetThreadContext(thread, ctx))
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] SKILLCNT-WRITER-CAPTURE GetThreadContext (arm) failed; error={Marshal.GetLastWin32Error()}.");
                    return;
                }

                Marshal.WriteInt64(ctx, OffsetDr0, dr0Address);
                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                const long clearMask = 0x1L | (0xFL << 16);
                dr7 &= ~clearMask;
                dr7 |= 0x1L; // L0
                dr7 |= (Dr7WriteRw << 16) | (Dr7Len4Bytes << 18); // RW0/LEN0
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);

                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                if (!SetThreadContext(thread, ctx))
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] SKILLCNT-WRITER-CAPTURE SetThreadContext (arm) failed; error={Marshal.GetLastWin32Error()}.");
                    return;
                }

                _armedDr0Address = dr0Address;
                _armed = true;
            }
            finally
            {
                Marshal.FreeHGlobal(rawAlloc);
            }
        }

        private static void DisarmWatchpoint()
        {
            IntPtr ctx = AllocAlignedContext(out IntPtr rawAlloc);
            try
            {
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                IntPtr thread = GetCurrentThread();
                if (!GetThreadContext(thread, ctx)) return;
                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                dr7 &= ~0x1L;
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                SetThreadContext(thread, ctx);
            }
            finally
            {
                Marshal.FreeHGlobal(rawAlloc);
                _armed = false;
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

        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                int ctxFlags = Marshal.ReadInt32(contextRecordPtr, OffsetContextFlags);
                bool hasDebugRegisters =
                    (unchecked((uint)ctxFlags) & ContextDebugRegisters) == ContextDebugRegisters;
                if (!hasDebugRegisters) return ExceptionContinueSearch;

                long dr6 = Marshal.ReadInt64(contextRecordPtr, OffsetDr6);
                bool b0 = (dr6 & 0x1L) != 0;
                if (!b0) return ExceptionContinueSearch; // not our Dr0

                try { CaptureHit(contextRecordPtr, dr6); }
                catch { _captureErrorCount++; }

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

        private static void CaptureHit(IntPtr contextRecordPtr, long dr6)
        {
            if (_pendingCount >= PendingCapacity) return;

            ref PendingHit slot = ref _pending[_pendingCount];
            slot.Dr6 = dr6;
            slot.Rip = Marshal.ReadInt64(contextRecordPtr, OffsetRip);
            slot.Rsp = Marshal.ReadInt64(contextRecordPtr, OffsetRsp);
            slot.Rflags = Marshal.ReadInt32(contextRecordPtr, OffsetEFlags);
            slot.WatchedAddress = _armedDr0Address;
            slot.ThreadId = unchecked((int)GetCurrentThreadId());
            slot.FrameId = _frameCounter;
            slot.Rax = Marshal.ReadInt64(contextRecordPtr, OffsetRax);
            slot.Rbx = Marshal.ReadInt64(contextRecordPtr, OffsetRbx);
            slot.Rcx = Marshal.ReadInt64(contextRecordPtr, OffsetRcx);
            slot.Rdx = Marshal.ReadInt64(contextRecordPtr, OffsetRdx);
            slot.Rsi = Marshal.ReadInt64(contextRecordPtr, OffsetRsi);
            slot.Rdi = Marshal.ReadInt64(contextRecordPtr, OffsetRdi);
            slot.Rbp = Marshal.ReadInt64(contextRecordPtr, OffsetRbp);
            slot.R8 = Marshal.ReadInt64(contextRecordPtr, OffsetR8);
            slot.R9 = Marshal.ReadInt64(contextRecordPtr, OffsetR9);
            slot.R10 = Marshal.ReadInt64(contextRecordPtr, OffsetR10);
            slot.R11 = Marshal.ReadInt64(contextRecordPtr, OffsetR11);
            slot.R12 = Marshal.ReadInt64(contextRecordPtr, OffsetR12);
            slot.R13 = Marshal.ReadInt64(contextRecordPtr, OffsetR13);
            slot.R14 = Marshal.ReadInt64(contextRecordPtr, OffsetR14);
            slot.R15 = Marshal.ReadInt64(contextRecordPtr, OffsetR15);

            // Extra raw context - plain Marshal reads against pointers
            // already cached in ordinary (Prefix) context. No property
            // getters, no allocation, no formatting.
            try
            {
                if (_cachedPCurrentStock != 0)
                {
                    slot.SkillCntAtHit = Marshal.ReadInt32(new IntPtr(_cachedPCurrentStock), 0x48);
                }
                if (_cachedGbwkPtr != 0)
                {
                    slot.Pending32AtHit = unchecked((ushort)Marshal.ReadInt16(new IntPtr(_cachedGbwkPtr), 0x32));
                    slot.SeqCurrentAtHit = Marshal.ReadByte(new IntPtr(_cachedGbwkPtr), 0x11);
                    slot.SeqLastAtHit = Marshal.ReadByte(new IntPtr(_cachedGbwkPtr), 0x13);
                    slot.FlagAtHit = unchecked((sbyte)Marshal.ReadByte(new IntPtr(_cachedGbwkPtr), 0x7E));
                }
                if (_cachedSkillArrayFieldPtr != 0)
                {
                    IntPtr arr = new IntPtr(_cachedSkillArrayFieldPtr);
                    slot.Skill0 = Marshal.ReadInt32(arr, 0x20 + 0 * 4);
                    slot.Skill1 = Marshal.ReadInt32(arr, 0x20 + 1 * 4);
                    slot.Skill2 = Marshal.ReadInt32(arr, 0x20 + 2 * 4);
                    slot.Skill3 = Marshal.ReadInt32(arr, 0x20 + 3 * 4);
                    slot.Skill4 = Marshal.ReadInt32(arr, 0x20 + 4 * 4);
                    slot.Skill5 = Marshal.ReadInt32(arr, 0x20 + 5 * 4);
                    slot.Skill6 = Marshal.ReadInt32(arr, 0x20 + 6 * 4);
                    slot.Skill7 = Marshal.ReadInt32(arr, 0x20 + 7 * 4);
                    slot.SkillsReadOk = true;
                }
            }
            catch
            {
                // leave whichever fields were not yet set at their default
            }

            _pendingCount++;
        }

        private static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            for (int i = 0; i < count; i++) EmitLog(in _pending[i]);

            int errors = _captureErrorCount;
            if (errors > 0)
            {
                _captureErrorCount = 0;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SKILLCNT-WRITER-CAPTURE capture errors (safely dropped): {errors}.");
            }
        }

        private static void EmitLog(in PendingHit hit)
        {
            try
            {
                string ripLocation = DescribeAddress(hit.Rip);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLCNT-WRITER-HIT; " +
                    $"frame={hit.FrameId}; watchedAddress=0x{hit.WatchedAddress:X}; threadId={hit.ThreadId}; " +
                    $"dr6=0x{hit.Dr6:X}; trapRip=0x{hit.Rip:X}; trapRipLocation={ripLocation}; " +
                    $"rsp=0x{hit.Rsp:X}; rflags=0x{hit.Rflags:X}; " +
                    $"skillCntAtHit={hit.SkillCntAtHit}; pending32AtHit={hit.Pending32AtHit}; " +
                    $"seqCurrentAtHit={hit.SeqCurrentAtHit}; seqLastAtHit={hit.SeqLastAtHit}; flagAtHit={hit.FlagAtHit}; " +
                    $"rax=0x{hit.Rax:X}; rbx=0x{hit.Rbx:X}; rcx=0x{hit.Rcx:X}; rdx=0x{hit.Rdx:X}; " +
                    $"rsi=0x{hit.Rsi:X}; rdi=0x{hit.Rdi:X}; rbp=0x{hit.Rbp:X}; " +
                    $"r8=0x{hit.R8:X}; r9=0x{hit.R9:X}; r10=0x{hit.R10:X}; r11=0x{hit.R11:X}; " +
                    $"r12=0x{hit.R12:X}; r13=0x{hit.R13:X}; r14=0x{hit.R14:X}; r15=0x{hit.R15:X}.");

                if (hit.SkillsReadOk)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLCNT-WRITER-HIT-SKILLS; " +
                        $"frame={hit.FrameId}; " +
                        $"skills=[{hit.Skill0 & 0xFFFF},{hit.Skill1 & 0xFFFF},{hit.Skill2 & 0xFFFF},{hit.Skill3 & 0xFFFF}," +
                        $"{hit.Skill4 & 0xFFFF},{hit.Skill5 & 0xFFFF},{hit.Skill6 & 0xFFFF},{hit.Skill7 & 0xFFFF}].");
                }

                try
                {
                    const int before = 48;
                    const int after = 16;
                    IntPtr windowStart = new IntPtr(hit.Rip - before);
                    byte[] window = new byte[before + after];
                    Marshal.Copy(windowStart, window, 0, window.Length);
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLCNT-WRITER-HIT-BYTES; " +
                        $"frame={hit.FrameId}; windowStart=0x{windowStart.ToInt64():X}; " +
                        $"tripRipOffsetInWindow={before}; bytesHex={BytesToHex(window)}.");
                }
                catch (Exception exBytes)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] SKILLCNT-WRITER-HIT-BYTES read failed: {exBytes.Message}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] SKILLCNT-WRITER-HIT emit failed safely: {ex.Message}");
            }
        }

        private static string DescribeAddress(long addr)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                foreach (System.Diagnostics.ProcessModule m in proc.Modules)
                {
                    long baseAddr = m.BaseAddress.ToInt64();
                    long size = m.ModuleMemorySize;
                    if (addr >= baseAddr && addr < baseAddr + size)
                    {
                        long rva = addr - baseAddr;
                        return $"{m.ModuleName}+0x{rva:X}";
                    }
                }
            }
            catch
            {
                // fall through
            }
            return "UNKNOWN-MODULE(heap/JIT-allocated or unmapped)";
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandlerDelegate handler);

        [DllImport("kernel32.dll")]
        private static extern uint RemoveVectoredExceptionHandler(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadContext(IntPtr hThread, IntPtr lpContext);
    }
}
