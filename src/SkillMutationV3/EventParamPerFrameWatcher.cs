using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - EVENTPARAM PER-FRAME WATCHER.
    // Read-only observer only. Never writes any field.
    //
    // Purpose: DefaultSkillIteratorTrace (hooked on rstcalc.rstCalcEventInfo,
    // the ONE confirmed-by-disassembly writer of GBWK.EventParam via
    // "mov word ptr [r12], ax" at VA 0x18227C5FB) is NOT showing the actual
    // 396->349 / 16->349 transitions in its own before/after snapshots -
    // every logged call either has EventParam unchanged, or already shows
    // the new value BEFORE the call even starts. That means the write is
    // happening somewhere this method-level hook cannot see: either a
    // DIFFERENT call site of the same method that resolves its `ref ushort`
    // argument to something my hook fails to correlate with GBWK.EventParam
    // at the right moment, or a genuinely different writer entirely.
    //
    // This class does not guess which - it polls GBWK.EventParam/EventNums/
    // EventOfs/DefSkillResult UNCONDITIONALLY every frame (via rstcalc.
    // rstCalc's existing per-frame Postfix hook point, the same low-risk
    // pattern already used by ForgetFlowRuntimeTrace/RstCalcState1CDiagnostics/
    // PreCoreGateDiagnostics) and logs on ANY change, regardless of seq or
    // forget-armed state. Cross-referencing the exact frame a change lands
    // on against DEFAULTSKILL-ITERATOR-CALL and FORGET-SEQ-CHANGE logs will
    // show definitively whether the write is inside or outside that hooked
    // method's visible call boundary.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class EventParamPerFrameWatcher
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why.
        internal static readonly bool Enabled = false;

        private static bool _hasLast;
        private static ushort _lastEventParam;
        private static sbyte _lastEventNums;
        private static sbyte _lastEventOfs;
        private static sbyte _lastDefSkillResult;
        private static int _lastUnit;
        private static int _lastSeq;

        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;

                int unit = stock?.id ?? -1;
                ushort eventParam = gbwk.EventParam;
                sbyte eventNums = gbwk.EventNums;
                sbyte eventOfs = gbwk.EventOfs;
                sbyte defSkillResult = gbwk.DefSkillResult;
                int seq = gbwk.SeqInfo.Current;

                if (!_hasLast)
                {
                    _hasLast = true;
                    _lastEventParam = eventParam;
                    _lastEventNums = eventNums;
                    _lastEventOfs = eventOfs;
                    _lastDefSkillResult = defSkillResult;
                    _lastUnit = unit;
                    _lastSeq = seq;
                    return;
                }

                bool changed = eventParam != _lastEventParam || eventNums != _lastEventNums ||
                               eventOfs != _lastEventOfs || defSkillResult != _lastDefSkillResult;
                if (!changed)
                {
                    _lastUnit = unit;
                    _lastSeq = seq;
                    return;
                }

                int frame = UnityEngine.Time.frameCount;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] EVENTPARAM-PERFRAME-CHANGE; " +
                    $"frame={frame}; unit={_lastUnit}->{unit}; seq={_lastSeq}->{seq}; " +
                    $"eventParam {_lastEventParam}->{eventParam}; " +
                    $"eventNums {_lastEventNums}->{eventNums}; " +
                    $"eventOfs {_lastEventOfs}->{eventOfs}; " +
                    $"defSkillResult {_lastDefSkillResult}->{defSkillResult}.");

                _lastEventParam = eventParam;
                _lastEventNums = eventNums;
                _lastEventOfs = eventOfs;
                _lastDefSkillResult = defSkillResult;
                _lastUnit = unit;
                _lastSeq = seq;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] EventParamPerFrameWatcher postfix failed safely: {ex.Message}");
            }
        }
    }
}
