using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - DEFAULTSKILL EXTERNAL ITERATOR STATE PROBE.
    // Read-only observer only. Never writes any field.
    //
    // Purpose: rstcalc.rstCalcEventInfo (VA 0x18227C330, exact name match
    // confirmed) compares "(counter + EventNums) vs EventOfs" using a byte
    // object reached through a static-field chain SEPARATE from GBWK
    // itself, and separately reads a "reference index" word field (+0x24)
    // through a SECOND, differently-indirected static chain. Neither
    // object has ever been read directly at runtime - only inferred from
    // static disassembly. This class resolves the ABSOLUTE addresses of
    // both static field slots (computed once, offline, from the exact
    // instruction bytes at VA 0x18227C52F and VA 0x18227C366 - see the
    // constant comments below for the derivation) relative to
    // GameAssembly.dll's ACTUAL runtime load base (via GetModuleHandle,
    // not the static/preferred image base used during offline analysis),
    // then follows the SAME pointer chain natively does, using only plain
    // Marshal reads.
    //
    // Counter chain (single indirection):
    //   slotCounterAddr -> [+0] = p1 -> [p1+0xb8] = counterObjectPtr
    //   -> [counterObjectPtr+0] = counter byte
    // Reference-table chain (double indirection):
    //   slotRefTableAddr -> [+0] = q1 -> [q1+0xb8] = obj1
    //   -> [obj1+0] = obj2 -> [obj2+0x60] = (should equal gbwk.Pointer -
    //      cross-checked below, NOT assumed) -> [obj2+0x68] = refTableObj
    //   -> [refTableObj+0x24] = reference index (word)
    //   Also reads [obj2+0x30] (the "table built for this reference index"
    //   byte the rebuild loop gates on).
    //
    // Hooked as an ADDITIONAL Prefix/Postfix pair on the SAME
    // rstcalc.rstCalcEventInfo method DefaultSkillIteratorTrace already
    // patches - Harmony supports multiple independent patches on one
    // method, so this adds no new hook target, just more read-only work
    // inside an already-instrumented, low-frequency call.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcEventInfo))]
    internal static class DefaultSkillExternalStateProbe
    {
        internal static readonly bool Enabled = true;

        // Derivation (offline, from GameAssembly.dll's own preferred image
        // base 0x180000000 - see .analysis/scratch_18227c330_full.txt):
        //   Counter chain instruction: VA 0x18227C52F
        //     "mov rax, qword ptr [rip + 0xbb50fa]" (7 bytes, next=0x18227C536)
        //     slot VA = 0x18227C536 + 0xBB50FA = 0x182E31630
        //   Reference-table chain instruction: VA 0x18227C366
        //     "mov rdx, qword ptr [rip + 0xbca14b]" (7 bytes, next=0x18227C36D)
        //     slot VA = 0x18227C36D + 0xBCA14B = 0x182E464B8
        // Both converted to RVA (relative to preferred base 0x180000000)
        // here, then added to the ACTUAL runtime module base at use time -
        // never hardcoded as an absolute runtime address, since
        // GameAssembly.dll may not load at its preferred base.
        private const long CounterSlotRva = 0x182E31630L - 0x180000000L;
        private const long RefTableSlotRva = 0x182E464B8L - 0x180000000L;

        private static IntPtr _moduleBase = IntPtr.Zero;
        private static bool _moduleBaseResolved;

        private static bool _captured;
        private static byte _counterBefore;
        private static bool _counterReadOkBefore;
        private static short _refIndexBefore;
        private static byte _refTableBuiltFlagBefore;
        private static long _obj2Before;
        private static long _gbwkPtrForCompareBefore;
        private static bool _refReadOkBefore;
        private static long _refTableObjBefore;
        private static long _counterObjPtrBefore;

        // STOCK FLAG 0x200 / REFINDEX ROLLBACK CORRELATION: stockPtr+0x10 is
        // a raw flags dword read directly via gbwk.pCurrentStock.Pointer (no
        // separate static-slot chain needed - the IL2CPP interop wrapper
        // already gives us this pointer). Bit 0x200 gates the CONDITIONAL
        // second write to refTableObj+0x24 found inside twin_commit_advance
        // (VA 0x196547EFE-0x196547F02: "movzx eax,[rbx+0x24]; mov
        // [rcx+0x24],ax" - an OVERWRITE from a different object, not an
        // increment - executed only when this bit is set). Read-only.
        private static uint _stockFlagsBefore;
        private static bool _stockFlagsReadOkBefore;

        // DEFAULTSKILL REFINDEX ROLLBACK INVESTIGATION: cross-call snapshot.
        // Populated by every successful Postfix, read by the NEXT Prefix -
        // this is the only way to see state changes that happen BETWEEN two
        // rstCalcEventInfo calls (i.e. NOT explainable by this function's
        // own body), such as the twin_rebuild_loop/twin_commit_advance
        // functions discovered via binary-wide counter-slot xref scanning
        // (VA 0x196535910 / 0x196547DD0) which are invoked through an
        // indirect/obfuscated dispatch we have not been able to trace
        // statically. Still read-only - never written.
        private static bool _lastExitCaptured;
        private static long _lastExitRefTableObj;
        private static short _lastExitRefIndex;
        private static long _lastExitCounterObjPtr;
        private static byte _lastExitCounter;
        private static byte _lastExitTableBuiltFlag;
        private static int _lastExitUnit;
        private static long _lastExitStockPtr;
        private static sbyte _lastExitEventNums;
        private static sbyte _lastExitEventOfs;
        private static int _lastExitFrame;
        private static uint _lastExitStockFlags;
        private static bool _lastExitStockFlagsReadOk;
        private static int _lastExitSeq;

        private static IntPtr ResolveModuleBase()
        {
            if (_moduleBaseResolved) return _moduleBase;
            _moduleBaseResolved = true;
            try
            {
                _moduleBase = GetModuleHandle("GameAssembly.dll");
            }
            catch
            {
                _moduleBase = IntPtr.Zero;
            }
            return _moduleBase;
        }

        private static bool TryReadCounter(IntPtr moduleBase, out byte counter, out long counterObjPtr)
        {
            counter = 0;
            counterObjPtr = 0;
            try
            {
                long slotAddr = moduleBase.ToInt64() + CounterSlotRva;
                long p1 = Marshal.ReadInt64(new IntPtr(slotAddr));
                if (p1 == 0) return false;
                long objPtr = Marshal.ReadInt64(new IntPtr(p1 + 0xb8));
                if (objPtr == 0) return false;
                counterObjPtr = objPtr;
                counter = Marshal.ReadByte(new IntPtr(objPtr), 0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadRefTable(
            IntPtr moduleBase, out short refIndex, out byte tableBuiltFlag, out long obj2,
            out long refTableObjOut)
        {
            refIndex = 0;
            tableBuiltFlag = 0;
            obj2 = 0;
            refTableObjOut = 0;
            try
            {
                long slotAddr = moduleBase.ToInt64() + RefTableSlotRva;
                long q1 = Marshal.ReadInt64(new IntPtr(slotAddr));
                if (q1 == 0) return false;
                long obj1 = Marshal.ReadInt64(new IntPtr(q1 + 0xb8));
                if (obj1 == 0) return false;
                long obj2Local = Marshal.ReadInt64(new IntPtr(obj1), 0);
                if (obj2Local == 0) return false;
                obj2 = obj2Local;
                tableBuiltFlag = Marshal.ReadByte(new IntPtr(obj2Local), 0x30);
                long refTableObj = Marshal.ReadInt64(new IntPtr(obj2Local + 0x68));
                if (refTableObj == 0) return false;
                refTableObjOut = refTableObj;
                refIndex = Marshal.ReadInt16(new IntPtr(refTableObj), 0x24);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadStockFlags(IntPtr stockPtr, out uint flags)
        {
            flags = 0;
            if (stockPtr == IntPtr.Zero) return false;
            try
            {
                flags = unchecked((uint)Marshal.ReadInt32(stockPtr, 0x10));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Prefix()
        {
            _captured = false;
            if (!Enabled) return;
            try
            {
                var moduleBase = ResolveModuleBase();
                if (moduleBase == IntPtr.Zero) return;

                var gbwk = rstinit.GBWK;
                _gbwkPtrForCompareBefore = gbwk?.Pointer.ToInt64() ?? 0;

                _counterReadOkBefore = TryReadCounter(moduleBase, out _counterBefore, out _counterObjPtrBefore);
                _refReadOkBefore = TryReadRefTable(
                    moduleBase, out _refIndexBefore, out _refTableBuiltFlagBefore, out _obj2Before,
                    out _refTableObjBefore);

                var stockNowForFlags = gbwk?.pCurrentStock;
                _stockFlagsReadOkBefore = TryReadStockFlags(
                    stockNowForFlags?.Pointer ?? IntPtr.Zero, out _stockFlagsBefore);

                _captured = true;

                // DEFAULTSKILL REFINDEX ROLLBACK INVESTIGATION: cross-call
                // diff. Compare THIS call's entry snapshot against the
                // PREVIOUS call's exit snapshot (captured in Postfix last
                // time). Any difference here happened OUTSIDE this
                // function's own body - i.e. between two rstCalcEventInfo
                // invocations - which is exactly where twin_rebuild_loop /
                // twin_commit_advance (VA 0x196535910 / 0x196547DD0) would
                // have to fire, since neither is called from within
                // rstCalcEventInfo itself (confirmed via full disassembly).
                if (_lastExitCaptured && _refReadOkBefore && _counterReadOkBefore)
                {
                    bool refObjSame = _refTableObjBefore == _lastExitRefTableObj;
                    bool counterObjSame = _counterObjPtrBefore == _lastExitCounterObjPtr;
                    bool bit200Before = _lastExitStockFlagsReadOk && (_lastExitStockFlags & 0x200) != 0;
                    bool bit200After = _stockFlagsReadOkBefore && (_stockFlagsBefore & 0x200) != 0;
                    bool anyChange =
                        !refObjSame || !counterObjSame ||
                        _refIndexBefore != _lastExitRefIndex ||
                        _counterBefore != _lastExitCounter ||
                        _refTableBuiltFlagBefore != _lastExitTableBuiltFlag ||
                        bit200Before != bit200After;

                    if (anyChange)
                    {
                        var stock = gbwk?.pCurrentStock;
                        int unitNow = stock?.id ?? -1;
                        long stockPtrNow = stock?.Pointer.ToInt64() ?? 0;
                        sbyte eventNumsNow = gbwk?.EventNums ?? 0;
                        sbyte eventOfsNow = gbwk?.EventOfs ?? 0;
                        int seqNow = gbwk?.SeqInfo.Current ?? -1;
                        int frameNow = UnityEngine.Time.frameCount;

                        bool rollbackMatchesBit200Theory =
                            _refIndexBefore < _lastExitRefIndex && bit200Before;

                        MelonLogger.Msg(
                            "[NocturneModernGameplay] DEFAULTSKILL-CROSSCALL-DIFF; " +
                            $"unit={_lastExitUnit}->{unitNow}; " +
                            $"frame={_lastExitFrame}->{frameNow}; " +
                            $"seq={_lastExitSeq}->{seqNow}; " +
                            $"refObjSame={refObjSame}; " +
                            $"refTableObj 0x{_lastExitRefTableObj:X}->0x{_refTableObjBefore:X}; " +
                            $"refIndex {_lastExitRefIndex}->{_refIndexBefore}; " +
                            $"counterObjSame={counterObjSame}; " +
                            $"counterObj 0x{_lastExitCounterObjPtr:X}->0x{_counterObjPtrBefore:X}; " +
                            $"counter {_lastExitCounter}->{_counterBefore}; " +
                            $"tableBuiltFlag {_lastExitTableBuiltFlag}->{_refTableBuiltFlagBefore}; " +
                            $"stockPtr 0x{_lastExitStockPtr:X}->0x{stockPtrNow:X}; " +
                            $"stockFlagsOk={_lastExitStockFlagsReadOk}/{_stockFlagsReadOkBefore}; " +
                            $"stockFlags 0x{_lastExitStockFlags:X}->0x{_stockFlagsBefore:X}; " +
                            $"bit0x200 {bit200Before}->{bit200After}; " +
                            $"eventNums {_lastExitEventNums}->{eventNumsNow}; " +
                            $"eventOfs {_lastExitEventOfs}->{eventOfsNow}; " +
                            $"rollbackMatchesBit200Theory={rollbackMatchesBit200Theory}.");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DefaultSkillExternalStateProbe prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_captured) return;
            _captured = false;
            try
            {
                var moduleBase = ResolveModuleBase();
                if (moduleBase == IntPtr.Zero) return;

                bool counterOkAfter = TryReadCounter(moduleBase, out byte counterAfter, out long counterObjPtrAfter);
                bool refOkAfter = TryReadRefTable(
                    moduleBase, out short refIndexAfter, out byte tableBuiltFlagAfter, out long obj2After,
                    out long refTableObjAfter);

                var gbwk = rstinit.GBWK;
                long gbwkPtrAfter = gbwk?.Pointer.ToInt64() ?? 0;

                // Always refresh the cross-call baseline when reads succeed,
                // regardless of whether the within-call state "changed" (the
                // rollback we're hunting shows up between calls, not
                // necessarily within one).
                if (refOkAfter && counterOkAfter)
                {
                    var stockForExit = gbwk?.pCurrentStock;
                    _lastExitCaptured = true;
                    _lastExitRefTableObj = refTableObjAfter;
                    _lastExitRefIndex = refIndexAfter;
                    _lastExitCounterObjPtr = counterObjPtrAfter;
                    _lastExitCounter = counterAfter;
                    _lastExitTableBuiltFlag = tableBuiltFlagAfter;
                    _lastExitUnit = stockForExit?.id ?? -1;
                    _lastExitStockPtr = stockForExit?.Pointer.ToInt64() ?? 0;
                    _lastExitEventNums = gbwk?.EventNums ?? 0;
                    _lastExitEventOfs = gbwk?.EventOfs ?? 0;
                    _lastExitSeq = gbwk?.SeqInfo.Current ?? -1;
                    _lastExitStockFlagsReadOk = TryReadStockFlags(
                        stockForExit?.Pointer ?? IntPtr.Zero, out _lastExitStockFlags);
                    _lastExitFrame = UnityEngine.Time.frameCount;
                }

                bool stockFlagsOkAfter = TryReadStockFlags(
                    gbwk?.pCurrentStock?.Pointer ?? IntPtr.Zero, out uint stockFlagsAfter);
                bool bit200Before = _stockFlagsReadOkBefore && (_stockFlagsBefore & 0x200) != 0;
                bool bit200After = stockFlagsOkAfter && (stockFlagsAfter & 0x200) != 0;

                bool changed =
                    !_counterReadOkBefore || !counterOkAfter || _counterBefore != counterAfter ||
                    !_refReadOkBefore || !refOkAfter ||
                    _refIndexBefore != refIndexAfter ||
                    _refTableBuiltFlagBefore != tableBuiltFlagAfter ||
                    _obj2Before != obj2After ||
                    bit200Before != bit200After;
                if (!changed) return;

                int frame = UnityEngine.Time.frameCount;

                // gbwkMatchesObj2Plus0x60 cross-checks the STATIC-ANALYSIS
                // assumption that obj2+0x60 equals gbwk.Pointer - computed
                // fresh here (not assumed) so the report can say definitively
                // whether that mapping is correct.
                long obj2Plus60 = 0;
                bool obj2Plus60ReadOk = false;
                try
                {
                    if (obj2After != 0)
                    {
                        obj2Plus60 = Marshal.ReadInt64(new IntPtr(obj2After + 0x60));
                        obj2Plus60ReadOk = true;
                    }
                }
                catch { /* leave defaults */ }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] DEFAULTSKILL-EXTERNAL-STATE; " +
                    $"frame={frame}; " +
                    $"counterOk={_counterReadOkBefore}/{counterOkAfter}; counter {_counterBefore}->{counterAfter}; " +
                    $"counterObj 0x{_counterObjPtrBefore:X}->0x{counterObjPtrAfter:X}; " +
                    $"refOk={_refReadOkBefore}/{refOkAfter}; refIndex {_refIndexBefore}->{refIndexAfter}; " +
                    $"refTableObj 0x{_refTableObjBefore:X}->0x{refTableObjAfter:X}; " +
                    $"tableBuiltFlag {_refTableBuiltFlagBefore}->{tableBuiltFlagAfter}; " +
                    $"obj2 0x{_obj2Before:X}->0x{obj2After:X}; " +
                    $"gbwkPtr 0x{_gbwkPtrForCompareBefore:X}->0x{gbwkPtrAfter:X}; " +
                    $"obj2Plus0x60ReadOk={obj2Plus60ReadOk}; obj2Plus0x60=0x{obj2Plus60:X}; " +
                    $"matchesGbwkPtr={(obj2Plus60ReadOk && obj2Plus60 == gbwkPtrAfter)}; " +
                    $"stockFlagsOk={_stockFlagsReadOkBefore}/{stockFlagsOkAfter}; " +
                    $"stockFlags 0x{_stockFlagsBefore:X}->0x{stockFlagsAfter:X}; " +
                    $"bit0x200 {bit200Before}->{bit200After}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DefaultSkillExternalStateProbe postfix failed safely: {ex.Message}");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }
}
