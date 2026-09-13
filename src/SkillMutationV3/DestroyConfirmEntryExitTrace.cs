using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - NATIVE vs BRIDGE FORGET-FLOW STATE DIFF.
    // Read-only observer only. Never writes any field.
    //
    // Purpose: an earlier round of this investigation found that native's
    // OWN forget episode (DefaultSkill's own +0x3c==2 -> seq21 -> seq22,
    // e.g. forgetting "400" to make room for a curriculum-mandated skill)
    // exits directly from seq22 to seq10 (FORGET-SEQ-CHANGE frame=3944:
    // 22->10 in one prior session's log), while the FullCapacityAddNew
    // bridge's own forget episode (borrowing the same seq21/22) was
    // observed exiting from seq22 to seq8 instead (FORGET-SEQ-CHANGE
    // frame=7769: 22->8) - immediately re-entering rstUpdateSeqDefaultSkill
    // rather than returning to the Power-Up/ordinary flow. This is the
    // single most concrete, reproducible difference found between the two
    // paths so far, and a strong candidate for why the bridge case alone
    // re-offers a just-forgotten skill and re-rolls Mutation.
    //
    // This class does NOT assume the root cause is already known - it logs
    // a uniform snapshot (EventParam, EventNums, EventOfs, DefSkillResult,
    // sourceObj+0x3c is the SAME field as DefSkillResult per cpp2il -
    // Flag, SeqInfo.Current/Last, PUpSkillResult/Index/ID, skillcnt) on
    // EVERY call to rstUpdateSeqDestroyConfirm (Prefix and Postfix), for
    // BOTH the native-triggered and bridge-triggered episodes indifferently
    // - this class has no knowledge of FullCapacityAddNewBridgeState and
    // does not distinguish the two; that distinction is made afterward by
    // reading the logs alongside FULLCAP-ADDNEW-* markers already emitted
    // elsewhere.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDestroyConfirm))]
    internal static class DestroyConfirmEntryExitTrace
    {
        // COMPLETION-CHECK-GAP REPRODUCTION CHECK (re-enabled): the old
        // Queue-era archive (Section 7.4, master-archive.md) root-caused a
        // near-identical symptom to a "completion-check-gap" - native's own
        // fresh calc cycle (rstCalcSkillPowerUpCore entering at seq=9) began
        // in the SAME frame that rstUpdateSeqDestroyConfirm resolved to
        // seq=8/last=21, before the mod's own completion logic (at the time,
        // hooked at a coarser boundary) had a chance to run. The fix moved
        // completion-checking directly into rstUpdateSeqDestroyConfirm's own
        // Postfix. This class already captures exactly seqCurrent/seqLast/
        // frame at that exact native boundary - re-enabled, with bridge
        // Active/target visibility added, to check whether the CURRENT V3
        // bridge (whose own completion/restore logic runs on the coarser
        // rstupdate.rstUpdate() Postfix) is racing against the same gap.
        internal static readonly bool Enabled = true;

        private static int _invocationCounter;
        private static int _invocation;
        private static bool _captured;

        // Prefix-captured snapshot, compared against Postfix in the log
        // line itself.
        private static int _unitBefore;
        private static long _stockPtrBefore;
        private static int _skillCntBefore;
        private static sbyte _flagBefore;
        private static int _seqCurrentBefore;
        private static int _seqLastBefore;
        private static sbyte _seqChangeBefore;
        private static sbyte _defSkillResultBefore;
        private static ushort _eventParamBefore;
        private static sbyte _eventNumsBefore;
        private static sbyte _eventOfsBefore;
        private static sbyte _pUpResultBefore;
        private static sbyte _pUpIndexBefore;
        private static ushort _pUpIdBefore;
        private static int _candidateSlotIndexBefore;

        // HANDLEDSLOTS-IDENTITY CHECK: which array slot in stock.skill[]
        // currently holds the value equal to `skillId` (the candidate this
        // forget-confirm call is about, i.e. EventParam - e.g. 349). -1 if
        // not found (already removed, or not present this call). Read-only.
        private static int FindSkillSlot(Il2Cppnewdata_H.datUnitWork_t? stock, ushort skillId)
        {
            if (stock == null || stock.Pointer == IntPtr.Zero || skillId == 0) return -1;
            try
            {
                int cnt = stock.skillcnt;
                for (int i = 0; i < cnt && i < stock.skill.Length; i++)
                {
                    if (unchecked((ushort)stock.skill[i]) == skillId) return i;
                }
            }
            catch { /* fall through */ }
            return -1;
        }

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

                _invocation = ++_invocationCounter;
                _unitBefore = stock.id;
                _stockPtrBefore = stock.Pointer.ToInt64();
                _skillCntBefore = stock.skillcnt;
                _flagBefore = gbwk.Flag;
                _seqCurrentBefore = gbwk.SeqInfo.Current;
                _seqLastBefore = gbwk.SeqInfo.Last;
                _seqChangeBefore = gbwk.SeqInfo.Change;
                _defSkillResultBefore = gbwk.DefSkillResult;
                _eventParamBefore = gbwk.EventParam;
                _eventNumsBefore = gbwk.EventNums;
                _eventOfsBefore = gbwk.EventOfs;
                _pUpResultBefore = gbwk.PUpSkillResult;
                _pUpIndexBefore = gbwk.PUpSkillIndex;
                _pUpIdBefore = gbwk.PUpSkillID;
                _candidateSlotIndexBefore = FindSkillSlot(stock, _eventParamBefore);
                _captured = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DestroyConfirmEntryExitTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_captured) return;
            _captured = false;
            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                if (gbwk == null) return;

                int unitAfter = stock?.id ?? -1;
                long stockPtrAfter = stock?.Pointer.ToInt64() ?? 0;
                int skillCntAfter = stock?.skillcnt ?? -1;
                sbyte flagAfter = gbwk.Flag;
                int seqCurrentAfter = gbwk.SeqInfo.Current;
                int seqLastAfter = gbwk.SeqInfo.Last;
                sbyte seqChangeAfter = gbwk.SeqInfo.Change;
                sbyte defSkillResultAfter = gbwk.DefSkillResult;
                ushort eventParamAfter = gbwk.EventParam;
                sbyte eventNumsAfter = gbwk.EventNums;
                sbyte eventOfsAfter = gbwk.EventOfs;
                sbyte pUpResultAfter = gbwk.PUpSkillResult;
                sbyte pUpIndexAfter = gbwk.PUpSkillIndex;
                ushort pUpIdAfter = gbwk.PUpSkillID;
                int candidateSlotIndexAfter = FindSkillSlot(stock, eventParamAfter);

                // Only log calls where SOMETHING actually changed - this
                // method is called many times per frame while a dialog is
                // simply waiting for input (per the state-guard 0x182169960
                // this session's static analysis found gates most of its
                // body). Logging every single call would be enormous
                // volume for no benefit; a change is what matters here.
                bool changed =
                    _flagBefore != flagAfter ||
                    _seqCurrentBefore != seqCurrentAfter ||
                    _seqLastBefore != seqLastAfter ||
                    _seqChangeBefore != seqChangeAfter ||
                    _defSkillResultBefore != defSkillResultAfter ||
                    _eventParamBefore != eventParamAfter ||
                    _eventNumsBefore != eventNumsAfter ||
                    _eventOfsBefore != eventOfsAfter ||
                    _pUpResultBefore != pUpResultAfter ||
                    _pUpIndexBefore != pUpIndexAfter ||
                    _pUpIdBefore != pUpIdAfter ||
                    _skillCntBefore != skillCntAfter;
                if (!changed) return;

                int frame = UnityEngine.Time.frameCount;

                // COMPLETION-CHECK-GAP: bridge-side visibility. Read-only -
                // does not affect FullCapacityAddNewBridgeState in any way,
                // only observes it.
                bool bridgeActive = FullCapacityAddNewBridgeState.Active;
                int bridgeTarget = FullCapacityAddNewBridgeState.Target;
                bool targetPresent = false;
                if (bridgeActive && stock != null && stock.Pointer != IntPtr.Zero)
                {
                    try
                    {
                        int cnt = stock.skillcnt;
                        for (int i = 0; i < cnt && i < stock.skill.Length; i++)
                        {
                            if (unchecked((ushort)stock.skill[i]) == (ushort)bridgeTarget)
                            {
                                targetPresent = true;
                                break;
                            }
                        }
                    }
                    catch { /* leave targetPresent=false */ }
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] DESTROYCONFIRM-STATE-DIFF; " +
                    $"invocation={_invocation}; frame={frame}; unit={_unitBefore}; " +
                    $"stockPtr=0x{_stockPtrBefore:X}; " +
                    $"skillCnt {_skillCntBefore}->{skillCntAfter}; " +
                    $"flag {_flagBefore}->{flagAfter}; " +
                    $"seqCurrent {_seqCurrentBefore}->{seqCurrentAfter}; " +
                    $"seqLast {_seqLastBefore}->{seqLastAfter}; " +
                    $"seqChange {_seqChangeBefore}->{seqChangeAfter}; " +
                    $"defSkillResult {_defSkillResultBefore}->{defSkillResultAfter}; " +
                    $"eventParam {_eventParamBefore}->{eventParamAfter}; " +
                    $"eventNums {_eventNumsBefore}->{eventNumsAfter}; " +
                    $"eventOfs {_eventOfsBefore}->{eventOfsAfter}; " +
                    $"pUpResult {_pUpResultBefore}->{pUpResultAfter}; " +
                    $"pUpIndex {_pUpIndexBefore}->{pUpIndexAfter}; " +
                    $"pUpId {_pUpIdBefore}->{pUpIdAfter}; " +
                    $"candidateSlot {_candidateSlotIndexBefore}->{candidateSlotIndexAfter}; " +
                    $"bridgeActive={bridgeActive}; bridgeTarget={bridgeTarget}; targetPresent={targetPresent}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DestroyConfirmEntryExitTrace postfix failed safely: {ex.Message}");
            }
        }
    }
}
