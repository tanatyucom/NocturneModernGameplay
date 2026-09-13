using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - DestroyConfirm immediate before/after trace.
    // Read-only observer only. Never writes SeqInfo.Current, Flag, skill[],
    // skillcnt, or any PUpSkill* field.
    //
    // Purpose: ForgetFlowRuntimeTrace (per-rstcalc-frame sampling) never
    // observed a skillcnt 8->7 dip across two real-machine full-capacity
    // level-up sessions, even though rstUpdateSeqDestroyConfirm's confirmed-
    // delete path (VA 0x182288E65 onward, fully disassembled this session)
    // unconditionally deletes skill[selectedIndex], compacts the array, and
    // recomputes skillcnt. The leading hypothesis is that the dip is too
    // transient for once-per-rstcalc-call sampling to catch - the freed
    // slot may be refilled by the SAME level-up transaction (DefaultSkill's
    // own pending new-skill insertion) before the next rstcalc Postfix runs.
    // This class hooks rstUpdateSeqDestroyConfirm itself (Prefix/Postfix)
    // directly, to see skillcnt/skill[] exactly as they are on entry to and
    // exit from THIS SPECIFIC native call - the tightest window static
    // analysis and the coarser per-frame trace could not resolve.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDestroyConfirm))]
    internal static class DestroyConfirmImmediateTrace
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why.
        internal static readonly bool Enabled = false;

        private static int _invocationCounter;

        private static bool _captured;
        private static int _invocation;
        private static int _seqBefore;
        private static sbyte _flagBefore;
        private static int _unit;
        private static int _skillCntBefore;
        private static int[] _skillsBefore = Array.Empty<int>();
        private static sbyte _pUpResultBefore;
        private static sbyte _pUpIndexBefore;
        private static ushort _pUpIdBefore;

        private static void Prefix()
        {
            _captured = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                var ownedArray = stock.skill;
                if (ownedArray == null) return;
                int arrayLength = ownedArray.Length;
                var skills = new int[arrayLength];
                for (int i = 0; i < arrayLength; i++) skills[i] = ownedArray[i] & 0xFFFF;

                _invocation = ++_invocationCounter;
                _seqBefore = gbwk.SeqInfo.Current;
                _flagBefore = gbwk.Flag;
                _unit = stock.id;
                _skillCntBefore = stock.skillcnt;
                _skillsBefore = skills;
                _pUpResultBefore = gbwk.PUpSkillResult;
                _pUpIndexBefore = gbwk.PUpSkillIndex;
                _pUpIdBefore = gbwk.PUpSkillID;
                _captured = true;
            }
            catch (Exception ex)
            {
                _captured = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DestroyConfirmImmediateTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_captured) return;
            _captured = false; // consume once per Prefix/Postfix pair
            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;

                var ownedArray = stock.skill;
                if (ownedArray == null) return;

                int arrayLength = _skillsBefore.Length;
                var skillsAfter = new int[arrayLength];
                for (int i = 0; i < arrayLength && i < ownedArray.Length; i++)
                    skillsAfter[i] = ownedArray[i] & 0xFFFF;

                int skillCntAfter = stock.skillcnt;
                sbyte pUpResultAfter = gbwk.PUpSkillResult;
                sbyte pUpIndexAfter = gbwk.PUpSkillIndex;
                ushort pUpIdAfter = gbwk.PUpSkillID;
                int seqAfter = gbwk.SeqInfo.Current;
                sbyte flagAfter = gbwk.Flag;

                bool skillCntChanged = skillCntAfter != _skillCntBefore;
                bool arraysChanged = false;
                var changedSlots = new List<string>();
                for (int i = 0; i < arrayLength; i++)
                {
                    if (_skillsBefore[i] != skillsAfter[i])
                    {
                        arraysChanged = true;
                        changedSlots.Add($"slot{i}:{_skillsBefore[i]}->{skillsAfter[i]}");
                    }
                }
                bool pUpChanged = pUpResultAfter != _pUpResultBefore ||
                                   pUpIndexAfter != _pUpIndexBefore ||
                                   pUpIdAfter != _pUpIdBefore;

                if (!skillCntChanged && !arraysChanged && !pUpChanged) return; // boring frame, no logging

                int frame = UnityEngine.Time.frameCount;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] DESTROYCONFIRM-BEGIN; " +
                    $"invocation={_invocation}; frame={frame}; unit={_unit}; " +
                    $"seqBefore={_seqBefore}; flagBefore={_flagBefore}; " +
                    $"skillCntBefore={_skillCntBefore}; " +
                    $"pUpResultBefore={_pUpResultBefore}; pUpIndexBefore={_pUpIndexBefore}; pUpIdBefore={_pUpIdBefore}; " +
                    $"skillsBefore=[{string.Join(",", _skillsBefore)}].");

                MelonLogger.Msg(
                    "[NocturneModernGameplay] DESTROYCONFIRM-END; " +
                    $"invocation={_invocation}; frame={frame}; unit={_unit}; " +
                    $"seqAfter={seqAfter}; flagAfter={flagAfter}; " +
                    $"skillCntAfter={skillCntAfter}; " +
                    $"pUpResultAfter={pUpResultAfter}; pUpIndexAfter={pUpIndexAfter}; pUpIdAfter={pUpIdAfter}; " +
                    $"skillsAfter=[{string.Join(",", skillsAfter)}].");

                if (_skillCntBefore == 8 && skillCntAfter == 7)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] DESTROYCONFIRM-DELETE-OBSERVED; " +
                        $"invocation={_invocation}; unit={_unit}; " +
                        $"skillsBefore=[{string.Join(",", _skillsBefore)}]; " +
                        $"skillsAfter=[{string.Join(",", skillsAfter)}].");
                }
                else if (!skillCntChanged && arraysChanged)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] DESTROYCONFIRM-REPLACE-OBSERVED; " +
                        $"invocation={_invocation}; unit={_unit}; skillCnt={skillCntAfter}; " +
                        $"changedSlots=[{string.Join(",", changedSlots)}].");
                }
                else if (skillCntChanged)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] DESTROYCONFIRM-SKILLCNT-CHANGE; " +
                        $"invocation={_invocation}; unit={_unit}; " +
                        $"skillCntBefore={_skillCntBefore}; skillCntAfter={skillCntAfter}.");
                }

                if (pUpChanged)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] DESTROYCONFIRM-PUPSKILL-CHANGE; " +
                        $"invocation={_invocation}; unit={_unit}; " +
                        $"pUpResultBefore={_pUpResultBefore}; pUpResultAfter={pUpResultAfter}; " +
                        $"pUpIndexBefore={_pUpIndexBefore}; pUpIndexAfter={pUpIndexAfter}; " +
                        $"pUpIdBefore={_pUpIdBefore}; pUpIdAfter={pUpIdAfter}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DestroyConfirmImmediateTrace postfix failed safely: {ex.Message}");
            }
        }
    }
}
