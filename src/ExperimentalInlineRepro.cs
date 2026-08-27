using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Temporary diagnostic path. This is intentionally compile-time isolated so
    // the production Global Pending Queue remains intact and can be restored by
    // changing this single switch to false.
    internal static class ExperimentalInlineRepro
    {
        internal static readonly bool Enabled = true;
        private const byte MutationVisitedBit = 0x40;

        internal readonly struct Bit6State
        {
            internal Bit6State(IntPtr stock, int unit, bool restore)
            {
                Stock = stock;
                Unit = unit;
                Restore = restore;
            }

            internal IntPtr Stock { get; }
            internal int Unit { get; }
            internal bool Restore { get; }
        }

        internal static void Initialize()
        {
            if (!Enabled) return;
            MelonLogger.Warning(
                "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO enabled; " +
                "Global Pending Queue is preserved but bypassed for new full-capacity mutations. " +
                "Unit 59/60 bit6 is cleared only for each rstCalcSkillPowerUpCore call and restored afterward.");
        }

        internal static Bit6State BeginCoreBit6Probe()
        {
            if (!Enabled) return default;
            try
            {
                Il2Cppnewdata_H.datUnitWork_t stock = rstinit.GBWK.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero ||
                    (stock.id != 59 && stock.id != 60)) return default;

                byte flags = Marshal.ReadByte(stock.Pointer, 0x10);
                bool wasSet = (flags & MutationVisitedBit) != 0;
                if (wasSet)
                    Marshal.WriteByte(stock.Pointer, 0x10,
                        unchecked((byte)(flags & ~MutationVisitedBit)));

                MelonLogger.Msg(
                    "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO bit6-core-entry; " +
                    $"frame={UnityEngine.Time.frameCount} unit={stock.id} " +
                    $"stockPtr=0x{stock.Pointer.ToInt64():X} before=0x{flags:X2} " +
                    $"cleared={wasSet}.");
                return new Bit6State(stock.Pointer, stock.id, wasSet);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO bit6 entry failed safely: {ex.Message}");
                return default;
            }
        }

        internal static void EndCoreBit6Probe(Bit6State state, sbyte result)
        {
            if (!Enabled || state.Stock == IntPtr.Zero) return;
            try
            {
                byte afterCore = Marshal.ReadByte(state.Stock, 0x10);
                if (state.Restore)
                    Marshal.WriteByte(state.Stock, 0x10,
                        unchecked((byte)(afterCore | MutationVisitedBit)));
                byte restored = Marshal.ReadByte(state.Stock, 0x10);
                MelonLogger.Msg(
                    "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO bit6-core-exit; " +
                    $"frame={UnityEngine.Time.frameCount} unit={state.Unit} " +
                    $"stockPtr=0x{state.Stock.ToInt64():X} result={result} " +
                    $"afterCore=0x{afterCore:X2} restored=0x{restored:X2}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO bit6 restore failed safely: {ex.Message}");
            }
        }
    }

    // rstCalcSkillPowerUpCore and stock+0x10 bit6 are already covered by the
    // existing native analysis and detailed telemetry. No new native target is
    // introduced by this experiment.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    [HarmonyPriority(Priority.First)]
    internal static class ExperimentalInlineCoreBit6Patch
    {
        private static void Prefix(out ExperimentalInlineRepro.Bit6State __state) =>
            __state = ExperimentalInlineRepro.BeginCoreBit6Probe();

        private static void Postfix(sbyte __result, ExperimentalInlineRepro.Bit6State __state) =>
            ExperimentalInlineRepro.EndCoreBit6Probe(__state, __result);
    }
}
