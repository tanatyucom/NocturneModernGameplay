using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - FULL-CAPACITY FORGET FLOW runtime trace.
    // Read-only observer only. Never writes SeqInfo.Current, Flag, skill[],
    // skillcnt, or any PUpSkill* field - this class exists purely to watch
    // native's OWN forget-flow (seq21 rstUpdateSeqDestroySkill / seq22
    // rstUpdateSeqDestroyConfirm), reached through the ORDINARY level-up
    // "learn a new skill, no room left" path (seq8 rstUpdateSeqDefaultSkill),
    // to answer the one remaining question static analysis could not:
    // where does native go after a skill is actually deleted and the
    // forget UI closes.
    //
    // Static evidence this session already established (full disassembly,
    // not guessed):
    //   - seq21 (DestroySkill) writes SeqInfo.Current=22 on confirm-select
    //     (VA 0x182289502).
    //   - seq22 (DestroyConfirm) writes SeqInfo.Current=21 only on its
    //     Flag==3 "cancel" input path (VA 0x182288AE4-equivalent site).
    //   - The delete-confirmed path (index<8) deletes skill[selectedIndex],
    //     compacts skill[], recomputes skillcnt, calls the HP/MP-recalc
    //     helper (misleadingly named rstAddSkill), and falls through to
    //     GBWK.Flag=1 - it does NOT write SeqInfo.Current itself.
    //   - Every helper function reachable from seq21/22 (four independently
    //     confirmed this session) operates on entirely separate UI-widget/
    //     message-queue/input-state globals - none read or write
    //     GBWK.PUpSkillID(+0x4E)/PUpSkillResult(+0x4B)/PUpSkillIndex(+0x4C).
    // What remains UNRESOLVED and this trace is meant to answer: after
    // Flag=1, does native's own Flag/Seq state ever leave the 21/22 pair,
    // and if so, to which seq and via what field transition.
    //
    // Hooked at rstcalc.rstCalc's existing per-frame Postfix hook point
    // (already used read-only by RstCalcState1CDiagnostics for an
    // unrelated purpose - this is a second, independent Postfix on the
    // same method, which Harmony supports natively). "Armed" tracking
    // (only active while at/near seq 21/22, plus a short cooldown after
    // leaving, to capture the exit transition) keeps this from spamming
    // logs during ordinary unrelated gameplay.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class ForgetFlowRuntimeTrace
    {
        // Read-only diagnostic. Safe to leave enabled - performs no writes
        // of any kind.
        // Re-enabled (alone, alongside DefaultSkillIteratorTrace only) for
        // the HIDDEN PENDING SKILL / FORGET LOOP investigation - need full
        // seq/Flag/PUpSkill* transition visibility around
        // FULLCAP-ADDNEW-BEGIN to check whether native's own 349 checkpoint
        // was already resolved at gate-open time.
        internal static readonly bool Enabled = true;

        private const int CooldownFrames = 180; // ~3s at 60fps after leaving seq 21/22, to still capture the exit

        private static bool _armed;
        private static int _cooldownRemaining;

        private static bool _hasLast;
        private static int _lastSeq;
        private static sbyte _lastFlag;
        private static int _lastUnit;
        private static int _lastSkillCnt;
        private static sbyte _lastPUpResult;
        private static sbyte _lastPUpIndex;
        private static ushort _lastPUpId;
        private static int[] _lastSkillArray = Array.Empty<int>();

        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                int seq = gbwk.SeqInfo.Current;
                sbyte flag = gbwk.Flag;
                int unit = stock.id;
                int skillCnt = stock.skillcnt;
                sbyte pUpResult = gbwk.PUpSkillResult;
                sbyte pUpIndex = gbwk.PUpSkillIndex;
                ushort pUpId = gbwk.PUpSkillID;

                var ownedArray = stock.skill;
                int arrayLength = ownedArray?.Length ?? 0;
                var skillArray = new int[arrayLength];
                for (int i = 0; i < arrayLength; i++) skillArray[i] = ownedArray![i] & 0xFFFF;

                bool inForgetRange = seq == 21 || seq == 22;
                if (inForgetRange)
                {
                    _armed = true;
                    _cooldownRemaining = CooldownFrames;
                }
                else if (_armed)
                {
                    _cooldownRemaining--;
                    if (_cooldownRemaining <= 0) _armed = false;
                }

                if (!_armed && !inForgetRange)
                {
                    // Not in or near the forget flow - still update the
                    // "last" snapshot so the eventual entry into seq21/22
                    // logs an accurate from= value, but do not emit any
                    // log lines.
                    _hasLast = true;
                    _lastSeq = seq; _lastFlag = flag; _lastUnit = unit; _lastSkillCnt = skillCnt;
                    _lastPUpResult = pUpResult; _lastPUpIndex = pUpIndex; _lastPUpId = pUpId;
                    _lastSkillArray = skillArray;
                    return;
                }

                if (!_hasLast)
                {
                    _hasLast = true;
                    _lastSeq = seq; _lastFlag = flag; _lastUnit = unit; _lastSkillCnt = skillCnt;
                    _lastPUpResult = pUpResult; _lastPUpIndex = pUpIndex; _lastPUpId = pUpId;
                    _lastSkillArray = skillArray;
                    return;
                }

                bool changed = seq != _lastSeq || flag != _lastFlag || unit != _lastUnit ||
                               skillCnt != _lastSkillCnt || pUpResult != _lastPUpResult ||
                               pUpIndex != _lastPUpIndex || pUpId != _lastPUpId;
                if (!changed) return;

                int frame = UnityEngine.Time.frameCount;

                if (seq != _lastSeq)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] FORGET-SEQ-CHANGE; " +
                        $"frame={frame}; unit={unit}; from={_lastSeq}; to={seq}.");
                }

                if (flag != _lastFlag)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] FORGET-FLAG-CHANGE; " +
                        $"frame={frame}; seq={seq}; unit={unit}; from={_lastFlag}; to={flag}.");
                }

                if (pUpResult != _lastPUpResult || pUpIndex != _lastPUpIndex || pUpId != _lastPUpId)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] FORGET-PUPSKILL-CHANGE; " +
                        $"frame={frame}; seq={seq}; unit={unit}; " +
                        $"pUpResultBefore={_lastPUpResult}; pUpResultAfter={pUpResult}; " +
                        $"pUpIndexBefore={_lastPUpIndex}; pUpIndexAfter={pUpIndex}; " +
                        $"pUpIdBefore={_lastPUpId}; pUpIdAfter={pUpId}.");
                }

                if (skillCnt != _lastSkillCnt)
                {
                    if (_lastSkillCnt == 8 && skillCnt == 7)
                    {
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] FORGET-DELETE-DETECTED; " +
                            $"frame={frame}; seq={seq}; flag={flag}; unit={unit}; " +
                            $"skillCntBefore={_lastSkillCnt}; skillCntAfter={skillCnt}; " +
                            $"pUpResult={pUpResult}; pUpIndex={pUpIndex}; pUpId={pUpId}; " +
                            $"skillsBefore=[{string.Join(",", _lastSkillArray)}]; " +
                            $"skillsAfter=[{string.Join(",", skillArray)}].");
                    }
                    else
                    {
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] FORGET-SKILLCNT-CHANGE; " +
                            $"frame={frame}; seq={seq}; flag={flag}; unit={unit}; " +
                            $"skillCntBefore={_lastSkillCnt}; skillCntAfter={skillCnt}; " +
                            $"skillsBefore=[{string.Join(",", _lastSkillArray)}]; " +
                            $"skillsAfter=[{string.Join(",", skillArray)}].");
                    }
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] FORGET-TRACE; " +
                    $"frame={frame}; seq={seq}; flag={flag}; unit={unit}; skillCnt={skillCnt}; " +
                    $"pUpResult={pUpResult}; pUpIndex={pUpIndex}; pUpId={pUpId}.");

                _lastSeq = seq; _lastFlag = flag; _lastUnit = unit; _lastSkillCnt = skillCnt;
                _lastPUpResult = pUpResult; _lastPUpIndex = pUpIndex; _lastPUpId = pUpId;
                _lastSkillArray = skillArray;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] ForgetFlowRuntimeTrace postfix failed safely: {ex.Message}");
            }
        }
    }
}
