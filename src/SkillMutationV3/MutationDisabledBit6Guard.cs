using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Corrects a pre-existing gap in SkillMutation.Chance = Disabled
    // (SkillMutationChanceControl's DEntry1/DEntry2 raw patches), found via
    // static disassembly this session (.analysis/disasm_mutation_disabled_
    // bit6.py): rstCalcSkillPowerUpCore's RNG-bypass route (native "dil=1",
    // set at VA 0x18227E344, jumping directly to the merge block at
    // 0x18227E4BC) skips the bit6 test at 0x18227E40C entirely. With
    // DEntry2 (VA 0x18227E4BF) NOPed by Mutation.Disabled, this route falls
    // straight into the ordinary Power-Up success tail (0x18227E4C1:
    // bit6|=0x40; return 1) regardless of bit6's actual state - so a unit
    // whose bit6 was already SET (Repeat=Native: "already power-upped this
    // cycle") can still have Core report result=1 whenever native's own
    // unmodified dil-roll happens to select the bypass route. This is
    // independent of SkillPowerUp.Chance's value.
    //
    // Evidence status (per investigation discipline): the CFG path itself
    // is CONFIRMED via static disassembly. That this actually happens at
    // runtime (a genuine dil=1 roll landing on an already-bit6-SET unit)
    // has not yet been directly observed - this class exists specifically
    // to make that observable (and to correct it) without waiting for
    // static-analysis-only confidence to become a runtime blocker.
    //
    // Scope discipline: this class ONLY ever assigns to Postfix's own
    // `__result` parameter. It never writes PUpSkillID, PUpSkillIndex,
    // pCurrentStock, any skill array element, stock.flag, or SeqInfo -
    // Core's own flag|=0x40 write already happened by the time this Postfix
    // runs; since bit6 was already SET before Core ran, that OR is a no-op
    // on the bit itself, so there is nothing to revert there.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class MutationDisabledBit6Guard
    {
        private static bool _captured;
        private static bool _bit6WasSetBeforeCore;
        private static int _capturedUnit = -1;

        private static void Prefix()
        {
            _captured = false;
            try
            {
                var stock = rstinit.GBWK?.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                // Prefix-time capture only, per design: Core's own body
                // performs "flag |= 0x40" on the ordinary-success path, so
                // reading bit6 in Postfix could no longer distinguish "was
                // already SET before this call" from "just SET by this
                // call" - only the Prefix-time value answers the question
                // this guard exists to answer.
                _bit6WasSetBeforeCore = (stock.flag & 0x40) != 0;
                _capturedUnit = stock.id;
                _captured = true;
            }
            catch (Exception ex)
            {
                _captured = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MutationDisabledBit6Guard prefix capture failed safely: {ex.Message}");
            }
        }

        private static void Postfix(ref sbyte __result)
        {
            if (!_captured) return;
            _captured = false; // consume once per Prefix/Postfix pair

            if (SkillMutationChanceControl.Mode != NativeChanceMode.Disabled) return;

            bool corrected = false;
            sbyte originalResult = __result;
            if (_bit6WasSetBeforeCore && __result == 1)
            {
                __result = 0;
                corrected = true;
            }

            if (PowerUpMutationCfgDiagnostics.Enabled &&
                PowerUpMutationCfgDiagnostics.IsTargetUnit(_capturedUnit))
            {
                MelonLogger.Msg(
                    "[NocturneModernGameplay] MutationDisabledBit6Guard; " +
                    $"unit={_capturedUnit} bit6WasSetBeforeCore={_bit6WasSetBeforeCore} " +
                    $"originalResult={originalResult} correctedResult={__result} corrected={corrected}.");
            }
        }
    }
}
