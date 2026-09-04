using System;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Read-only raw instrumentation inside rstCalcSkillPowerUpCore's
    // promotion-check / bit6-test / 16-scan / Mutation-merge region, added
    // to resolve two successive UNRESOLVED items from this investigation:
    //   1. (prior session) High Pixie frame=4911/17032: bit6=True observed
    //      at the Prefix/Postfix method boundary yet coreResult=2
    //      (Mutation) anyway.
    //   2. (this session) a fresh from-disk disassembly of the whole
    //      function (capstone, zero-base, cross-checked against the live
    //      process's own byte read) CONFIRMED there is no CFG edge that
    //      bypasses the bit6 test after promotion-check - yet real-machine
    //      telemetry showed the point-B breakpoint below firing 3/3 times
    //      for coreResult in {0,1} and 0/5 times for coreResult=2, despite
    //      promotion-check (V3-CFG-SKILLOWNER) firing 8/8 times. Since the
    //      CFG itself is confirmed unambiguous, this pointed at the
    //      instrumentation's own reliability rather than a missed code
    //      path - hence four simultaneous basic-block "did we pass through
    //      here" probes (A/B/C/D below), not a search for a new bypass
    //      edge.
    //
    // Four points, all inside the single straight-line region this
    // session's disassembly confirmed has no internal branches other than
    // the ones already documented (VAs and encodings below are exactly
    // what that disassembly read from disk, cross-checked byte-for-byte at
    // point B against the live process during the prior real-machine
    // test):
    //   A 0x18227E3D7  "test al, al"                     (84 C0)
    //     - immediately after the cmbChkSkillOwner call (promotion-check
    //       result in AL). Confirms the call actually returned here.
    //   B 0x18227E40C  "test byte ptr [rax+0x10], 0x40"  (F6 40 10 40)
    //     - the bit6 test itself (pre-existing point from the prior
    //       revision of this file; its extra value-inspection fields -
    //       rax/flagRaw/bit6/matchesPrefixPtr/unit-based filtering - are
    //       unchanged).
    //   C 0x18227E440  "test byte ptr [rcx+0x12f], 2"    (F6 81 2F 01 00 00 02)
    //     - the top of the 16-element scan loop's first condition check.
    //   D 0x18227E4BC  "test dil, dil"                    (40 84 FF)
    //     - the Mutation-attempt merge block's entry instruction.
    // A/C/D are passage-only: per this session's instruction, no value at
    // these points is inspected or logged beyond the point id itself,
    // coreInvocationId, RIP (redundant with the point id, logged anyway as
    // requested), and the OS thread id - the last of which is the key
    // field for this round, since a per-point difference in which thread
    // captures a hit (or a hit's total absence) is exactly what
    // distinguishes "this thread's breakpoints are unreliable" from "this
    // invocation's downstream code ran on a different thread than the one
    // the breakpoints are armed on".
    //
    // Mechanism (unchanged from the prior revision, now applied to four
    // addresses at once): x86-64 exposes four independent execute
    // breakpoints via Dr0-Dr3/Dr7, observed via a Vectored Exception
    // Handler - NOT a byte patch or inline detour, and GameAssembly.dll is
    // never written to (VirtualProtect/Marshal.Copy-write are never called
    // against any of these four addresses; Marshal.Copy is used only to
    // READ each address once at install time for verification). The CPU
    // raises #DB (EXCEPTION_SINGLE_STEP) immediately BEFORE the flagged
    // instruction executes; the handler below identifies which of the four
    // addresses matched, records the minimum raw fields into a fixed
    // buffer, and returns control with the CONTEXT unmodified except for
    // EFLAGS.RF (Resume Flag, bit 16) - required so the CPU does not
    // immediately re-trap on the same instruction when resuming (an
    // execute breakpoint is a fault: RIP has not advanced past it yet).
    // RF is a CPU-internal debug-only flag, distinct from the ALU flags
    // (ZF/SF/PF/CF/OF) any of these four instructions themselves set and
    // that the following jcc actually branches on. No general-purpose
    // register, no ALU flag, and no return value is ever written. Any
    // exception that is not one of these four addresses' EXCEPTION_SINGLE_
    // STEP is passed through unmodified via EXCEPTION_CONTINUE_SEARCH.
    //
    // Exception-handler discipline (unchanged from the prior self-review):
    // the VEH callback (VectoredHandler/CaptureHit) does ONLY raw Marshal
    // reads of CONTEXT fields and process memory, a raw GetCurrentThreadId
    // call (a plain TEB read, no allocation/lock/interop), plus writes
    // into a small preallocated fixed-size struct array using plain
    // integer indexing. It never calls MelonLogger, never touches
    // UnityEngine (frameCount), never touches an IL2CPP-wrapped managed
    // object (rstinit.GBWK / rstcalc.EventStart / any .SeqInfo/
    // .pCurrentStock property - these marshal through the IL2CPP interop
    // layer, which is not something to re-enter from inside a hardware-
    // exception callback that just interrupted GameAssembly.dll's OWN
    // native code mid-function), never formats a string, and never takes a
    // lock. The two static-field reads it does perform
    // (V3CfgCoreBoundaryPatch.LastPrefixInvocationId /
    // LastPrefixPCurrentStockPtr) are plain CLR auto-property reads on this
    // mod's own C# static class - no interop, no allocation - and are
    // captured here (not deferred) specifically because they identify
    // WHICH Core invocation a hit belongs to, which would become wrong if
    // read later after a subsequent Core call's Prefix has already
    // advanced them. Every other point-B-only field
    // (frame/seqCurrent/eventStart/gbwkPCurrentStockPtr) is informational
    // and is read later, in ordinary managed context (FlushPendingLogs,
    // called from ModMain.OnUpdate - never from the VEH), where the actual
    // MelonLogger call also happens.
    //
    // Thread affinity (unchanged from the prior revision): the breakpoints
    // MUST be armed on the same OS thread that later executes
    // rstCalcSkillPowerUpCore, since Dr0-Dr7 are per-thread CPU state.
    // EnsureInstalledOnGameplayThread is called from ModMain.OnUpdate,
    // MelonLoader's per-frame Unity Update-loop callback - unambiguously
    // the same OS thread that drives Unity's simulation step. It installs
    // once (on the first OnUpdate) and is a no-op afterward. This round's
    // per-hit threadId field lets a per-point/per-invocation thread
    // mismatch (if any) be seen directly in the log instead of only
    // inferred from an absence.
    //
    // Dr0-Dr3/Dr7 at point D (revised this round): the prior revision read
    // these via GetThreadContext(GetCurrentThread(), ...) from inside the
    // VEH callback, and a matching call was added to
    // V3CfgCoreBoundaryPatch's Prefix/Postfix. Per Windows' documented
    // behavior, GetThreadContext does NOT return a valid context for the
    // calling thread - that data was unreliable and has been removed. The
    // corrected source, used only at point D, is the CONTEXT the VEH is
    // already handed (contextRecordPtr = EXCEPTION_POINTERS.ContextRecord)
    // - the processor context the OS itself constructed to service this
    // exact #DB exception, read with zero additional API calls. Its
    // ContextFlags is checked for CONTEXT_DEBUG_REGISTERS before the Dr
    // fields are trusted (see CaptureHit). The Prefix/Postfix Dr-state
    // logging has been removed entirely (no replacement) - this round's
    // comparison is Install()'s own readback (already CONFIRMED reliable
    // separately) versus this ContextRecord-sourced value at point D.
    internal static class PowerUpMutationBit6RawProbe
    {
        // Fixed-capacity, no-allocation buffer written only from inside the
        // VEH callback (CaptureHit) and drained only from ordinary managed
        // context (FlushPendingLogs). Both sides only ever run on the same
        // OS thread (the one the breakpoints are armed on) and never
        // concurrently with each other (the VEH callback always completes,
        // as part of #DB exception dispatch/resume, before native
        // execution continues and eventually returns control to that
        // thread's normal call stack, where FlushPendingLogs can then run)
        // - so no synchronization primitive is needed or used. Capacity
        // raised from the prior single-point revision's 16 to 64 now that
        // up to four points can each fire per Core invocation.
        private struct PendingHit
        {
            internal char Point; // 'A'/'B'/'C'/'D'
            internal long Rip;
            internal int ThreadId;
            internal long CoreInvocationId;

            // Point B only (unused/default for A/C/D) - unchanged from the
            // prior revision.
            internal long Rax;
            internal byte FlagRaw;
            internal bool FlagOk;
            internal bool Bit6;
            internal int Unit;
            internal IntPtr PrefixPtr;

            // Point D only (unused/default for A/B/C) - added this round to
            // directly test whether Dr0-Dr3/Dr7 have actually changed by
            // the time D's breakpoint fires, per this session's leading
            // hypothesis (Dr0-Dr2/Dr7 losing their enable state mid-
            // invocation while Dr3 survives).
            internal long DrDr0;
            internal long DrDr1;
            internal long DrDr2;
            internal long DrDr3;
            internal long DrDr7;
            internal bool DrReadOk;
        }

        private const int PendingCapacity = 64;
        private static readonly PendingHit[] _pending = new PendingHit[PendingCapacity];
        private static int _pendingCount;
        private static int _captureErrorCount;
        private static bool _installAttempted;

        private const long GameAssemblyPreferredBase = 0x180000000L;

        // Point VAs and their statically-confirmed encodings (this
        // session's fresh capstone disassembly of GameAssembly.dll read
        // directly from disk; point B's bytes were additionally cross-
        // checked against the live process during the prior real-machine
        // test).
        private const long TargetVaA = 0x18227E3D7L; // test al, al
        private const long TargetVaB = 0x18227E40CL; // test byte ptr [rax+0x10], 0x40
        private const long TargetVaC = 0x18227E440L; // test byte ptr [rcx+0x12f], 2
        private const long TargetVaD = 0x18227E4BCL; // test dil, dil

        private static readonly byte[] ExpectedBytesA = { 0x84, 0xC0 };
        private static readonly byte[] ExpectedBytesB = { 0xF6, 0x40, 0x10, 0x40 };
        private static readonly byte[] ExpectedBytesC = { 0xF6, 0x81, 0x2F, 0x01, 0x00, 0x00, 0x02 };
        private static readonly byte[] ExpectedBytesD = { 0x40, 0x84, 0xFF };

        private const uint ContextAmd64 = 0x00100000;
        private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
        private const int ContextBufferSize = 1232; // sizeof(CONTEXT) on x64
        private const int OffsetContextFlags = 0x30;
        private const int OffsetEFlags = 0x44;
        private const int OffsetDr0 = 0x48;
        private const int OffsetDr1 = 0x50;
        private const int OffsetDr2 = 0x58;
        private const int OffsetDr3 = 0x60;
        private const int OffsetDr7 = 0x70;
        private const int OffsetRax = 0x78;
        private const int OffsetRip = 0xF8;
        private const int ResumeFlagBit = 0x10000;

        // Dr7 bits: L0/L1/L2/L3 = bits 0/2/4/6 (mask 0x55). R/Wn + LENn for
        // n=0..3 occupy bits 16-31 (4 bits per breakpoint); all zero (0x0)
        // is required for execute breakpoints on every one of the four.
        private const long Dr7EnableMask = 0x55L;
        private const long Dr7RwLenMask = 0xFFFFL << 16;

        private const uint ExceptionSingleStep = 0x80000004;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        private static IntPtr _targetAddressA = IntPtr.Zero;
        private static IntPtr _targetAddressB = IntPtr.Zero;
        private static IntPtr _targetAddressC = IntPtr.Zero;
        private static IntPtr _targetAddressD = IntPtr.Zero;
        private static IntPtr _vehHandle = IntPtr.Zero;
        private static VectoredHandlerDelegate? _handlerDelegate;
        private static bool _installed;

        // Call once per game session from ordinary managed context on the
        // gameplay thread (ModMain.OnUpdate). Safe to call every frame -
        // installs on the first call only, no-ops afterward regardless of
        // success or failure (a failed install logs once via Install()'s
        // own catch block and does not retry, matching this mod's existing
        // "refuse safely, do not retry a failed native operation" pattern).
        internal static void EnsureInstalledOnGameplayThread()
        {
            if (_installAttempted) return;
            _installAttempted = true;
            Install();
        }

        private static void Install()
        {
            if (_installed) return;
            try
            {
                IntPtr moduleBase = GetModuleHandle("GameAssembly.dll");
                if (moduleBase == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"GameAssembly.dll module base unavailable; error={Marshal.GetLastWin32Error()}");

                _targetAddressA = ResolveAndVerify(moduleBase, TargetVaA, ExpectedBytesA, "point A");
                _targetAddressB = ResolveAndVerify(moduleBase, TargetVaB, ExpectedBytesB, "point B");
                _targetAddressC = ResolveAndVerify(moduleBase, TargetVaC, ExpectedBytesC, "point C");
                _targetAddressD = ResolveAndVerify(moduleBase, TargetVaD, ExpectedBytesD, "point D");

                _handlerDelegate = VectoredHandler;
                _vehHandle = AddVectoredExceptionHandler(1, _handlerDelegate);
                if (_vehHandle == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");

                InstallHardwareBreakpointsOnCurrentThread();

                _installed = true;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-CFG-BLOCKPASS-RAW installed; " +
                    $"pointA=0x{_targetAddressA.ToInt64():X} pointB=0x{_targetAddressB.ToInt64():X} " +
                    $"pointC=0x{_targetAddressC.ToInt64():X} pointD=0x{_targetAddressD.ToInt64():X} " +
                    "mechanism=hardware-breakpoint(no GameAssembly.dll bytes written).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[NocturneModernGameplay] V3-CFG-BLOCKPASS-RAW install refused safely: {ex}");
                Uninstall();
            }
        }

        // Read-only: resolves moduleBase+RVA and verifies the live bytes
        // match this session's statically-confirmed encoding before
        // anything is armed. Throws (aborting the WHOLE install, none of
        // the four points get armed) on any mismatch - fail-closed, same
        // discipline as the byte-patch verification sites elsewhere in
        // this mod.
        private static IntPtr ResolveAndVerify(IntPtr moduleBase, long targetVa, byte[] expected, string label)
        {
            long rva = targetVa - GameAssemblyPreferredBase;
            IntPtr address = new IntPtr(checked(moduleBase.ToInt64() + rva));

            if (VirtualQuery(address, out MemoryBasicInformation mbi,
                (UIntPtr)Marshal.SizeOf<MemoryBasicInformation>()) == UIntPtr.Zero ||
                mbi.State != 0x1000 || (mbi.Protect & 0x100) != 0 || (mbi.Protect & 0x01) != 0)
                throw new InvalidOperationException(
                    $"{label} 0x{address.ToInt64():X} is not readable committed executable memory");

            byte[] actual = new byte[expected.Length];
            Marshal.Copy(address, actual, 0, actual.Length);
            if (!BytesEqual(actual, expected))
                throw new InvalidOperationException(
                    $"{label} unexpected bytes; expected={FormatBytes(expected)} actual={FormatBytes(actual)} " +
                    "- refusing to install any breakpoint on unverified code");

            return address;
        }

        internal static void Uninstall()
        {
            try
            {
                if (_targetAddressA != IntPtr.Zero || _targetAddressB != IntPtr.Zero ||
                    _targetAddressC != IntPtr.Zero || _targetAddressD != IntPtr.Zero)
                {
                    try { RemoveHardwareBreakpointsOnCurrentThread(); }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning(
                            $"[NocturneModernGameplay] V3-CFG-BLOCKPASS-RAW breakpoint removal failed: {ex.Message}");
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
                _handlerDelegate = null;
                _targetAddressA = IntPtr.Zero;
                _targetAddressB = IntPtr.Zero;
                _targetAddressC = IntPtr.Zero;
                _targetAddressD = IntPtr.Zero;
            }
        }

        // Must run on the same OS thread the breakpoints should be armed
        // on - debug registers (Dr0-Dr7) are per-thread CPU state. Called
        // from Install(), which is called from
        // EnsureInstalledOnGameplayThread(), which ModMain.OnUpdate calls
        // on its first invocation - i.e. this always runs on the Unity
        // Update-loop thread. Uninstall() (from OnDeinitializeMelon, at
        // game/process shutdown) is best-effort by comparison - if that
        // callback ever ran on a different thread the clear would silently
        // no-op, but by then the process is terminating anyway.
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

                Marshal.WriteInt64(ctx, OffsetDr0, _targetAddressA.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr1, _targetAddressB.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr2, _targetAddressC.ToInt64());
                Marshal.WriteInt64(ctx, OffsetDr3, _targetAddressD.ToInt64());

                long dr7 = Marshal.ReadInt64(ctx, OffsetDr7);
                // L0-L3 (bits 0/2/4/6) + R/W0-3 and LEN0-3 (bits 16-31, all
                // zero required for execute breakpoints). Every other bit
                // (G0-3/LE/GE/reserved) is preserved as read.
                long mask = Dr7EnableMask | Dr7RwLenMask;
                dr7 = (dr7 & ~mask) | Dr7EnableMask;
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);

                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                if (!SetThreadContext(thread, ctx))
                    throw new InvalidOperationException(
                        $"SetThreadContext failed; error={Marshal.GetLastWin32Error()}");

                // Read-back verification, same discipline as the byte-patch
                // sites elsewhere in this mod: refuse to declare success
                // without confirming every write actually took effect.
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                if (!GetThreadContext(thread, ctx))
                    throw new InvalidOperationException(
                        $"post-install GetThreadContext readback failed; error={Marshal.GetLastWin32Error()}");

                long dr0Rb = Marshal.ReadInt64(ctx, OffsetDr0);
                long dr1Rb = Marshal.ReadInt64(ctx, OffsetDr1);
                long dr2Rb = Marshal.ReadInt64(ctx, OffsetDr2);
                long dr3Rb = Marshal.ReadInt64(ctx, OffsetDr3);
                long dr7Rb = Marshal.ReadInt64(ctx, OffsetDr7);
                bool ok =
                    dr0Rb == _targetAddressA.ToInt64() && (dr7Rb & 0x1L) != 0 &&
                    dr1Rb == _targetAddressB.ToInt64() && (dr7Rb & 0x4L) != 0 &&
                    dr2Rb == _targetAddressC.ToInt64() && (dr7Rb & 0x10L) != 0 &&
                    dr3Rb == _targetAddressD.ToInt64() && (dr7Rb & 0x40L) != 0;
                if (!ok)
                    throw new InvalidOperationException(
                        $"hardware breakpoint readback mismatch; dr0=0x{dr0Rb:X} dr1=0x{dr1Rb:X} " +
                        $"dr2=0x{dr2Rb:X} dr3=0x{dr3Rb:X} dr7=0x{dr7Rb:X}");
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
                dr7 &= ~Dr7EnableMask; // clear L0-L3 only; leave every other bit untouched
                Marshal.WriteInt64(ctx, OffsetDr7, dr7);
                Marshal.WriteInt32(ctx, OffsetContextFlags, unchecked((int)ContextDebugRegisters));
                SetThreadContext(thread, ctx);
            }
            finally
            {
                Marshal.FreeHGlobal(rawAlloc);
            }
        }

        // CONTEXT is DECLSPEC_ALIGN(16) on x64; allocate extra headroom and
        // align the usable pointer manually rather than relying on
        // AllocHGlobal's (unspecified) alignment guarantee.
        private static IntPtr AllocAlignedContext(out IntPtr rawAlloc)
        {
            rawAlloc = Marshal.AllocHGlobal(ContextBufferSize + 16);
            long aligned = (rawAlloc.ToInt64() + 15L) & ~15L;
            IntPtr ctx = new IntPtr(aligned);
            for (int i = 0; i < ContextBufferSize; i++) Marshal.WriteByte(ctx, i, 0);
            return ctx;
        }

        // Runs inside #DB exception dispatch, on the thread that just
        // interrupted GameAssembly.dll's own native code. Per the class-
        // level comment: only raw Marshal reads/writes, a raw
        // GetCurrentThreadId call, and plain integer arithmetic below - no
        // MelonLogger, no UnityEngine, no IL2CPP object access, no string
        // formatting, no lock, no growable/allocating collection.
        private static int VectoredHandler(IntPtr exceptionPointers)
        {
            try
            {
                IntPtr exceptionRecordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
                IntPtr contextRecordPtr = Marshal.ReadIntPtr(exceptionPointers, IntPtr.Size);

                uint exceptionCode = unchecked((uint)Marshal.ReadInt32(exceptionRecordPtr, 0));
                if (exceptionCode != ExceptionSingleStep) return ExceptionContinueSearch;

                IntPtr exceptionAddress = Marshal.ReadIntPtr(exceptionRecordPtr, 0x10);

                char point;
                if (exceptionAddress == _targetAddressA) point = 'A';
                else if (exceptionAddress == _targetAddressB) point = 'B';
                else if (exceptionAddress == _targetAddressC) point = 'C';
                else if (exceptionAddress == _targetAddressD) point = 'D';
                else return ExceptionContinueSearch;

                try { CaptureHit(contextRecordPtr, point); }
                catch
                {
                    // No logging here by design (see class-level comment) -
                    // just count it; FlushPendingLogs reports the count
                    // later from ordinary context. Must never throw past
                    // this point: the RF-set/resume below is mandatory.
                    _captureErrorCount++;
                }

                // Required to resume past a fault-class hardware breakpoint
                // without immediately re-trapping on the same instruction.
                // RF is not one of the ALU flags any of the four
                // instructions set/their callers branch on.
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

        // Raw-only capture (see class-level comment). Writes at most one
        // entry into the fixed _pending array per hit; silently drops (no
        // allocation, no growth) if the buffer is already full.
        //
        // Points A/C/D: passage-only, per this session's instruction - no
        // value inspection, no unit filtering (there is no cheap/reliable
        // unit-bearing register at A/C/D the way rax is at B, and the
        // stated purpose this round is passage confirmation, not semantic
        // filtering), every hit at these three points is recorded.
        //
        // Point B: unchanged from the prior revision - rax/flagRaw/bit6/
        // unit/prefixPtr are captured and the existing target-unit filter
        // (PowerUpMutationCfgDiagnostics.IsTargetUnit, read from the
        // object rax itself points to, +0x14) still applies, so B's log
        // volume/behavior is identical to before this extension.
        private static void CaptureHit(IntPtr contextRecordPtr, char point)
        {
            long rax = 0;
            byte flagRaw = 0;
            bool flagOk = false;
            bool bit6 = false;
            int unit = -1;
            IntPtr prefixPtr = IntPtr.Zero;

            if (point == 'B')
            {
                rax = Marshal.ReadInt64(contextRecordPtr, OffsetRax);
                IntPtr raxPtr = new IntPtr(rax);
                try { flagRaw = Marshal.ReadByte(raxPtr, 0x10); flagOk = true; }
                catch { /* flagOk stays false; recorded as-is below */ }
                bit6 = flagOk && (flagRaw & 0x40) != 0;

                // Unit id read directly from the object rax points to
                // (datUnitWork_s+0x14, CONFIRMED offset) rather than from
                // GBWK.pCurrentStock - object identity at this exact
                // instant is part of what this probe exists to check.
                try { unit = Marshal.ReadInt16(raxPtr, 0x14); } catch { }

                if (unit != -1 && !PowerUpMutationCfgDiagnostics.IsTargetUnit(unit)) return;

                prefixPtr = V3CfgCoreBoundaryPatch.LastPrefixPCurrentStockPtr;
            }

            long drDr0 = 0, drDr1 = 0, drDr2 = 0, drDr3 = 0, drDr7 = 0;
            bool drReadOk = false;
            if (point == 'D')
            {
                // This round's core diagnostic: read Dr0-Dr3/Dr7 exactly as
                // the OS captured them for THIS #DB exception, directly
                // from the CONTEXT the VEH was already handed
                // (contextRecordPtr = EXCEPTION_POINTERS.ContextRecord) -
                // no new API call. Per Windows' documented behavior,
                // GetThreadContext(GetCurrentThread(), ...) does NOT return
                // a valid context for the calling thread (this was used in
                // an earlier revision of this file and is now known to be
                // unreliable evidence; removed). ContextRecord, by
                // contrast, is the processor context the OS itself
                // constructed to service this exact debug exception, so
                // its ContextFlags is checked below for CONTEXT_DEBUG_
                // REGISTERS before trusting the Dr fields - if that bit is
                // absent, drReadOk is left false and the raw (possibly
                // meaningless) values are still recorded for visibility.
                int ctxFlags = Marshal.ReadInt32(contextRecordPtr, OffsetContextFlags);
                bool hasDebugRegisters =
                    (unchecked((uint)ctxFlags) & ContextDebugRegisters) == ContextDebugRegisters;
                drDr0 = Marshal.ReadInt64(contextRecordPtr, OffsetDr0);
                drDr1 = Marshal.ReadInt64(contextRecordPtr, OffsetDr1);
                drDr2 = Marshal.ReadInt64(contextRecordPtr, OffsetDr2);
                drDr3 = Marshal.ReadInt64(contextRecordPtr, OffsetDr3);
                drDr7 = Marshal.ReadInt64(contextRecordPtr, OffsetDr7);
                drReadOk = hasDebugRegisters;
            }

            if (_pendingCount >= PendingCapacity) return;

            long rip = Marshal.ReadInt64(contextRecordPtr, OffsetRip);
            uint threadId = GetCurrentThreadId();
            // Captured now (not deferred to flush time) because it
            // identifies WHICH Core invocation this hit belongs to; a
            // plain static-field read on this mod's own class (no
            // interop, no allocation) - see class-level comment.
            long coreInvocationId = V3CfgCoreBoundaryPatch.LastPrefixInvocationId;

            ref PendingHit slot = ref _pending[_pendingCount];
            slot.Point = point;
            slot.Rip = rip;
            slot.ThreadId = unchecked((int)threadId);
            slot.CoreInvocationId = coreInvocationId;
            slot.Rax = rax;
            slot.FlagRaw = flagRaw;
            slot.FlagOk = flagOk;
            slot.Bit6 = bit6;
            slot.Unit = unit;
            slot.PrefixPtr = prefixPtr;
            slot.DrDr0 = drDr0;
            slot.DrDr1 = drDr1;
            slot.DrDr2 = drDr2;
            slot.DrDr3 = drDr3;
            slot.DrDr7 = drDr7;
            slot.DrReadOk = drReadOk;
            _pendingCount++;
        }

        // Ordinary managed context only - called from ModMain.OnUpdate,
        // never from the VEH. Safe here to touch UnityEngine, IL2CPP-
        // wrapped GBWK objects, format strings, and call MelonLogger.
        internal static void FlushPendingLogs()
        {
            int count = _pendingCount;
            _pendingCount = 0;
            for (int i = 0; i < count; i++) EmitLog(in _pending[i]);

            int errors = _captureErrorCount;
            if (errors > 0)
            {
                _captureErrorCount = 0;
                MelonLogger.Warning(
                    "[NocturneModernGameplay] V3-CFG-BLOCKPASS-RAW capture errors " +
                    $"(raw-read failures inside the exception handler, safely dropped): {errors}.");
            }
        }

        private static void EmitLog(in PendingHit hit)
        {
            try
            {
                if (hit.Point == 'B')
                {
                    IntPtr raxPtr = new IntPtr(hit.Rax);

                    int frame = -1;
                    try { frame = UnityEngine.Time.frameCount; } catch { }

                    int seqCurrent = -1;
                    int eventStart = -1;
                    IntPtr gbwkPCurrentStockPtr = IntPtr.Zero;
                    try
                    {
                        eventStart = rstcalc.EventStart;
                        var gbwk = rstinit.GBWK;
                        if (gbwk != null && gbwk.Pointer != IntPtr.Zero)
                        {
                            seqCurrent = gbwk.SeqInfo.Current;
                            gbwkPCurrentStockPtr = gbwk.pCurrentStock?.Pointer ?? IntPtr.Zero;
                        }
                    }
                    catch { }

                    bool matchesPrefixPtr = raxPtr == hit.PrefixPtr;
                    string flagStatus = hit.FlagOk ? "ok" : "read-failed";

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-CFG-BIT6-RAW; " +
                        $"point=B threadId={hit.ThreadId} rip=0x{hit.Rip:X} " +
                        $"frame={frame} unit={hit.Unit} seqCurrent={seqCurrent} eventStart={eventStart} " +
                        $"coreInvocationId={hit.CoreInvocationId} rax=0x{hit.Rax:X} " +
                        $"gbwkPCurrentStockPtr=0x{gbwkPCurrentStockPtr.ToInt64():X} " +
                        $"flagRaw=0x{hit.FlagRaw:X2} bit6={hit.Bit6} flagStatus={flagStatus} " +
                        $"prefixInvocationPCurrentStockPtr=0x{hit.PrefixPtr.ToInt64():X} " +
                        $"matchesPrefixPtr={matchesPrefixPtr}.");
                }
                else if (hit.Point == 'D')
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-CFG-BLOCKPASS-RAW; " +
                        $"point=D coreInvocationId={hit.CoreInvocationId} " +
                        $"rip=0x{hit.Rip:X} threadId={hit.ThreadId} " +
                        $"drReadOk={hit.DrReadOk} dr0=0x{hit.DrDr0:X} dr1=0x{hit.DrDr1:X} " +
                        $"dr2=0x{hit.DrDr2:X} dr3=0x{hit.DrDr3:X} dr7=0x{hit.DrDr7:X}.");
                }
                else
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-CFG-BLOCKPASS-RAW; " +
                        $"point={hit.Point} coreInvocationId={hit.CoreInvocationId} " +
                        $"rip=0x{hit.Rip:X} threadId={hit.ThreadId}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] V3-CFG-BLOCKPASS-RAW emit failed safely: {ex.Message}");
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

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadContext(IntPtr hThread, IntPtr lpContext);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            internal IntPtr BaseAddress;
            internal IntPtr AllocationBase;
            internal uint AllocationProtect;
            internal ushort PartitionId;
            internal UIntPtr RegionSize;
            internal uint State;
            internal uint Protect;
            internal uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(
            IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);
    }
}
