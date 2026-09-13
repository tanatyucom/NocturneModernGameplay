using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - cmpDrawSkillList
    // entry-point trace (read-only, no writes). Companion to
    // SkillNameCostDrawFieldTrace.cs.
    //
    // Purpose: capture stock.skill[8] at the exact moment cmpDrawSkillList
    // itself starts running the per-frame draw pass, not at rstUpdate
    // Postfix time (where HiddenSlotPresentationPoc's earlier disabled
    // write-PoC staged/observed it). The two points are NOT guaranteed to
    // be the same frame-relative moment - if a future write experiment
    // stages skill[8] in an rstUpdate Postfix but the draw pass actually
    // runs before that Postfix (or after a restore), the earlier "writing
    // skill[8] didn't change the highlight" result would be a timing
    // artifact rather than proof the array slot is irrelevant to
    // rendering. This trace exists to settle that ambiguity independently
    // of any write experiment - it only ever reads.
    //
    // Logs once per distinct state whenever SeqInfo.Current is 21 or 22
    // (same gate as the sibling traces), regardless of write-PoC state
    // (HiddenSlotPresentationPoc.Enabled is currently false, so under
    // normal operation this always reports the untouched native value).
    [HarmonyPatch(typeof(cmpDrawSkill), nameof(cmpDrawSkill.cmpDrawSkillList))]
    internal static class SkillDrawListEntryTrace
    {
        internal static readonly bool Enabled = true;

        // See SkillNameCostDrawFieldTrace.LogPatchStatus for why this
        // exists: the first real-machine test produced zero lines from
        // this trace (and from SkillNameCostDrawFieldTrace) while the
        // sibling seq21/22 logic-side traces fired normally, so patch
        // application itself needs to be independently verifiable.
        internal static void LogPatchStatus()
        {
            try
            {
                var method = typeof(cmpDrawSkill).GetMethod(nameof(cmpDrawSkill.cmpDrawSkillList));
                if (method == null)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLDRAWLIST-PATCH-STATUS; " +
                        "methodInfo=NULL (GetMethod failed to resolve cmpDrawSkill.cmpDrawSkillList).");
                    return;
                }

                var info = HarmonyLib.Harmony.GetPatchInfo(method);
                int prefixes = info?.Prefixes?.Count ?? 0;
                int postfixes = info?.Postfixes?.Count ?? 0;
                IntPtr fnPtr = method.MethodHandle.GetFunctionPointer();
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLDRAWLIST-PATCH-STATUS; " +
                    $"methodInfo=FOUND; declaringType={method.DeclaringType}; " +
                    $"prefixes={prefixes}; postfixes={postfixes}; " +
                    $"functionPointer=0x{fnPtr.ToInt64():X}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillDrawListEntryTrace.LogPatchStatus failed: {ex}");
            }
        }

        private static string _lastSnapshot = "";

        // See SkillMakeStrColFieldTrace's identical counter for rationale:
        // three consecutive tests produced zero ENTRY-TRACE lines despite
        // the list visibly being on screen, so an ungated call counter is
        // needed to tell "never called at all" apart from "called, but not
        // while SeqInfo.Current reads 21/22".
        private static long _totalCalls;

        private static void Prefix(sbyte DrawMode, int Rate, uint OtNo)
        {
            if (!Enabled) return;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                int seqForHeartbeat = gbwk.SeqInfo.Current;
                _totalCalls++;
                if (_totalCalls == 1 || _totalCalls % 500 == 0)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLDRAWLIST-UNGATED-HEARTBEAT; " +
                        $"totalCalls={_totalCalls}; frame={UnityEngine.Time.frameCount}; seq={seqForHeartbeat}.");
                }

                int seq = seqForHeartbeat;
                if (seq != 21 && seq != 22) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                var arr = stock.skill;
                int skill8 = (arr != null && arr.Length > 8) ? arr[8] : -999999;

                var cursorInfo = gbwk.SkillCursor;
                var cursorPos = cursorInfo?.CursorPos;
                short cursorIndex = cursorPos?.Index ?? -1;
                byte cursorShift = cursorPos?.Shift ?? 0;

                bool bridgeActive = FullCapacityAddNewBridgeState.Active;

                string snapshot =
                    $"seq={seq}; bridgeActive={bridgeActive}; unit={stock.id}; " +
                    $"skillcnt={stock.skillcnt}; skill8={skill8}; " +
                    $"cursorIndex={cursorIndex}; cursorShift={cursorShift}; " +
                    $"selectSkillID={gbwk.SelectSkillID}; drawMode={DrawMode}; rate={Rate}; otNo={OtNo}";

                if (snapshot == _lastSnapshot) return;
                _lastSnapshot = snapshot;

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLDRAWLIST-ENTRY-TRACE; " +
                    $"frame={frame}; {snapshot}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillDrawListEntryTrace prefix failed safely: {ex.Message}");
            }
        }
    }
}
