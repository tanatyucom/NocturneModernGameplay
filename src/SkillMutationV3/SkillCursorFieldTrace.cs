using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - SkillCursor
    // differential trace (read-only, no writes).
    //
    // Background: GBWK+0x88 is confirmed (via rstData_t's real managed
    // field layout) to be `SkillCursor` (cmpCursorInfo_t), and its own
    // +0x20 sub-object is `CursorPos` (cmpCursorPos_t) - the object
    // cmpDrawSkillSelectCursor itself reads (gating its whole draw call on
    // CursorPos+0x10 being nonzero). cmpCursorPos_t's real field layout:
    //   +0x10 ListNums            (byte)  - likely total REAL entries
    //   +0x11 InvisibleListNums   (sbyte) - literally named "invisible
    //                                       list count" - the leading
    //                                       candidate for what governs
    //                                       whether the synthetic 9th row
    //                                       is treated as a real,
    //                                       highlightable entry
    //   +0x12 Index               (short) - the "official" cursor index
    //   +0x14 Shift               (byte)  - what this session's earlier
    //                                       cursor-reading code actually
    //                                       reads (empirically tracked
    //                                       cursor position correctly for
    //                                       this non-scrolling list, but
    //                                       is NOT the same field as
    //                                       Index - noted for accuracy)
    //   +0x15 DrawShift, +0x16 ShiftMax
    //
    // Native's own transition into seq21 (rstUpdateSeqDefaultSkill's
    // forget-needed branch) calls an auxiliary function (VA 0x1822EEFA0,
    // investigated earlier this session as "pure UI, no stateful side
    // effect" - correct in isolation, but re-examined here in a NEW
    // context) that resets several small fields on SkillCursor's own
    // sub-objects before opening the UI. FullCapacityAddNewBridgeMonitor's
    // own seq21 entry only writes SeqInfo.Current=21 - it never calls this
    // initialization, so SkillCursor/CursorPos may carry over STALE values
    // from whatever it was doing before (e.g. the Power-Up/Mutation
    // presentation), rather than a freshly-reset state matching what
    // native's own genuine forget-UI entry always starts from.
    //
    // Logs unconditionally whenever SeqInfo.Current is 21 or 22 (covers
    // BOTH native's own forget flow and this bridge's borrowed one),
    // tagged with bridgeActive so the two can be directly compared in the
    // same log without needing two separate test runs.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class SkillCursorFieldTrace
    {
        internal static readonly bool Enabled = true;

        private static string _lastSnapshot = "";

        private static void Postfix()
        {
            if (!Enabled) return;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                int seq = gbwk.SeqInfo.Current;
                if (seq != 21 && seq != 22) return;

                var cursorInfo = gbwk.SkillCursor;
                if (cursorInfo == null) return;
                var cursorPos = cursorInfo.CursorPos;
                if (cursorPos == null) return;

                byte listNums = cursorPos.ListNums;
                sbyte invisibleListNums = cursorPos.InvisibleListNums;
                short index = cursorPos.Index;
                byte shift = cursorPos.Shift;
                byte drawShift = cursorPos.DrawShift;
                byte shiftMax = cursorPos.ShiftMax;
                sbyte stopFlag = cursorInfo.StopFlag;
                int stepY = cursorInfo.StepY;

                bool bridgeActive = FullCapacityAddNewBridgeState.Active;
                int unit = -1;
                try
                {
                    var stock = gbwk.pCurrentStock;
                    if (stock != null && stock.Pointer != IntPtr.Zero) unit = stock.id;
                }
                catch { /* leave unit=-1 */ }

                string snapshot =
                    $"seq={seq}; bridgeActive={bridgeActive}; unit={unit}; " +
                    $"listNums={listNums}; invisibleListNums={invisibleListNums}; " +
                    $"index={index}; shift={shift}; drawShift={drawShift}; shiftMax={shiftMax}; " +
                    $"stopFlag={stopFlag}; stepY={stepY}";

                if (snapshot == _lastSnapshot) return;
                _lastSnapshot = snapshot;

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLCURSOR-FIELD-TRACE; " +
                    $"frame={frame}; {snapshot}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCursorFieldTrace postfix failed safely: {ex.Message}");
            }
        }
    }
}
