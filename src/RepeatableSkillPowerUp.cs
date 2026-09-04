using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Repeatable Skill Power-Up (Stage 2).
    //
    // Background (see the Skill Mutation / Learn-As-New investigation log):
    // rstCalcSkillPowerUpCore rolls a die each call. On the dil=0 branch
    // (ordinary "skill power-up", NOT the Mutation/dil=1 branch), it checks
    // stock+0x10 bit6 (0x40) and bails out immediately if already set; on
    // success it sets bit6. No native code path found in rstcalc/rstCalcCore/
    // rstinit/rstupdate ever clears this bit again except a single gated spot
    // inside rstUpdateSeqSkillPowerUp. In practice that means a unit's
    // ordinary skill power-up roll only ever fires once, for the life of the
    // stock object (bit6 was also confirmed, separately, to survive a save/
    // load in at least one observed case).
    //
    // Crucially, bit6 has NO effect on the dil=1 (Mutation) branch - that was
    // confirmed by direct observation (rstCalcSkillPowerUpCore returned
    // result=2 with bit6 already set). So this feature is unrelated to
    // Skill Mutation / Learn-As-New; it exists purely to let bit6 - and only
    // bit6 - be cleared at a point that is confirmed, via the
    // BIT6-BOUNDARY-CHECK read-only telemetry, to correspond to "this unit's
    // current result-lifecycle processing has finished":
    //
    //   stockChanged : GBWK.pCurrentStock changed across the
    //                  rstCalcSeqDevilLevelUp call that transitions to the
    //                  next demon in this lifecycle.
    //   noMoreDemons : GBWK.TargetIndex reached the "no further demons"
    //                  sentinel value (16) while pCurrentStock stayed the
    //                  same (last unit in the lifecycle).
    //
    // Design note: this clears bit6 once per completed result lifecycle for
    // a unit, not once per level gained within a multi-level-up. If a unit
    // gains several levels in one result screen, it still gets at most one
    // ordinary skill power-up roll for that whole lifecycle. That is treated
    // as "Repeatable Skill Power-Up v1" - repeatable across separate level-up
    // events, not across individual levels within one event.
    //
    // Deliberately independent of SkillMutationLearnAsNew: no shared state,
    // no interaction with HandledSlots/Candidates/Pending. Toggle with
    // Enabled to fully disable.
    internal static class RepeatableSkillPowerUp
    {
        // Retired for the [SkillPowerUp] Chance validation phase: this
        // feature's own bit6-clear (at a rstCalcSeqDevilLevelUp lifecycle
        // boundary) would make Repeat=Native no longer mean genuine vanilla
        // one-shot behavior. Source is kept for a future, separate Repeat
        // investigation - only this flag changed. Both CapturePrefix and
        // ApplyPostfix below no-op immediately while this is false; the
        // Harmony patch class is still applied (MelonMod auto-patches all
        // [HarmonyPatch] classes in the assembly) but has zero effect.
        internal static readonly bool Enabled = false;
        private const byte PowerUpUsedBit = 0x40;

        private static IntPtr _oldStockPtr;
        private static int _oldUnit;
        private static byte _oldFlags10;
        private static bool _oldBit6WasSet;
        private static sbyte _oldTargetIndex;
        private static bool _captured;

        internal static void CapturePrefix()
        {
            if (!Enabled) return;
            _captured = false;
            try
            {
                var gbwk = rstinit.GBWK;
                var currentStock = gbwk?.pCurrentStock;
                if (currentStock == null || currentStock.Pointer == IntPtr.Zero) return;

                _oldStockPtr = currentStock.Pointer;
                _oldUnit = currentStock.id;
                _oldFlags10 = Marshal.ReadByte(_oldStockPtr, 0x10);
                _oldBit6WasSet = (_oldFlags10 & PowerUpUsedBit) != 0;
                _oldTargetIndex = gbwk.TargetIndex;
                _captured = true;
            }
            catch (Exception ex)
            {
                _captured = false;
                MelonLogger.Warning($"[NocturneModernGameplay] BIT6-CLEAR capture failed safely: {ex.Message}");
            }
        }

        internal static void ApplyPostfix()
        {
            if (!Enabled || !_captured) return;
            _captured = false; // consume once per Prefix/Postfix pair

            // Only ever act on the (stock, bit6-was-set) pair captured in
            // Prefix. Never touch whatever GBWK.pCurrentStock happens to be
            // by the time Postfix runs - that may already be a different
            // unit or a native placeholder, and is irrelevant here.
            if (_oldStockPtr == IntPtr.Zero || !_oldBit6WasSet) return;

            // Guard added after direct observation: unit id 0 was seen at a
            // fixed stock pointer (0x1DA7605A630 in one session) receiving a
            // BIT6-CLEAR every result lifecycle, distinct from party members.
            // This is not the speculative "unit==0 exclusion" considered and
            // deliberately rejected earlier - it is a targeted fix for a
            // specific native placeholder/dummy stock confirmed in telemetry,
            // which this feature has no business touching.
            if (_oldUnit == 0) return;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                var newStock = gbwk.pCurrentStock;
                IntPtr newStockPtr = newStock?.Pointer ?? IntPtr.Zero;
                sbyte newTargetIndex = gbwk.TargetIndex;

                bool stockChanged = _oldStockPtr != newStockPtr;
                bool noMoreDemons = newTargetIndex == 16;
                if (!(stockChanged || noMoreDemons)) return;

                byte before = Marshal.ReadByte(_oldStockPtr, 0x10);
                byte after = unchecked((byte)(before & ~PowerUpUsedBit));
                Marshal.WriteByte(_oldStockPtr, 0x10, after);

                string reason = stockChanged ? "stockChanged" : "noMoreDemons";
                MelonLogger.Msg(
                    "[NocturneModernGameplay] BIT6-CLEAR; " +
                    $"frame={UnityEngine.Time.frameCount} oldUnit={_oldUnit} " +
                    $"oldStock=0x{_oldStockPtr.ToInt64():X} " +
                    $"before=0x{before:X2} after=0x{after:X2} reason={reason}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] BIT6-CLEAR apply failed safely: {ex.Message}");
            }
        }
    }

    // Hooks the same native call site already used for the read-only
    // BIT6-BOUNDARY-CHECK telemetry. Runs independently of
    // DevilLevelUpTelemetryPatch - both Prefix/Postfix pairs fire every call,
    // Harmony supports multiple patches per method without conflict.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSeqDevilLevelUp))]
    internal static class RepeatableSkillPowerUpPatch
    {
        private static void Prefix() => RepeatableSkillPowerUp.CapturePrefix();
        private static void Postfix() => RepeatableSkillPowerUp.ApplyPostfix();
    }
}
