using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Mutation AddNew - EMPTY-SLOT PoC (2026-09-15, User-approved design
    // candidate B).
    //
    // Zero-base mirror of AddNewEmptySlotPoc.cs (the ordinary Power-Up
    // equivalent), gated on PUpSkillResult==2 (Mutation success) instead of
    // ==1. Same mechanism: rstUpdateSeqSkillPowerUp computes the ownership
    // write's destination address from GBWK.PUpSkillIndex alone, so
    // redirecting that field to the next empty slot (skillcnt) for the
    // duration of one native call is sufficient to land the Mutation
    // target in a NEW slot instead of overwriting the source skill -
    // rstOverWriteSkill itself is a trivial *pSkill=NewSkillID store with
    // no other side effects (CONFIRMED, both this session and the prior
    // Power-Up investigation). skillcnt is committed manually afterward
    // (native's own ownership-write path never touches it, only the
    // DefaultSkill/cmbAddSkill path does).
    //
    // Naturally disjoint from MutationFullCapacityAddNewBridgePoc.cs by the
    // skillcnt guard alone (this class requires skillcnt < 8, that one
    // requires skillcnt == 8) - no additional cross-guard needed between
    // the two Mutation classes. Cross-guarded against the ORDINARY
    // Power-Up bridges (FullCapacityAddNewBridgeState.Active) for the same
    // reason as the full-capacity Mutation trigger - see that class's
    // comment.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
    internal static class MutationAddNewEmptySlotPoc
    {
        internal static readonly bool Enabled = true;

        private const int LogicalSkillCapacity = 8;

        private static bool _active;
        private static bool _restored;
        private static int _invocation;
        private static int _unit;
        private static int _originalIndex;
        private static int _emptySlot;
        private static int _sourceSkillId;
        private static int _targetSkillId;
        private static int _originalSkillCnt;
        private static int[] _skillsBefore = Array.Empty<int>();

        private static int _invocationCounter;

        private static bool _eventCommitted;
        private static int _committedUnit;
        private static int _committedOriginalIndex;
        private static int _committedSourceSkillId;
        private static int _committedTargetSkillId;
        private static bool _blockLoggedForCurrentLatch;

        private static void Prefix()
        {
            _active = false;
            _restored = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;

                // Mutation success only. Ordinary Power-Up (==1) is
                // AddNewEmptySlotPoc's own domain, untouched here.
                if (gbwk.PUpSkillResult != 2)
                {
                    if (_eventCommitted)
                    {
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] MUTADDNEW-LATCH-RESET; " +
                            $"unit={_committedUnit}; pUpSkillResult={gbwk.PUpSkillResult}.");
                    }
                    _eventCommitted = false;
                    _blockLoggedForCurrentLatch = false;
                    return;
                }

                // Mutual exclusion against ordinary Power-Up AddNew
                // (either variant) in flight - same reasoning as
                // MutationFullCapacityAddNewBridgeTrigger.
                if (FullCapacityAddNewBridgeState.Active) return;

                sbyte originalIndex = gbwk.PUpSkillIndex;
                if (originalIndex < 0) return;

                ushort targetSkillId = gbwk.PUpSkillID;
                if (targetSkillId == 0) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                var ownedArray = stock.skill;
                if (ownedArray == null) return;

                int skillCnt = stock.skillcnt;
                int arrayLength = ownedArray.Length;
                int unit = stock.id;

                if (originalIndex >= skillCnt) return;

                int sourceSkillId = ownedArray[originalIndex] & 0xFFFF;
                if (sourceSkillId == 0) return;

                bool sameAsCommittedEvent = _eventCommitted &&
                    unit == _committedUnit &&
                    originalIndex == _committedOriginalIndex &&
                    sourceSkillId == _committedSourceSkillId &&
                    targetSkillId == _committedTargetSkillId;
                if (sameAsCommittedEvent)
                {
                    if (!_blockLoggedForCurrentLatch)
                    {
                        _blockLoggedForCurrentLatch = true;
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] MUTADDNEW-LATCH-BLOCK; " +
                            $"unit={unit}; originalIndex={originalIndex}; " +
                            $"source={SkillNameResolver.Format(sourceSkillId)}; " +
                            $"target={SkillNameResolver.Format(targetSkillId)}; " +
                            "reason=same-committed-event.");
                    }
                    return;
                }

                int invocation = ++_invocationCounter;

                if (skillCnt < 0 || skillCnt >= LogicalSkillCapacity || arrayLength < LogicalSkillCapacity)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTADDNEW-POC-SKIP; " +
                        $"invocation={invocation}; unit={unit}; reason=no-capacity.");
                    return; // full capacity: MutationFullCapacityAddNewBridgePoc's domain
                }

                bool packed = true;
                for (int i = 0; i < skillCnt; i++)
                {
                    if ((ownedArray[i] & 0xFFFF) == 0) { packed = false; break; }
                }
                if (packed && (ownedArray[skillCnt] & 0xFFFF) != 0) packed = false;

                if (!packed)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTADDNEW-POC-SKIP; " +
                        $"invocation={invocation}; unit={unit}; reason=non-packed-skill-layout.");
                    return;
                }

                int emptySlot = skillCnt;

                var skillsBefore = new int[arrayLength];
                for (int i = 0; i < arrayLength; i++) skillsBefore[i] = ownedArray[i] & 0xFFFF;

                _invocation = invocation;
                _unit = unit;
                _originalIndex = originalIndex;
                _emptySlot = emptySlot;
                _sourceSkillId = sourceSkillId;
                _targetSkillId = targetSkillId;
                _originalSkillCnt = skillCnt;
                _skillsBefore = skillsBefore;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MUTADDNEW-POC-BEGIN; " +
                    $"invocation={_invocation}; unit={_unit}; " +
                    $"originalIndex={_originalIndex}; emptySlot={_emptySlot}; " +
                    $"skillCnt={_originalSkillCnt}; " +
                    $"source={SkillNameResolver.Format(_sourceSkillId)}; " +
                    $"target={SkillNameResolver.Format(_targetSkillId)}; " +
                    $"skillsBefore=[{string.Join(",", _skillsBefore)}].");

                gbwk.PUpSkillIndex = (sbyte)emptySlot;
                _active = true;
            }
            catch (Exception ex)
            {
                _active = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationAddNewEmptySlotPoc prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_active) return;
            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;

                var ownedArray = stock.skill;
                if (ownedArray == null) return;

                int count = _skillsBefore.Length;
                var skillsAfter = new List<int>(count);
                for (int i = 0; i < count && i < ownedArray.Length; i++)
                    skillsAfter.Add(ownedArray[i] & 0xFFFF);

                bool sourcePreserved = _originalIndex < skillsAfter.Count &&
                                       skillsAfter[_originalIndex] == _sourceSkillId;
                bool targetAtEmptySlot = _emptySlot < skillsAfter.Count &&
                                         skillsAfter[_emptySlot] == _targetSkillId;
                int currentSkillCnt = stock.skillcnt;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MUTADDNEW-POC-END; " +
                    $"invocation={_invocation}; unit={_unit}; " +
                    $"skillsAfter=[{string.Join(",", skillsAfter)}]; " +
                    $"sourcePreserved={sourcePreserved}; targetAtEmptySlot={targetAtEmptySlot}; " +
                    $"skillCntCurrent={currentSkillCnt}.");

                if (!targetAtEmptySlot)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTADDNEW-POC-NO-COMMIT; " +
                        $"invocation={_invocation}; unit={_unit}; reason=target-not-written.");
                }
                else if (!sourcePreserved)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTADDNEW-POC-NO-COMMIT; " +
                        $"invocation={_invocation}; unit={_unit}; reason=source-not-preserved.");
                }
                else if (currentSkillCnt != _originalSkillCnt)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTADDNEW-POC-NO-COMMIT; " +
                        $"invocation={_invocation}; unit={_unit}; reason=skillcnt-changed-unexpectedly.");
                }
                else
                {
                    stock.skillcnt = _originalSkillCnt + 1;
                    _eventCommitted = true;
                    _blockLoggedForCurrentLatch = false;
                    _committedUnit = _unit;
                    _committedOriginalIndex = _originalIndex;
                    _committedSourceSkillId = _sourceSkillId;
                    _committedTargetSkillId = _targetSkillId;

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTADDNEW-LATCH-SET; " +
                        $"unit={_unit}; originalIndex={_originalIndex}; " +
                        $"source={SkillNameResolver.Format(_sourceSkillId)}; " +
                        $"target={SkillNameResolver.Format(_targetSkillId)}.");

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MUTADDNEW-POC-COMMIT; " +
                        $"invocation={_invocation}; unit={_unit}; " +
                        $"sourceSlot={_originalIndex}; targetSlot={_emptySlot}; " +
                        $"source={SkillNameResolver.Format(_sourceSkillId)}; " +
                        $"target={SkillNameResolver.Format(_targetSkillId)}; " +
                        $"skillCntBefore={_originalSkillCnt}; skillCntAfter={stock.skillcnt}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationAddNewEmptySlotPoc postfix failed safely: {ex.Message}");
            }
            finally
            {
                RestoreOriginalIndex();
            }
        }

        private static Exception? Finalizer(Exception? __exception)
        {
            if (_active && !_restored)
            {
                if (__exception != null)
                {
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] MUTADDNEW-POC-EXCEPTION; " +
                        $"invocation={_invocation}; unit={_unit}; " +
                        $"restoring PUpSkillIndex after an exception: {__exception.Message}");
                }
                RestoreOriginalIndex();
            }
            _active = false;
            return __exception;
        }

        private static void RestoreOriginalIndex()
        {
            if (_restored) return;
            _restored = true;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk != null) gbwk.PUpSkillIndex = (sbyte)_originalIndex;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationAddNewEmptySlotPoc restore failed: {ex.Message}");
            }
        }
    }
}
