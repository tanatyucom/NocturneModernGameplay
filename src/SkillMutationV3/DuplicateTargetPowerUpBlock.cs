using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // DUPLICATE POWER-UP TARGET BLOCK (mode-independent - Overwrite AND
    // AddNew both blocked).
    //
    // Background: real-machine testing this session found that when a
    // Skill Power-Up's target skill happens to already be owned by the
    // stock (e.g. source=ブフ(7), target=マハブフ(10), stock already has
    // マハブフ), native takes a special, different code path that produces
    // an EXTRA seq21/22 (forget-UI) episode. Initially scoped to AddNew
    // mode only, but per explicit design decision this is reclassified as
    // a general Power-Up CANDIDATE VALIDITY check, not an
    // Acquisition-mode-specific concern: if Overwrite mode can genuinely
    // produce a visible "duplicate skill" outcome (e.g. two マハブフ
    // entries) from the same root cause, native is not sufficiently
    // checking "is this target already owned" on its own, in EITHER mode.
    // So this guard now applies unconditionally: if the target is already
    // owned, this Power-Up attempt is simply unsuccessful (native's own
    // existing "no candidate" outcome), same as if RNG had produced no
    // valid candidate at all. No re-roll - see design discussion:
    // re-rolling introduces its own problems (0-candidate edge cases,
    // skewed probability distribution under Repeat=Unlimited).
    //
    // Intercept point: rstcalc.rstRndGetPowerUpSkill(datUnitWork_t pStock,
    // ref sbyte pIdx) : ushort has exactly ONE caller in the whole binary -
    // rstCalcSkillPowerUpCore itself (confirmed via whole-binary xref scan,
    // see SkillPowerUpChanceCandidateCapture's own comment) - and its
    // return value is written into GBWK.PUpSkillID by native's own code
    // (VA 0x18227E15F) immediately after this call returns. Overriding the
    // return value here, in this method's own Postfix, is therefore the
    // earliest possible point: no side effect (PUpSkillID write, Power-Up
    // presentation, forget-UI entry, FullCapacityAddNew bridge arming) has
    // happened yet. The override technique itself (Postfix(ref TResult
    // __result)) is the same one already shipped and working in
    // SkillPowerUpChanceAlwaysPatch (rstCalcSkillPowerUpCore's own Postfix).
    //
    // __result==0 is NOT a fabricated value - it is native's own existing
    // "no candidate / excluded" convention (see SkillPowerUpChanceAlwaysPatch's
    // comment: "__result==0: no candidate / excluded - never fabricated").
    // pIdx (the ref sbyte second parameter, the SOURCE skill's array index)
    // is deliberately left untouched - only __result is overridden.
    //
    // Mode-independent: applies whether Acquisition is Overwrite or AddNew.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstRndGetPowerUpSkill))]
    internal static class DuplicateTargetPowerUpBlock
    {
        internal static readonly bool Enabled = true;

        private static void Postfix(Il2Cppnewdata_H.datUnitWork_t __0, ref ushort __result)
        {
            if (!Enabled) return;
            if (__result == 0) return; // already "no candidate" - nothing to do

            try
            {
                if (__0 == null || __0.Pointer == IntPtr.Zero) return;

                int cnt = __0.skillcnt;
                var ownedArray = __0.skill;
                if (ownedArray == null) return;
                int limit = Math.Min(cnt, ownedArray.Length);

                for (int i = 0; i < limit; i++)
                {
                    if (unchecked((ushort)ownedArray[i]) != __result) continue;

                    int frame = UnityEngine.Time.frameCount;
                    string mode = FullCapacityAddNewBridgeTrigger.Enabled ? "AddNew" : "Overwrite";
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] DUPLICATE-PU-TARGET-BLOCK; " +
                        $"frame={frame}; unit={__0.id}; stockPtr=0x{__0.Pointer.ToInt64():X}; " +
                        $"target={SkillNameResolver.Format(__result)}; mode={mode}; " +
                        "action=BLOCK-AS-NO-CANDIDATE.");

                    __result = 0;
                    return;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DuplicateTargetPowerUpBlock postfix failed safely: {ex.Message}");
            }
        }
    }
}
