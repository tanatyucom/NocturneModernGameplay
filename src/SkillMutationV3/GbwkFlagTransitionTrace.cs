using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // GBWK.FLAG (+0x7E) WRITER/READER RUNTIME TRACE PoC (2026-09-19).
    // Read-only observer only. Never writes GBWK.Flag or any other native
    // field.
    //
    // Static xref scan (.analysis/search_gbwk_plus0x7e_all_refs.py,
    // .analysis/group_0x7e_refs_by_function.py, .analysis/gbwk_plus0x7e_
    // hits.txt) found GBWK.Flag is a SHARED byte read/written by 14
    // different rstupdate.rstUpdateSeq* functions, not SkillPowerUp-
    // exclusive as earlier Canonical assumed - both descriptions
    // (SkillPowerUp's own "classification result 0-4" and
    // docs/investigation-log.md's "6-element array progress cursor") are
    // correct for their own function, not contradictory.
    //
    // Most importantly: rstUpdateSeqHeartsMaster (VA 0x18228BC20) has a
    // CONFIRMED `inc byte ptr [rax+0x7e]` path (VA 0x18228C146) taken
    // whenever entry Flag is neither 0 nor 1 - a leftover SkillPowerUp
    // classification value (3 or 4) that was not consumed/reset before
    // HeartsMaster next runs gets silently bumped (3->4, 4->5). Real-
    // machine evidence already shows Flag=3 (Power-Up succeeded, 24 valid
    // Mutation candidates) vs Flag=5 (Core result=0, no candidates at all)
    // for the SAME save, SAME unit (stockPtr 0x2234C48A160), SAME
    // EventParam sequence (64->43) - this class exists to catch the FIRST
    // moment that divergence appears, nothing more. Do NOT reinterpret a
    // "same save, same conditions, different outcome" result as evidence
    // that conditions actually differed - find the first state divergence
    // instead.
    //
    // Deliberately transition-driven only (a prior high-frequency
    // diagnostic session caused a real slowdown - never repeat that
    // mistake): rstUpdateSeqSkillPowerUp/rstUpdateSeqDevilParam log only
    // when Flag actually changed across their own call; rstUpdate (the
    // top-level per-frame dispatcher) logs only when Flag or SeqInfo.
    // Current/Last changed since its own last call. rstUpdateSeqHeartsMaster
    // is the one exception - it ALWAYS logs when entry Flag>=2 (the
    // suspected leftover-classification-value case), in addition to
    // logging on any change.
    internal static class GbwkFlagTransitionTrace
    {
        // Disabled 2026-09-19: root cause found (see 01_CURRENT_STATE.md
        // Phase H) - the Hearts/DevilParam GBWK.Flag(+0x7E) system is a
        // confirmed separate mechanism from Skill Power-Up/Mutation, not
        // the cause under investigation. Kept as a diagnostic asset (not
        // deleted) for any future Hearts-event/episode-latch work.
        internal static readonly bool Enabled = false;

        private static string Describe(string tag, int frame, int unit, long stockPtr,
            sbyte oldFlag, sbyte newFlag, int seqCurrent, int seqLast, short levelUpCnt,
            ushort eventParam) =>
            $"[NocturneModernGameplay] {tag}; " +
            $"frame={frame}; unit={unit}; stockPtr=0x{stockPtr:X}; oldFlag={oldFlag}; " +
            $"newFlag={newFlag}; seqCurrent={seqCurrent}; seqLast={seqLast}; " +
            $"levelUpCnt={levelUpCnt}; eventParam={eventParam}.";

        // HEARTS/DEVILPARAM/SKILLPOWERUP CORRELATION PoC (2026-09-19).
        // Real-machine confirmation step for the static RE chain:
        //   RNG (VA 0x1821690d0, x2) -> GBWK+0x39 (EventType, read-only here)
        //   -> rstSetHeartsEvent -> GBWK+0x80 array[+0x20+i]++
        //   -> rstUpdateSeqDevilParam searches that array from Flag as start
        //      index -> advances Flag (progress) or leaves it untouched (stall)
        // Deliberately NOT claiming "array hit => SkillPowerUp success" as a
        // settled fact yet - only that DevilParam's own progress and
        // SkillPowerUp's own reach/no-reach are the two independent,
        // directly-observable outcomes to correlate. Read-only: never
        // writes GBWK+0x39, the array, or GBWK.Flag.
        private static byte ReadEventType(IntPtr gbwkPtr) =>
            gbwkPtr == IntPtr.Zero ? (byte)0xFF : Marshal.ReadByte(gbwkPtr, 0x39);

        private static string ReadHeartsEventArray(IntPtr gbwkPtr)
        {
            try
            {
                if (gbwkPtr == IntPtr.Zero) return "null";
                IntPtr arrayObj = Marshal.ReadIntPtr(gbwkPtr, 0x80);
                if (arrayObj == IntPtr.Zero) return "null";

                var values = new byte[6];
                for (int i = 0; i < 6; i++)
                {
                    values[i] = Marshal.ReadByte(arrayObj, 0x20 + i);
                }
                return string.Join(",", values);
            }
            catch (Exception ex)
            {
                return $"err({ex.Message})";
            }
        }

        // ---- rstUpdateSeqSkillPowerUp (VA 0x18228C770): log only when
        // Flag actually changed across this call. ----
        [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
        internal static class SkillPowerUpFlagTrace
        {
            private static bool _captured;
            private static int _unit;
            private static long _stockPtr;
            private static sbyte _oldFlag;

            private static void Prefix()
            {
                _captured = false;
                if (!Enabled) return;
                try
                {
                    var gbwk = rstinit.GBWK;
                    var stock = gbwk?.pCurrentStock;
                    if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;
                    _unit = stock.id;
                    _stockPtr = stock.Pointer.ToInt64();
                    _oldFlag = gbwk.Flag;
                    _captured = true;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(SkillPowerUp) prefix failed safely: {ex.Message}");
                }
            }

            private static void Postfix()
            {
                if (!_captured) return;
                _captured = false;
                try
                {
                    var gbwk = rstinit.GBWK;
                    if (gbwk == null) return;
                    sbyte newFlag = gbwk.Flag;
                    if (newFlag == _oldFlag) return;

                    int frame = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(GbwkFlagTransitionTrace.Describe(
                        "GBWKFLAG-SKILLPOWERUP", frame, _unit, _stockPtr, _oldFlag, newFlag,
                        gbwk.SeqInfo.Current, gbwk.SeqInfo.Last, gbwk.LevelUpCnt, gbwk.EventParam));
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(SkillPowerUp) postfix failed safely: {ex.Message}");
                }
            }
        }

        // ---- rstUpdateSeqHeartsMaster (VA 0x18228BC20): the prime
        // suspect. ALWAYS log when entry Flag>=2 (regardless of whether it
        // changes this call), in addition to logging on any change. ----
        [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqHeartsMaster))]
        internal static class HeartsMasterFlagTrace
        {
            private static bool _captured;
            private static bool _mustLog;
            private static int _unit;
            private static long _stockPtr;
            private static sbyte _oldFlag;

            private static void Prefix()
            {
                _captured = false;
                _mustLog = false;
                if (!Enabled) return;
                try
                {
                    var gbwk = rstinit.GBWK;
                    var stock = gbwk?.pCurrentStock;
                    if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;
                    _unit = stock.id;
                    _stockPtr = stock.Pointer.ToInt64();
                    _oldFlag = gbwk.Flag;
                    _mustLog = _oldFlag >= 2;
                    _captured = true;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(HeartsMaster) prefix failed safely: {ex.Message}");
                }
            }

            private static void Postfix()
            {
                if (!_captured) return;
                _captured = false;
                try
                {
                    var gbwk = rstinit.GBWK;
                    if (gbwk == null) return;
                    sbyte newFlag = gbwk.Flag;
                    if (!_mustLog && newFlag == _oldFlag) return;

                    int frame = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(GbwkFlagTransitionTrace.Describe(
                        "GBWKFLAG-HEARTSMASTER", frame, _unit, _stockPtr, _oldFlag, newFlag,
                        gbwk.SeqInfo.Current, gbwk.SeqInfo.Last, gbwk.LevelUpCnt, gbwk.EventParam));
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(HeartsMaster) postfix failed safely: {ex.Message}");
                }
            }
        }

        // ---- rstUpdateSeqDevilParam (VA 0x182289770): log only when Flag
        // actually changed across this call. The `mov [rax+0x7e],bl`
        // register-sourced write here (VA 0x182289A89) was not resolved
        // statically - this is what confirms or rules out its real value. ----
        [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDevilParam))]
        internal static class DevilParamFlagTrace
        {
            private static bool _captured;
            private static int _unit;
            private static long _stockPtr;
            private static sbyte _oldFlag;
            private static string _arrayBefore = "";

            private static void Prefix()
            {
                _captured = false;
                if (!Enabled) return;
                try
                {
                    var gbwk = rstinit.GBWK;
                    var stock = gbwk?.pCurrentStock;
                    if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;
                    _unit = stock.id;
                    _stockPtr = stock.Pointer.ToInt64();
                    _oldFlag = gbwk.Flag;
                    _arrayBefore = GbwkFlagTransitionTrace.ReadHeartsEventArray(gbwk.Pointer);
                    _captured = true;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(DevilParam) prefix failed safely: {ex.Message}");
                }
            }

            private static void Postfix()
            {
                if (!_captured) return;
                _captured = false;
                try
                {
                    var gbwk = rstinit.GBWK;
                    if (gbwk == null) return;
                    sbyte newFlag = gbwk.Flag;
                    if (newFlag == _oldFlag) return;

                    string arrayAfter = GbwkFlagTransitionTrace.ReadHeartsEventArray(gbwk.Pointer);
                    int frame = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(
                        GbwkFlagTransitionTrace.Describe(
                            "GBWKFLAG-DEVILPARAM", frame, _unit, _stockPtr, _oldFlag, newFlag,
                            gbwk.SeqInfo.Current, gbwk.SeqInfo.Last, gbwk.LevelUpCnt, gbwk.EventParam) +
                        $" arrayBefore=[{_arrayBefore}]; arrayAfter=[{arrayAfter}].");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(DevilParam) postfix failed safely: {ex.Message}");
                }
            }
        }

        // ---- rstSetHeartsEvent (VA 0x182286260): the ONLY writer of the
        // GBWK+0x80 array. Log only when the array actually changed
        // (transition-driven) - this function's own caller chain
        // (rstUpdateSeqHeartsEvent, gated by Flag==3 + two further gates)
        // could in principle re-enter across several frames before its
        // body actually mutates anything, so do not log unconditionally. ----
        [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstSetHeartsEvent))]
        internal static class HeartsEventCallTrace
        {
            private static bool _captured;
            private static int _unit;
            private static long _stockPtr;
            private static byte _eventType;
            private static string _arrayBefore = "";

            private static void Prefix()
            {
                _captured = false;
                if (!Enabled) return;
                try
                {
                    var gbwk = rstinit.GBWK;
                    var stock = gbwk?.pCurrentStock;
                    if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;
                    _unit = stock.id;
                    _stockPtr = stock.Pointer.ToInt64();
                    _eventType = GbwkFlagTransitionTrace.ReadEventType(gbwk.Pointer);
                    _arrayBefore = GbwkFlagTransitionTrace.ReadHeartsEventArray(gbwk.Pointer);
                    _captured = true;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(HeartsEventCall) prefix failed safely: {ex.Message}");
                }
            }

            private static void Postfix()
            {
                if (!_captured) return;
                _captured = false;
                try
                {
                    var gbwk = rstinit.GBWK;
                    if (gbwk == null) return;
                    string arrayAfter = GbwkFlagTransitionTrace.ReadHeartsEventArray(gbwk.Pointer);
                    if (arrayAfter == _arrayBefore) return;

                    int frame = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] GBWKFLAG-HEARTSEVENTCALL; " +
                        $"frame={frame}; unit={_unit}; stockPtr=0x{_stockPtr:X}; " +
                        $"eventType={_eventType}; arrayBefore=[{_arrayBefore}]; arrayAfter=[{arrayAfter}]; " +
                        $"flag={gbwk.Flag}; seqCurrent={gbwk.SeqInfo.Current}; seqLast={gbwk.SeqInfo.Last}; " +
                        $"levelUpCnt={gbwk.LevelUpCnt}.");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(HeartsEventCall) postfix failed safely: {ex.Message}");
                }
            }
        }

        // ---- rstUpdateSeqSkillPowerUp reach marker: transition-driven on
        // WHICH stockPtr is being processed (not on Flag), so this logs
        // exactly once when a new unit starts being run through this
        // function - answers "did SkillPowerUp get reached for this unit
        // at all this episode" independent of whether Flag ever changed. ----
        [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
        internal static class SkillPowerUpReachedTrace
        {
            private static bool _hasLast;
            private static long _lastStockPtr;

            private static void Prefix()
            {
                if (!Enabled) return;
                try
                {
                    var gbwk = rstinit.GBWK;
                    var stock = gbwk?.pCurrentStock;
                    if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;

                    long stockPtr = stock.Pointer.ToInt64();
                    if (_hasLast && stockPtr == _lastStockPtr) return;
                    _lastStockPtr = stockPtr;
                    _hasLast = true;

                    int frame = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLPOWERUP-REACHED; " +
                        $"frame={frame}; unit={stock.id}; stockPtr=0x{stockPtr:X}; flag={gbwk.Flag}; " +
                        $"seqCurrent={gbwk.SeqInfo.Current}; seqLast={gbwk.SeqInfo.Last}; " +
                        $"levelUpCnt={gbwk.LevelUpCnt}; eventParam={gbwk.EventParam}.");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(SkillPowerUpReached) prefix failed safely: {ex.Message}");
                }
            }
        }

        // ---- rstUpdate (VA 0x18228CDE0, top-level per-frame dispatcher):
        // silent unless Flag or SeqInfo.Current/Last changed since this
        // trace's own last observed call - never unconditional, this
        // function runs every frame. ----
        [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
        internal static class TopLevelFlagTrace
        {
            private static bool _hasLast;
            private static sbyte _lastFlag;
            private static int _lastSeqCurrent;
            private static int _lastSeqLast;

            private static void Postfix()
            {
                if (!Enabled) return;
                try
                {
                    var gbwk = rstinit.GBWK;
                    if (gbwk == null) return;
                    var stock = gbwk.pCurrentStock;

                    sbyte newFlag = gbwk.Flag;
                    int seqCurrent = gbwk.SeqInfo.Current;
                    int seqLast = gbwk.SeqInfo.Last;

                    if (_hasLast && newFlag == _lastFlag && seqCurrent == _lastSeqCurrent &&
                        seqLast == _lastSeqLast)
                    {
                        return;
                    }

                    sbyte oldFlag = _hasLast ? _lastFlag : newFlag;
                    _lastFlag = newFlag;
                    _lastSeqCurrent = seqCurrent;
                    _lastSeqLast = seqLast;
                    _hasLast = true;

                    int unit = stock?.id ?? -1;
                    long stockPtr = stock?.Pointer.ToInt64() ?? 0;
                    int frame = UnityEngine.Time.frameCount;

                    MelonLogger.Msg(GbwkFlagTransitionTrace.Describe(
                        "GBWKFLAG-RSTUPDATE", frame, unit, stockPtr, oldFlag, newFlag,
                        seqCurrent, seqLast, gbwk.LevelUpCnt, gbwk.EventParam));
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] GbwkFlagTransitionTrace(rstUpdate) postfix failed safely: {ex.Message}");
                }
            }
        }
    }
}
