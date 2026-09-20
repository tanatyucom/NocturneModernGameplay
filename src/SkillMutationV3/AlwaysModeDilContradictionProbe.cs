using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // ALWAYS/dil CONTRADICTION VERIFICATION PROBE (2026-09-19). Read-only.
    // Never writes any native field, never touches __result.
    //
    // Purpose: static RE (this session) confirmed the ordinary Power-Up/
    // exclusion block (VA 0x18227E3AD) has exactly 2 structural entry
    // points in rstCalcSkillPowerUpCore (function start VA 0x18227E100),
    // both gated by SkillMutationChanceControl's 3 Always-mode patches
    // (RngPathA 0x18227E339, RngPathB 0x18227E342, IdZeroPath 0x18227E39C)
    // - confirmed the only incoming edges, on-disk vanilla bytes match
    // expectations exactly, no duplicate function body exists. Yet
    // real-machine logs show OPTION-F-DECISION firing with
    // classification=R0-B (native exclusion matched, requires dil==0)
    // during a session where SkillMutation.Chance=Always was continuously
    // active with successful patch application (readback-verified, no
    // mode toggling in between). This is an UNRESOLVED contradiction
    // between static structure and observed behavior. Two live-only
    // hypotheses remain to distinguish, neither answerable from static
    // disassembly alone:
    //   (a) the patched bytes are somehow not actually in place in LIVE
    //       process memory at the exact moment Core runs, despite the
    //       mod's own readback check claiming success at SetMode() time
    //   (b) OptionFRepeatUnlimitedControl's R0-B classification reads a
    //       STALE gbwk.PUpSkillID - captured only in Postfix, never in
    //       Prefix - so a value left over from an earlier call/context
    //       could be misattributed to the specific rawResult==0 invocation
    //       under classification, when the true PUpSkillID for THIS call
    //       was actually 0 (R0-A) or something else entirely.
    //
    // This class hooks rstCalcSkillPowerUpCore's own Prefix/Postfix,
    // independently of every other diagnostic already on this method
    // (Harmony supports multiple independent patches on one method - see
    // HandledCandidatesObserver's own header comment for the established
    // precedent), and does two things no existing diagnostic does:
    //   1. reads the LIVE bytes at the 3 patch VAs directly from process
    //      memory on every invocation (distinguishing hypothesis (a))
    //   2. captures gbwk.PUpSkillID at Prefix time (before Core runs), in
    //      addition to Postfix time, so a stale-vs-fresh PUpSkillID can be
    //      told apart directly (distinguishing hypothesis (b))
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    [HarmonyPriority(Priority.First)]
    internal static class AlwaysModeDilContradictionProbe
    {
        // Disabled 2026-09-19: the contradiction is resolved (see
        // 01_CURRENT_STATE.md Phase H - early exclusion happens before the
        // dil roll, so SkillMutationChanceControl's Always patches were
        // never the issue). Kept as a diagnostic asset, not deleted.
        internal static readonly bool Enabled = false;

        private const long RngPathAVa = 0x18227E339L;
        private const long RngPathBVa = 0x18227E342L;
        private const long IdZeroPathVa = 0x18227E39CL;

        private static bool _captured;
        private static int _unit;
        private static long _stockPtr;
        private static ushort _pUpSkillIdAtPrefix;
        private static string _liveBytesAtPrefix = "";

        private static void Prefix()
        {
            _captured = false;
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;

                _unit = stock.id;
                _stockPtr = stock.Pointer.ToInt64();
                _pUpSkillIdAtPrefix = gbwk.PUpSkillID;

                IntPtr moduleBase = NativeChancePatchUtility.ResolveModuleBase();
                IntPtr rngPathA = NativeChancePatchUtility.ResolveVa(moduleBase, RngPathAVa);
                IntPtr rngPathB = NativeChancePatchUtility.ResolveVa(moduleBase, RngPathBVa);
                IntPtr idZeroPath = NativeChancePatchUtility.ResolveVa(moduleBase, IdZeroPathVa);

                byte[] a = NativeChancePatchUtility.ReadBytes(rngPathA, 2);
                byte[] b = NativeChancePatchUtility.ReadBytes(rngPathB, 2);
                byte[] c = NativeChancePatchUtility.ReadBytes(idZeroPath, 3);

                _liveBytesAtPrefix =
                    $"rngPathA=[{NativeChancePatchUtility.FormatBytes(a)}] " +
                    $"rngPathB=[{NativeChancePatchUtility.FormatBytes(b)}] " +
                    $"idZeroPath=[{NativeChancePatchUtility.FormatBytes(c)}]";
                _captured = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] AlwaysModeDilContradictionProbe prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix(sbyte __result)
        {
            if (!_captured) return;
            _captured = false;
            try
            {
                var gbwk = rstinit.GBWK;
                ushort pUpSkillIdAtPostfix = gbwk?.PUpSkillID ?? 0;
                int frame = UnityEngine.Time.frameCount;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] ALWAYSDIL-PROBE; " +
                    $"frame={frame}; unit={_unit}; stockPtr=0x{_stockPtr:X}; " +
                    $"pUpSkillIdAtPrefix={_pUpSkillIdAtPrefix}; pUpSkillIdAtPostfix={pUpSkillIdAtPostfix}; " +
                    $"rawResult={__result}; " +
                    $"skillMutationChanceMode={SkillMutationChanceControl.Mode}; " +
                    $"{_liveBytesAtPrefix}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] AlwaysModeDilContradictionProbe postfix failed safely: {ex.Message}");
            }
        }
    }
}
