using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - presentation candidate injection.
    //
    // Confirmed working end-to-end on real hardware (2026-09-15, see
    // investigations/HIDDEN_SKILL_ENTRY/PLAN.md "CONFIRMED - 実機テストで
    // PoC成功"): Frost's hidden entry now shows name/icon/highlight, and
    // completing it correctly teaches the pending target
    // (FULLCAP-ADDNEW-COMPLETE; targetPresent=True; sourcePreserved=True).
    // High Pixie's pre-existing native-driven success path was re-verified
    // unaffected in the same session. Promoted out of PoC naming; the file
    // still carries "Poc"-era reasoning in these comments because the
    // underlying mechanism has not changed, only its name and log volume.
    //
    // Background: the AddNew bridge's "which skill are you giving up?"
    // screen has a 3rd, always-present column showing whatever curriculum
    // skill is still pending to be taught this level-up - driven entirely by
    // native (`rstcalc.rstCreateBeforeSkillList` builds a small candidate
    // list into an `rstSkillInfo_t` object every frame; `cmpDrawStatus.
    // cmpDrawSkill` reads it back and, when its own logical cursor lands on
    // slot 8, presents candidate index 0 - highlight, icon, and text all
    // handled by native). This works correctly for a TRANSFORMED demon (e.g.
    // High Pixie, `datUnitWork_t.hensinmae != 0`) because native's own
    // curriculum scan finds real upcoming-skill candidates for it. For an
    // ORDINARY demon (`hensinmae == 0`, e.g. Frost) native's scan
    // structurally never finds any (CONFIRMED runtime, `CurriculumGateChainTrace`:
    // every one of Frost's 24 scanned curriculum slots has a level threshold
    // at or below its current level, so gate2 rejects all of them), so
    // `rstSkillInfo_t.SkillCnt` stays 0 and `cmpDrawSkill`'s entire
    // awaitObj/await2Obj/target==8 presentation block is skipped at its very
    // first check (`SkillCnt<=0` acts as a master gate, not just an array
    // bound - CONFIRMED via full disassembly of cmpDrawSkill, 1663
    // instructions, 0x1822D97C0-0x1822DB3A0). This is not a Frost-specific
    // bug; High Pixie's success was itself incidental (it happens to be a
    // transformed demon that still has real curriculum headroom). `hensinmae`
    // is demon-specific save data and is NOT touched by this class or any
    // other part of this mod.
    //
    // outList (`rstSkillInfo_t`) structure, CONFIRMED by cross-referencing
    // the byte-exact disassembly of both `rstCreateBeforeSkillList` (writer)
    // and the FULL body of `cmpDrawSkill` (every one of its 13 references to
    // the object, catalogued) against this project's own cpp2il dump
    // (.analysis/cpp2il_cs/DiffableCs/Assembly-CSharp/result2_H/
    // rstSkillInfo_t.cs):
    //   SkillCnt     (sbyte,   +0x10) - candidate count. MASTER GATE for
    //                the whole target==8 presentation block in cmpDrawSkill,
    //                not merely an array bound.
    //   TargetLevel  (byte[],  +0x18) - per-candidate level/type byte.
    //                CONFIRMED UNREAD anywhere in cmpDrawSkill's full body
    //                (0 of 13 r13-references touch +0x18) - locked at a
    //                placeholder value for THIS screen (see
    //                PlaceholderTargetLevel below). (Caveat: only
    //                cmpDrawSkill was checked; if some other, currently
    //                unidentified consumer also reads this object, that
    //                consumer's use of TargetLevel remains unverified - no
    //                such consumer has turned up in any of this
    //                investigation's disassembly work.)
    //   SkillID      (ushort[],+0x20) - per-candidate skill ID. The ONLY
    //                outList sub-array cmpDrawSkill actually reads (4 of 13
    //                references). The target==8 special block always reads
    //                index 0 specifically (fixed `[rax+0x20]`, no index
    //                multiplier) - so only SkillID[0] matters for this
    //                screen, regardless of SkillCnt.
    // Both TargetLevel and SkillID are CONFIRMED (runtime, `loopBound`/
    // `byteArrayLen`/`wordArrayLen` in CurriculumGateChainTrace's log,
    // retired - see ModMain.cs) to already be non-null, fully-allocated
    // (Length=24, both units, every sample, INCLUDING Frost's SkillCnt=0
    // frames) arrays on the SAME outList object native fetches fresh from a
    // shared slot every frame - no new IL2CPP array allocation is required
    // to write into them.
    //
    // Native's own append order (CONFIRMED byte-exact, single call site,
    // `rstCreateBeforeSkillList` VA 0x1822805EB/0x1822805FC/0x182280611):
    //   TargetLevel[count] = <level byte>
    //   SkillCnt = count + 1
    //   SkillID[oldCount] = <skill id>
    // This class deliberately does NOT mirror that exact order. Native gets
    // away with "count in the middle" because nothing else runs between its
    // three instructions on the same thread. This Postfix has no such
    // guarantee once other managed code exists in the same frame, so it
    // publishes SkillCnt LAST instead - element data is fully written before
    // the master gate is opened, so no other reader of this object can ever
    // observe SkillCnt>0 with a not-yet-written SkillID[0].
    //
    // Self-cleaning by construction, no separate teardown needed: every
    // single call to rstCreateBeforeSkillList (CONFIRMED runtime, writer1,
    // 100% of samples both units) unconditionally resets SkillCnt=0 before
    // its own candidate scan runs. Since this class only ever runs AFTER
    // that native reset+scan has already completed (it is a Postfix on the
    // whole method) and only injects while FullCapacityAddNewBridgeState.
    // Active is true, an injected SkillCnt=1 never survives into a frame
    // where the guard conditions no longer hold - native's own next call
    // wipes it back to a fresh 0 before this Postfix runs again and
    // re-evaluates the guard.
    //
    // Deliberately a SEPARATE file from AddNewHighlightCorrection.cs: that
    // class corrects a DIFFERENT, already-shipped highlight-index quirk;
    // this one supplies a native presentation path with data it is
    // structurally missing. Different problem, different native function,
    // different risk profile - keeping them apart makes each easier to
    // reason about and to disable independently.
    //
    // Positional binding (Harmony "__N" convention), matching the existing
    // OptionFRepeatUnlimitedExclusionObserver patch on this SAME native
    // method: the interop assembly's real parameter names for this
    // native-heavy method are not confirmed, so binding by position (2nd
    // parameter = pStock = __1, 4th parameter = pInfo = __3, both
    // 0-indexed) is the reliable option. Harmony applies both this patch and
    // OptionFRepeatUnlimitedExclusionObserver's independently - they do not
    // conflict.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCreateBeforeSkillList))]
    internal static class HiddenSlotCandidateInjection
    {
        internal static readonly bool Enabled = true;

        private const int ForgetSelectSeq = 21;

        // LOCKED (2026-09-15, production): CONFIRMED unread by cmpDrawSkill
        // (see class comment) for the entire duration of the forget/select
        // screen this class targets. 0 chosen with no further significance
        // intended - any value would be equally inert for THIS screen; kept
        // as a named constant rather than an inline literal so a future
        // consumer discovery (see caveat above) has one place to fix.
        private const byte PlaceholderTargetLevel = 0;

        // Log-on-change only (this mod's established discipline - see e.g.
        // FullCapacityAddNewBridgeMonitor.RunPostCompleteWatch): this
        // Postfix runs on every rstCreateBeforeSkillList call while the
        // guard holds, which is every frame of the forget/select screen
        // (measured: ~500+ calls for a few seconds of player dwell time on
        // the hidden entry). Without this gate the injection would be
        // confirmed correct but log at that same volume, which is not
        // useful signal past the first occurrence per episode.
        private static int _lastLoggedUnit = -1;
        private static int _lastLoggedTarget = -1;

        private static void Postfix(Il2Cppnewdata_H.datUnitWork_t __1, Il2Cppresult2_H.rstSkillInfo_t __3)
        {
            var pStock = __1;
            var pInfo = __3;
            if (!Enabled) return;
            try
            {
                // Recognizes EITHER bridge's active episode (2026-09-15,
                // User-directed minimal additive change for Mutation
                // AddNew - candidate B). The two are mutually exclusive by
                // construction (see MutationFullCapacityAddNewBridgePoc.cs's
                // own comment), so at most one of these is ever true.
                int target;
                int watchedUnit;
                if (FullCapacityAddNewBridgeState.Active)
                {
                    target = FullCapacityAddNewBridgeState.Target;
                    watchedUnit = FullCapacityAddNewBridgeState.WatchedUnit;
                }
                else if (MutationAddNewBridgeState.Active)
                {
                    target = MutationAddNewBridgeState.Target;
                    watchedUnit = MutationAddNewBridgeState.WatchedUnit;
                }
                else
                {
                    _lastLoggedUnit = -1;
                    _lastLoggedTarget = -1;
                    return;
                }

                var gbwk = rstinit.GBWK;
                if (gbwk == null) return;
                if (gbwk.SeqInfo.Current != ForgetSelectSeq) return;

                if (target == 0) return;

                if (pStock == null || pStock.Pointer == IntPtr.Zero) return;
                if (pStock.id != watchedUnit) return;

                if (pInfo == null) return;

                // OVERRIDE FIX (2026-09-15, User-reported: Mutation AddNew
                // on a transformed demon - hensinmae != 0, e.g. High Pixie -
                // showed the wrong skill highlighted in this slot; the
                // player's genuinely pending curriculum skill appeared
                // instead of the AddNew bridge's own target). Root cause:
                // the ORIGINAL guard here (`if (pInfo.SkillCnt != 0)
                // return`) was written for the pre-bridge Hidden Skill
                // Entry fix, where the goal was narrowly "supply data only
                // when native found none at all" - it never anticipated a
                // caller (this same class, now also serving both AddNew
                // bridges) that needs its OWN target to always win this
                // slot while a bridge episode is in progress, regardless of
                // whether native's own curriculum scan ALSO happened to
                // find something (true for any hensinmae!=0 unit, per the
                // class comment above). cmpDrawSkill's target==8 block
                // always reads SkillID[0] specifically (CONFIRMED, see
                // class comment) - so overwriting index 0 unconditionally,
                // while a bridge is active, is sufficient and safe: native's
                // own SkillCnt (if already >=1) is left untouched (only
                // forced up to 1 if native left it at 0), so no other
                // unidentified consumer of SkillCnt observes a value it
                // would not have produced on its own.
                var targetLevelArray = pInfo.TargetLevel;
                var skillIdArray = pInfo.SkillID;
                if (targetLevelArray == null || skillIdArray == null) return;
                if (targetLevelArray.Length < 1 || skillIdArray.Length < 1) return;

                // Element data fully written BEFORE the master gate
                // (SkillCnt) opens - see class comment on publish order.
                targetLevelArray[0] = PlaceholderTargetLevel;
                skillIdArray[0] = (ushort)target;
                if (pInfo.SkillCnt < 1) pInfo.SkillCnt = 1;

                if (pStock.id != _lastLoggedUnit || target != _lastLoggedTarget)
                {
                    _lastLoggedUnit = pStock.id;
                    _lastLoggedTarget = target;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] HIDDENSLOT-INJECT; " +
                        $"frame={UnityEngine.Time.frameCount}; unit={pStock.id}; " +
                        $"target={SkillNameResolver.Format(target)}; " +
                        $"targetLevelLen={targetLevelArray.Length}; skillIdLen={skillIdArray.Length}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HiddenSlotCandidateInjection postfix failed safely: {ex.Message}");
            }
        }
    }
}
