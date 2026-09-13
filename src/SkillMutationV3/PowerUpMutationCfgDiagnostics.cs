using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Read-only investigation telemetry for the Power-Up vs genuine Mutation
    // native branch inside rstcalc.rstCalcSkillPowerUpCore, per this
    // session's zero-base CFG re-derivation (cpp2il ISIL dump +
    // global-metadata.dat method resolution + direct disassembly -
    // independent of the old Patch A/B/C assumptions and of the old
    // Queue/Inline investigation).
    //
    // Design constraints (explicit instruction, this session):
    //   - No SeqInfo.Current==8 hard filter anywhere in this file - only a
    //     target-unit filter. This session's static xref scan found TWO
    //     independent call sites to rstCalcSkillPowerUpCore inside rstCalc
    //     (VA 0x18227E9C9 and 0x18227F0A1); which SeqInfo.Current /
    //     rstcalc.EventStart value each corresponds to is still open, so
    //     both are logged as raw values below instead of being filtered on.
    //   - pCurrentStock.flag bit6 is read from GBWK.pCurrentStock, NOT
    //     GBWK.WorkStock. The prior session's V3-GATE3B-BIT6 telemetry
    //     (SkillMutationV3.cs) read WorkStock.flag; this session's fresh
    //     disassembly of rstCalcSkillPowerUpCore confirmed the byte actually
    //     tested at VA 0x18227e40c ("test byte ptr [rax+0x10], 0x40") is
    //     pCurrentStock+0x10, i.e. datUnitWork_s(pCurrentStock).flag - a
    //     different object than what the old telemetry read.
    //   - Never writes __result, never writes any ref parameter, never
    //     touches Patch A/B/C bytes or any other native memory. Pure
    //     Prefix/Postfix observation of already-managed methods only - no
    //     raw/inline patch, no gameplay behavior change.
    //   - cmbGetMutationSkill telemetry ("D" in this investigation's
    //     design) already exists as MutationCandidateDiagnostics.cs
    //     (MUTATION-VALID-CANDIDATES, logs original skillID + nativeResult)
    //     and is reused unchanged - this file does not touch it.
    internal static class PowerUpMutationCfgDiagnostics
    {
        // Default OFF for the official [SkillMutation]/[SkillPowerUp]
        // Chance build - this investigation's telemetry (including
        // PowerUpMutationBit6RawProbe's hardware breakpoints) is
        // independent of the Chance control points and must never be a
        // prerequisite for them. Flip to true only for a deliberate,
        // explicit real-machine investigation session.
        //
        // Was temporarily set to false for one session so
        // SkillCntWriterCaptureProbe could use Dr0/Dr1 without conflicting
        // with this probe's four execute breakpoints (Dr0-Dr3). That
        // investigation (empty-slot AND full-capacity ownership-write
        // instructions, both CONFIRMED) is done and
        // SkillCntWriterCaptureProbe.Enabled is now false, so this is
        // restored to its normal value. Never enable both probes at once -
        // they share the same 4 hardware debug registers.
        //
        // Restored to true - EventParamWriterCaptureProbe caused a hard
        // crash near a high-load scene transition (Hi-Pixie battle entry)
        // and has been disabled (see its own Enabled comment). Do not
        // disable this again for that probe until its arm window is
        // narrowed and the crash cause is understood.
        //
        // HI-PIXIE STALL HEALTH CHECK: temporarily false anyway, alongside
        // every other observer in this mod - this file's own header
        // explicitly documents it as independent of [SkillMutation]/
        // [SkillPowerUp] Chance behavior, so disabling it does not affect
        // the AddNew bridge or Chance=Always functionality under test.
        internal static bool Enabled = false;

        private const int TargetUnitA = 59;
        private const int TargetUnitB = 60;

        internal static bool IsTargetUnit(int unit) => unit == TargetUnitA || unit == TargetUnitB;
    }

    // A: rstcalc.rstCalcSkillPowerUpCore boundary. Its sbyte return value
    // (0/1/2/3) is the single write source for GBWK.PUpSkillResult (written
    // verbatim by the caller, rstCalc, immediately after this call returns -
    // CONFIRMED this session via disassembly of both call sites). Coexists
    // with the pre-existing V3SkillPowerUpCoreBoundaryPatch (a separate
    // Harmony patch class targeting the same method, separate log tag
    // V3-SKILLPOWERUP-CORE, which this file does not modify - Harmony
    // supports multiple independent patches on one target method).
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class V3CfgCoreBoundaryPatch
    {
        // Correlation key for PowerUpMutationBit6RawProbe (raw hardware-
        // breakpoint instrumentation at VA 0x18227E40C, the bit6 test
        // inside this same native call). Incremented once per logged
        // invocation of this method's Prefix (i.e. once per Core call for
        // a target unit); the raw probe reads the current value at the
        // moment its breakpoint fires so a single Core call's Prefix / raw
        // bit6-test / Postfix observations can be tied together in the log.
        // Never written to by anything outside this class; read-only from
        // the raw probe's perspective.
        private static long _invocationCounter;
        internal static long LastPrefixInvocationId { get; private set; } = -1;
        internal static IntPtr LastPrefixPCurrentStockPtr { get; private set; } = IntPtr.Zero;

        private static string Snapshot(string phase, int frame)
        {
            var gbwk = rstinit.GBWK;
            int eventStart = rstcalc.EventStart;
            int seqCurrent = gbwk?.SeqInfo.Current ?? -1;

            Il2Cppnewdata_H.datUnitWork_t? stock = null;
            try { stock = gbwk?.pCurrentStock; }
            catch { /* leave null - reported below */ }

            IntPtr stockPtr = stock?.Pointer ?? IntPtr.Zero;
            int unit = (stock != null && stockPtr != IntPtr.Zero) ? stock.id : -1;

            string fields;
            if (stock == null || stockPtr == IntPtr.Zero)
            {
                fields = "pCurrentStockPtr=NULL pCurrentStockFlagRaw=NULL pCurrentStockBit6=NULL pCurrentStockId=NULL";
            }
            else
            {
                uint flagRaw = stock.flag;
                bool bit6 = (flagRaw & 0x40) != 0;
                fields =
                    $"pCurrentStockPtr=0x{stockPtr.ToInt64():X} pCurrentStockFlagRaw=0x{flagRaw:X} " +
                    $"pCurrentStockBit6={bit6} pCurrentStockId={stock.id}";
            }

            int pUpSkillIndex = gbwk?.PUpSkillIndex ?? -1;
            int pUpSkillID = gbwk?.PUpSkillID ?? -1;

            return
                $"phase={phase} frame={frame} eventStart={eventStart} seqCurrent={seqCurrent} unit={unit} " +
                $"{fields} pUpSkillIndex={pUpSkillIndex} pUpSkillID={pUpSkillID}";
        }

        private static bool ShouldLog()
        {
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return false;
                int unit = gbwk.pCurrentStock?.id ?? -1;
                return PowerUpMutationCfgDiagnostics.IsTargetUnit(unit);
            }
            catch
            {
                return false;
            }
        }

        private static void Prefix()
        {
            if (!PowerUpMutationCfgDiagnostics.Enabled || !ShouldLog()) return;
            try
            {
                long invocationId = ++_invocationCounter;
                LastPrefixInvocationId = invocationId;
                try { LastPrefixPCurrentStockPtr = rstinit.GBWK?.pCurrentStock?.Pointer ?? IntPtr.Zero; }
                catch { LastPrefixPCurrentStockPtr = IntPtr.Zero; }

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-CFG-CORE; " + Snapshot("prefix", frame) +
                    $" invocationId={invocationId}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] V3-CFG-CORE prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix(sbyte __result)
        {
            if (!PowerUpMutationCfgDiagnostics.Enabled || !ShouldLog()) return;
            try
            {
                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-CFG-CORE; " + Snapshot("postfix", frame) +
                    $" coreResult={__result} invocationId={LastPrefixInvocationId}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] V3-CFG-CORE postfix failed safely: {ex.Message}");
            }
        }
    }

    // B: rstcalc.rstRndGetPowerUpSkill boundary (Postfix only - its Prefix
    // has nothing new to observe beyond what A's Prefix already captures via
    // GBWK). This session's whole-binary call/jmp xref scan found exactly
    // one caller (rstCalcSkillPowerUpCore), so no cross-caller filter is
    // needed beyond the unit check.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstRndGetPowerUpSkill))]
    internal static class V3CfgRndPowerUpSkillPatch
    {
        private static void Postfix(
            Il2Cppnewdata_H.datUnitWork_t __0, ref SByte __1, ushort __result)
        {
            if (!PowerUpMutationCfgDiagnostics.Enabled) return;
            try
            {
                IntPtr argStockPtr = __0?.Pointer ?? IntPtr.Zero;
                int unit = (__0 != null && argStockPtr != IntPtr.Zero) ? __0.id : -1;
                if (!PowerUpMutationCfgDiagnostics.IsTargetUnit(unit)) return;

                int frame = UnityEngine.Time.frameCount;
                var gbwk = rstinit.GBWK;
                int eventStart = rstcalc.EventStart;
                int seqCurrent = gbwk?.SeqInfo.Current ?? -1;
                IntPtr gbwkStockPtr = gbwk?.pCurrentStock?.Pointer ?? IntPtr.Zero;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-CFG-RNDPOWERUP; " +
                    $"phase=postfix frame={frame} eventStart={eventStart} seqCurrent={seqCurrent} unit={unit} " +
                    $"argStockPtr=0x{argStockPtr.ToInt64():X} gbwkStockPtr=0x{gbwkStockPtr.ToInt64():X} " +
                    $"result={__result} pIdx={__1}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] V3-CFG-RNDPOWERUP postfix failed safely: {ex.Message}");
            }
        }
    }

    // C: fclCombineCalcCore.cmbChkSkillOwner boundary (Prefix + Postfix).
    // This function has multiple call sites in real gameplay code: the
    // "promotion check" inside rstCalcSkillPowerUpCore (VA 0x18227E3D2,
    // what this investigation cares about) AND several inside
    // rstCreateBeforeSkillList's exclusion-list construction (different
    // skillIDs - the unit's existing skill-slot contents, not the
    // RNG-picked PUpSkillID). isPromotionCandidate below is the designed
    // correlation key: true only when this call's SkillID argument equals
    // GBWK.PUpSkillID at the moment of the call, which is exactly what the
    // promotion-check call site passes per this session's CFG trace.
    [HarmonyPatch(typeof(fclCombineCalcCore), nameof(fclCombineCalcCore.cmbChkSkillOwner))]
    internal static class V3CfgSkillOwnerPatch
    {
        private static string Snapshot(
            string phase, int frame, ushort skillId, Il2Cppnewdata_H.datUnitWork_t? argStock)
        {
            IntPtr argStockPtr = argStock?.Pointer ?? IntPtr.Zero;
            int unit = (argStock != null && argStockPtr != IntPtr.Zero) ? argStock.id : -1;

            var gbwk = rstinit.GBWK;
            int eventStart = rstcalc.EventStart;
            int seqCurrent = gbwk?.SeqInfo.Current ?? -1;
            IntPtr gbwkStockPtr = gbwk?.pCurrentStock?.Pointer ?? IntPtr.Zero;
            int gbwkPUpSkillID = gbwk?.PUpSkillID ?? -1;
            bool isPromotionCandidate = gbwkPUpSkillID >= 0 && skillId == gbwkPUpSkillID;

            return
                $"phase={phase} frame={frame} eventStart={eventStart} seqCurrent={seqCurrent} unit={unit} " +
                $"argStockPtr=0x{argStockPtr.ToInt64():X} gbwkStockPtr=0x{gbwkStockPtr.ToInt64():X} " +
                $"skillID={skillId} gbwkPUpSkillID={gbwkPUpSkillID} isPromotionCandidate={isPromotionCandidate}";
        }

        private static void Prefix(ushort __0, Il2Cppnewdata_H.datUnitWork_t __1)
        {
            if (!PowerUpMutationCfgDiagnostics.Enabled) return;
            try
            {
                int unit = (__1 != null && __1.Pointer != IntPtr.Zero) ? __1.id : -1;
                if (!PowerUpMutationCfgDiagnostics.IsTargetUnit(unit)) return;

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-CFG-SKILLOWNER; " + Snapshot("prefix", frame, __0, __1) + ".");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] V3-CFG-SKILLOWNER prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix(ushort __0, Il2Cppnewdata_H.datUnitWork_t __1, sbyte __result)
        {
            if (!PowerUpMutationCfgDiagnostics.Enabled) return;
            try
            {
                int unit = (__1 != null && __1.Pointer != IntPtr.Zero) ? __1.id : -1;
                if (!PowerUpMutationCfgDiagnostics.IsTargetUnit(unit)) return;

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-CFG-SKILLOWNER; " + Snapshot("postfix", frame, __0, __1) +
                    $" result={__result}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] V3-CFG-SKILLOWNER postfix failed safely: {ex.Message}");
            }
        }
    }
}
