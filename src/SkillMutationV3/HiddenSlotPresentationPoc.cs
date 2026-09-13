using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - CONTROLLED
    // EXPERIMENT (not a confirmed fix yet).
    //
    // Background: HiddenSlotArrayObserver confirmed stock.skill[8] (one
    // past the official 8-slot capacity) stays 0 throughout this bridge's
    // own borrowed forget UI, in BOTH a curriculum-exhausted case and a
    // curriculum-still-pending case, and the highlight box never rendered
    // for the synthetic 9th row in either. Static disassembly of
    // cmpDrawSkill.cmpDrawSkillList (VA ~0x18252526C-2E) shows the
    // highlight/selection computation for a row is skipped entirely when
    // "stock.skill[idx] == 0". This does NOT yet prove skill[8]!=0 is
    // sufficient to make native render the highlight (that direction is
    // untested) - this class exists purely to test that, narrowly and
    // reversibly.
    //
    // Scope (explicit, per design agreement):
    //   - Active ONLY while FullCapacityAddNewBridgeState.Active (this
    //     bridge's own borrowed forget UI window).
    //   - Writes ONLY stock.skill[8] (index 8, one past the real 8 owned
    //     slots) - NEVER stock.skillcnt, which stays exactly as native
    //     reports it throughout.
    //   - The original value at index 8 (expected 0, but captured rather
    //     than assumed) is saved once when the window opens and force-
    //     restored the instant the window closes (transaction completes,
    //     cancels, or the tracked stock pointer stops matching) - never
    //     left in a modified state.
    //   - Never touches any of the real 8 owned-skill slots (0-7).
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class HiddenSlotPresentationPoc
    {
        // DISABLED: real-machine testing this session refuted the
        // hypothesis - writing stock.skill[8]=target (confirmed via
        // HIDDEN-SLOT-POC-STAGE/-RESTORE logs to actually take effect and
        // cleanly restore) did NOT make the highlight box appear. Left in
        // place (not deleted) as a documented, ruled-out experiment; the
        // investigation moved on to SkillCursorFieldTrace.cs instead.
        internal static readonly bool Enabled = false;

        private const int HiddenSlotIndex = 8;

        private static bool _capturedOriginal;
        private static int _originalValue;
        private static long _capturedStockPtr;
        private static int _capturedUnit;

        private static void Postfix()
        {
            if (!Enabled) return;

            try
            {
                if (FullCapacityAddNewBridgeState.Active)
                {
                    var gbwk = rstinit.GBWK;
                    if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                    var stock = gbwk.pCurrentStock;
                    if (stock == null || stock.Pointer == IntPtr.Zero) return;
                    var arr = stock.skill;
                    if (arr == null || arr.Length <= HiddenSlotIndex) return;

                    long stockPtr = stock.Pointer.ToInt64();

                    if (!_capturedOriginal)
                    {
                        _originalValue = arr[HiddenSlotIndex];
                        _capturedStockPtr = stockPtr;
                        _capturedUnit = stock.id;
                        _capturedOriginal = true;

                        MelonLogger.Msg(
                            "[NocturneModernGameplay] HIDDEN-SLOT-POC-CAPTURE; " +
                            $"unit={_capturedUnit}; stockPtr=0x{stockPtr:X}; " +
                            $"originalSkill8={_originalValue}; " +
                            $"target={FullCapacityAddNewBridgeState.Target}.");
                    }

                    if (stockPtr == _capturedStockPtr)
                    {
                        int target = FullCapacityAddNewBridgeState.Target;
                        if (arr[HiddenSlotIndex] != target)
                        {
                            arr[HiddenSlotIndex] = target;
                            MelonLogger.Msg(
                                "[NocturneModernGameplay] HIDDEN-SLOT-POC-STAGE; " +
                                $"unit={_capturedUnit}; stockPtr=0x{stockPtr:X}; " +
                                $"skill8Set={target}; skillcntUnchanged={stock.skillcnt}.");
                        }
                    }
                }
                else if (_capturedOriginal)
                {
                    RestoreNow("bridge-window-closed");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HiddenSlotPresentationPoc postfix failed safely: {ex.Message}");
                // Fail-closed: try to restore immediately rather than risk
                // leaving index 8 modified.
                RestoreNow("exception-fail-safe");
            }
        }

        private static void RestoreNow(string reason)
        {
            if (!_capturedOriginal) return;
            _capturedOriginal = false;
            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                long liveStockPtr = stock?.Pointer.ToInt64() ?? 0;

                if (stock != null && liveStockPtr == _capturedStockPtr)
                {
                    var arr = stock.skill;
                    if (arr != null && arr.Length > HiddenSlotIndex)
                    {
                        arr[HiddenSlotIndex] = _originalValue;
                        MelonLogger.Msg(
                            "[NocturneModernGameplay] HIDDEN-SLOT-POC-RESTORE; " +
                            $"reason={reason}; unit={_capturedUnit}; stockPtr=0x{liveStockPtr:X}; " +
                            $"restoredTo={_originalValue}; skillcntUnchanged={stock.skillcnt}.");
                        return;
                    }
                }

                MelonLogger.Warning(
                    "[NocturneModernGameplay] HIDDEN-SLOT-POC-RESTORE-SKIPPED; " +
                    $"reason={reason}; capturedUnit={_capturedUnit}; " +
                    $"capturedStockPtr=0x{_capturedStockPtr:X}; liveStockPtr=0x{liveStockPtr:X} " +
                    "(stock pointer changed or unavailable - could not verify restore target).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] HiddenSlotPresentationPoc restore failed: {ex.Message}");
            }
        }
    }
}
