using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // TEMPORARY DIAGNOSTIC HELPER - native bit6 CLEAR investigation aid
    // only. NOT an official Gameplay feature: not registered with
    // GameplayFeatureRegistry / GameplaySettingsService / the GUI, no
    // settings.json schema entry. Multiplies the final per-party-slot
    // battle EXP award so a human can reach repeated, reload-free
    // level-ups fast enough to observe rstUpdateSeqSkillPowerUp's native
    // bit6 CLEAR mechanism without long grinding sessions. Delete this
    // file (and only this file) once that investigation's runtime testing
    // is done.
    //
    // Control point: nbResultProcess.GetAddExp(Int32 stock, Int32 exp) -
    // VA 0x182395CB0 (thunk) -> 0x1966E9160 (real body; this session's
    // disassembly, .analysis/disasm_getaddexp.py). Confirmed call site:
    // nbResultProcess.nbResultAddExp() (VA 0x182396710 thunk ->
    // 0x1966F0620 real body, .analysis/disasm_nbresultaddexp.py) calls
    // nbGetKakutokuExp(data) once per party slot (loop ebp=0..15, call at
    // VA 0x1966F075A), applies per-unit modifiers (an EXP-boost skill
    // check plus an elemental/race-affinity check), then calls
    // GetAddExp(stock, modifiedExp) at VA 0x1966F08AD and feeds its return
    // value directly into the next call (VA 0x1966F08DA) that grants it to
    // that unit. GetAddExp's own body (0x1966E9160) is a pure clamp -
    // result = min(exp, max(perSlotCap - alreadyGranted, 0)) - it never
    // touches Macca, items, level, stats, or skill acquisition. Postfix-
    // multiplying its return value is the last point before the per-unit
    // grant where only the EXP number itself is in play.
    //
    // Scope caveat: only this one confirmed call site was traced (a full
    // whole-binary xref scan of every GetAddExp caller was not performed,
    // time-boxed for this temporary diagnostic) - if another caller exists
    // the multiplier would apply there too. Acceptable for a temporary,
    // human-supervised diagnostic build; called out here rather than
    // silently assumed away.
    [HarmonyPatch(typeof(nbResultProcess), nameof(nbResultProcess.GetAddExp))]
    internal static class ExperienceMultiplierDiagnostics
    {
        internal static readonly bool Enabled = true;
        internal const int Multiplier = 1;

        private static void Postfix(int stock, int exp, ref int __result)
        {
            if (!Enabled) return;
            try
            {
                int nativeExp = __result;
                int multipliedExp = MultiplySafely(nativeExp, Multiplier);
                __result = multipliedExp;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] EXP-MULTIPLIER; " +
                    $"stock={stock} nativeExp={nativeExp} multipliedExp={multipliedExp} multiplier={Multiplier}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] ExperienceMultiplierDiagnostics postfix failed safely: {ex.Message}");
            }
        }

        // Zero/negative pass through unchanged (nothing meaningful to
        // scale); positive values are scaled and clamped to int.MaxValue
        // instead of wrapping on overflow.
        private static int MultiplySafely(int nativeExp, int multiplier)
        {
            if (nativeExp <= 0) return nativeExp;
            long scaled = (long)nativeExp * multiplier;
            return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
        }
    }
}
