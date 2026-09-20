using System.Collections.Generic;
using MelonLoader;

namespace NocturneModernGameplay
{
    // MULTI-LEVEL EPISODE BOUNDARY TRACE PoC (2026-09-19). Read-only
    // observer only. Never writes any native field, never touches the
    // episode latch (HandledCandidatesObserver) beyond reading its existing
    // IsEpisodeLatched accessor.
    //
    // Purpose: real-machine logs preserved before the episode latch shipped
    // (investigations/MULTILEVEL_SKILLCHANGE_ZERO) showed exactly one
    // genuine 2-level episode (unit=97, GBWK.LevelUpCnt 2->1->0) in which
    // rstcalc.rstCalcSkillPowerUpCore fired only ONCE for the whole episode -
    // raising the open question of whether native rolls Skill Power-Up/
    // Mutation once PER LEVEL or once PER EPISODE. This class adds the
    // targeted, LOW-FREQUENCY logging needed to answer that on the next
    // real multi-level-up capture, deliberately avoiding the "log every
    // call unconditionally" mistake that caused a prior real-machine
    // slowdown:
    //
    //   - MULTILEVEL-STATE: emitted from DefaultSkillIteratorTrace's
    //     existing rstCalcEventInfo hook (no new Harmony patch added), but
    //     ONLY when unit/stockPtr/level/LevelUpCnt/SeqInfo.Current/
    //     SeqInfo.Last/TargetIndex/TargetCnt actually changed since the
    //     last observation for that stockPtr - transition-driven, not
    //     per-call.
    //   - MULTILEVEL-CORE: emitted from CoreReentryHandledCheck's existing
    //     rstCalcSkillPowerUpCore hook, once per invocation - already
    //     inherently low-frequency (12 occurrences across a ~46-minute real
    //     session in the preserved pre-latch log), so no additional
    //     throttling is applied there.
    //
    // Level is read directly from newdata_H.datUnitWork_s.level (offset
    // 0x24, confirmed via cpp2il_cs/DiffableCs/Assembly-CSharp/newdata_H/
    // datUnitWork_s.cs and already exposed on the managed `stock` object as
    // `stock.level` the same way `stock.id`/`stock.flag` already are) -
    // real value, not inferred from LevelUpCnt.
    internal static class MultiLevelStateTransitionTrace
    {
        // Disabled 2026-09-19 for production (kept as a diagnostic asset,
        // not deleted): per-episode single-roll understanding is now
        // CONFIRMED (01_CURRENT_STATE.md Phase H), but episode latch
        // double-fire (CORE-EPISODE-LATCH SUPPRESS) has still never been
        // observed - re-enable for any future episode-latch/multi-level
        // re-investigation. Also gates the MULTILEVEL-CORE log emitted
        // from CoreReentryHandledCheck.cs's Postfix.
        internal static readonly bool Enabled = false;

        private readonly struct Snapshot
        {
            internal readonly int Unit;
            internal readonly ushort Level;
            internal readonly short LevelUpCnt;
            internal readonly int SeqCurrent;
            internal readonly int SeqLast;
            internal readonly sbyte TargetIndex;
            internal readonly sbyte TargetCnt;
            internal readonly bool Bit6;

            internal Snapshot(int unit, ushort level, short levelUpCnt, int seqCurrent,
                int seqLast, sbyte targetIndex, sbyte targetCnt, bool bit6)
            {
                Unit = unit;
                Level = level;
                LevelUpCnt = levelUpCnt;
                SeqCurrent = seqCurrent;
                SeqLast = seqLast;
                TargetIndex = targetIndex;
                TargetCnt = targetCnt;
                Bit6 = bit6;
            }

            internal bool DiffersFrom(in Snapshot other) =>
                Unit != other.Unit || Level != other.Level || LevelUpCnt != other.LevelUpCnt ||
                SeqCurrent != other.SeqCurrent || SeqLast != other.SeqLast ||
                TargetIndex != other.TargetIndex || TargetCnt != other.TargetCnt ||
                Bit6 != other.Bit6;
        }

        private static readonly Dictionary<long, Snapshot> LastSeen = new();

        // Called from DefaultSkillIteratorTrace.Postfix with values it has
        // already fetched this call (unit/stockPtr/seqCurrent/seqLast/
        // levelUpCnt), plus level/TargetIndex/TargetCnt/bit6 fetched fresh
        // here from the same already-open gbwk/stock this call already
        // proved live.
        internal static void Observe(int frame, int unit, long stockPtr, ushort level,
            short levelUpCnt, int seqCurrent, int seqLast, sbyte targetIndex, sbyte targetCnt,
            bool bit6)
        {
            if (!Enabled || stockPtr == 0) return;

            var current = new Snapshot(unit, level, levelUpCnt, seqCurrent, seqLast, targetIndex,
                targetCnt, bit6);

            if (LastSeen.TryGetValue(stockPtr, out Snapshot previous) && !current.DiffersFrom(previous))
            {
                return;
            }

            LastSeen[stockPtr] = current;

            MelonLogger.Msg(
                "[NocturneModernGameplay] MULTILEVEL-STATE; " +
                $"frame={frame}; unit={unit}; stockPtr=0x{stockPtr:X}; level={level}; " +
                $"levelUpCnt={levelUpCnt}; seqCurrent={seqCurrent}; seqLast={seqLast}; " +
                $"targetIndex={targetIndex}; targetCnt={targetCnt}; bit6={bit6}.");
        }
    }
}
