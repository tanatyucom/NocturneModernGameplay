using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Repeat=Unlimited investigation, R0-B targeted reproduction diagnostic
    // (investigations/REPEAT_UNLIMITED/PLAN.md "R0-Bを意図的に再現するための
    // 調査"). Read-only observer only - does not implement Option F result
    // conversion, does not write __result/bit6/any native or game state.
    //
    // Purpose: for every rstcalc.rstRndGetPowerUpSkill call (VA 0x1822810B0,
    // real body 0x1965489A0 - the function Core calls to pick this level-up's
    // Power-Up candidate), enumerate the SAME stock's owned skill list the
    // native function is about to scan, and independently compute each
    // owned skill's power-up target via rstCalcCore.cmbGetPowerUpSkill
    // (ushort cmbGetPowerUpSkill(ushort skillId), managed signature
    // confirmed this session via raw metadata signature bytes). This lets a
    // human directly see the full eligible-candidate pool for a unit (not
    // just whichever one RNG happens to pick), and check e.g. whether a
    // specific unit currently owns skill 43 (whose power-up target, 44, was
    // observed on unit 59's exclusion list in an earlier session - see
    // PLAN.md "intersection").
    //
    // Why calling cmbGetPowerUpSkill here is safe: this session's full
    // disassembly of its real body (VA 0x196539430) found it is a pure
    // linear search over tblSkillPowerUp.fclSkillPowerUpTbl (itself fully
    // extracted this session - 55 literal (source,target) pairs baked into
    // tblSkillPowerUp's .cctor, VA 0x1826A9020) with no writes anywhere and
    // no RNG call - CONFIRMED static, recorded in PLAN.md. Calling it
    // read-only, purely to inspect its return value for already-owned
    // skills, introduces no side effect and no state mutation of any kind.
    //
    // stock.id / stock.skillcnt / stock.skill / stock.level / stock.hensinmae
    // are all real managed properties (confirmed accessible this session via
    // interop-assembly metadata inspection: get_id/get_skillcnt/get_skill/
    // get_level/get_hensinmae all exist), not a new raw pointer chain.
    // level/hensinmae are declared on the base class
    // Il2Cppnewdata_H.datUnitWork_s (datUnitWork_t : datUnitWork_s), same as
    // id/skillcnt/skill/flag - accessible directly on the datUnitWork_t
    // stock reference via inheritance.
    // stock.skill elements are Int32 but only the low 16 bits are ever a
    // real skill ID (native reads them via a 16-bit "movzx ... word ptr"
    // - confirmed in rstRndGetPowerUpSkill's own disassembly); skillID==0
    // is the native empty-slot sentinel and is skipped here, matching
    // native's own "候補なし" convention documented elsewhere in this file.
    //
    // Not unit-specific: applies identically to every stock this hook
    // observes, no unit==59/60 (or any other) special-casing.
    //
    // Core correlation: rstRndGetPowerUpSkill has exactly one confirmed
    // call site, inside rstCalcSkillPowerUpCore's own synchronous native
    // body (VA 0x18227E15A) - i.e. any call reaching this Prefix while
    // OptionFF2CoreDiagnostics.CoreActive is true is guaranteed (by
    // single-threaded synchronous nesting, not frame-proximity guessing)
    // to belong to that exact Core invocation, so this diagnostic tags its
    // log line with OptionFF2CoreDiagnostics.CurrentInvocationId whenever
    // CoreActive is true. A call observed with CoreActive==false (a
    // different/unknown caller, not verified against a full xref scan this
    // session) is still logged, explicitly marked coreActive=False and
    // invocation=N/A, rather than silently dropped - the pool data itself
    // is useful either way, and hiding it would just recreate a different
    // scoping ambiguity than the one already found and fixed for
    // OptionFF2ExclusionListObserver.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstRndGetPowerUpSkill))]
    internal static class OptionFCandidatePoolDiagnostics
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why.
        internal static readonly bool Enabled = false;

        // Positional original-argument binding (Harmony "__N" convention):
        // the interop assembly's real parameter name for this native-heavy
        // method's first argument is not confirmed, so binding by position
        // (1st parameter, 0-indexed = __0) is the reliable option. Managed
        // signature confirmed this session via raw metadata signature
        // bytes: ushort rstRndGetPowerUpSkill(datUnitWork_t, ref sbyte).
        private static void Prefix(Il2Cppnewdata_H.datUnitWork_t __0)
        {
            if (!Enabled) return;
            try
            {
                var stock = __0;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                int unitId = stock.id;
                int level = stock.level;
                int hensinmae = stock.hensinmae;
                int skillCnt = stock.skillcnt;
                var ownedArray = stock.skill;
                if (ownedArray == null) return;
                int count = Math.Max(0, Math.Min(skillCnt, ownedArray.Length));

                var ownedSkills = new List<int>();
                var powerUpPairs = new List<string>();
                var nonCandidateOwnedSkills = new List<int>();
                // Named (display-name) representations, additive only -
                // built in the same pass so cmbGetPowerUpSkill is never
                // called a second time for the same skill ID. Every
                // existing raw numeric field above is unchanged. See
                // SkillNameResolver.cs for the resolver itself.
                var ownedSkillsNamed = new List<string>();
                var powerUpPairsNamed = new List<string>();

                for (int i = 0; i < count; i++)
                {
                    ushort skillId = (ushort)(ownedArray[i] & 0xFFFF);
                    if (skillId == 0) continue; // empty slot sentinel, matches native's own convention
                    ownedSkills.Add(skillId);
                    ownedSkillsNamed.Add(SkillNameResolver.Format(skillId));

                    ushort target = rstCalcCore.cmbGetPowerUpSkill(skillId);
                    if (target != 0)
                    {
                        powerUpPairs.Add($"{skillId}->{target}");
                        powerUpPairsNamed.Add(SkillNameResolver.FormatPair(skillId, target));
                    }
                    else
                        nonCandidateOwnedSkills.Add(skillId);
                }

                bool coreActive = OptionFF2CoreDiagnostics.CoreActive;
                string invocationText = coreActive
                    ? OptionFF2CoreDiagnostics.CurrentInvocationId.ToString()
                    : "N/A";

                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-OPTIONF-CANDIDATE-POOL; " +
                    $"invocation={invocationText}; coreActive={coreActive}; unit={unitId}; " +
                    $"level={level}; hensinmae={hensinmae}; " +
                    $"stockPtr=0x{stock.Pointer.ToInt64():X}; " +
                    $"ownedSkills=[{string.Join(",", ownedSkills)}]; " +
                    $"ownedSkillsNamed=[{string.Join(",", ownedSkillsNamed)}]; " +
                    $"powerUpPairs=[{string.Join(",", powerUpPairs)}]; " +
                    $"powerUpPairsNamed=[{string.Join(",", powerUpPairsNamed)}]; " +
                    $"candidateCount={powerUpPairs.Count}; " +
                    $"nonCandidateOwnedSkills=[{string.Join(",", nonCandidateOwnedSkills)}].");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] OptionFCandidatePoolDiagnostics prefix failed safely: {ex.Message}");
            }
        }
    }
}
