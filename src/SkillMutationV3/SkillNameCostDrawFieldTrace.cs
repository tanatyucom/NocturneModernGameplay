using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - cmpSkillNameCostDraw
    // entry-point trace (read-only, no writes). Session continuation of
    // HiddenSlotArrayObserver.cs / SkillCursorFieldTrace.cs.
    //
    // Background: static disassembly of cmpDrawSkill.cmpDrawSkillList
    // (VA 0x182525040) shows a per-row gate at VA 0x182525268/0x18252526E
    // ("cmp dword ptr [rcx + r12*4 + 0x20], 0" / "je 0x18252549b") that
    // skips the ENTIRE per-row content block - not just the highlight
    // computation - whenever stock.skill[idx] == 0. That skipped block
    // contains the only confirmed call site to cmpSkillNameCostDraw
    // (VA 0x182525466, argument-exact xref-confirmed: it is
    // cmpSkillNameCostDraw's ONLY direct caller in the executable). So
    // statically: if stock.skill[8] stays 0 (already confirmed via
    // HiddenSlotArrayObserver), cmpSkillNameCostDraw is never called for
    // that row through this loop at all - MskFlag/SelFlag never even get
    // computed for it via this path.
    //
    // This class captures the REAL incoming MskFlag/SelFlag (and Col/idx/
    // SkillID) at cmpSkillNameCostDraw's own entry point, instead of
    // inferring them from the caller's disassembly, so a native single-
    // demon forget flow (seq21/22) can be compared row-by-row against this
    // bridge's own borrowed forget flow to find the first observable
    // presentation-relevant difference.
    //
    // Logs unconditionally whenever SeqInfo.Current is 21 or 22 (covers
    // BOTH native's own forget flow and this bridge's borrowed one, exactly
    // like SkillCursorFieldTrace), deduplicated per-idx per-frame so a
    // static (non-blinking) row does not spam the log every frame.
    [HarmonyPatch(typeof(cmpDrawSkill), nameof(cmpDrawSkill.cmpSkillNameCostDraw))]
    internal static class SkillNameCostDrawFieldTrace
    {
        internal static readonly bool Enabled = true;

        // Zero SKILLNAMECOSTDRAW-FIELD-TRACE lines appeared in the first
        // real-machine test even though the sibling seq21/22 logic-side
        // traces (SkillCursorFieldTrace, HiddenSlotArrayObserver) fired
        // normally - meaning the forget UI genuinely opened, but this
        // Prefix never ran. Mirrors GetDefaultSkillCallBoundaryTrace.
        // LogPatchStatus (DefaultSkillIteratorTrace.cs) to make patch
        // application itself independently verifiable at init time,
        // rather than inferring it only from silence.
        internal static void LogPatchStatus()
        {
            try
            {
                var method = typeof(cmpDrawSkill).GetMethod(nameof(cmpDrawSkill.cmpSkillNameCostDraw));
                if (method == null)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLNAMECOSTDRAW-PATCH-STATUS; " +
                        "methodInfo=NULL (GetMethod failed to resolve cmpDrawSkill.cmpSkillNameCostDraw).");
                    return;
                }

                var info = HarmonyLib.Harmony.GetPatchInfo(method);
                int prefixes = info?.Prefixes?.Count ?? 0;
                int postfixes = info?.Postfixes?.Count ?? 0;
                IntPtr fnPtr = method.MethodHandle.GetFunctionPointer();
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLNAMECOSTDRAW-PATCH-STATUS; " +
                    $"methodInfo=FOUND; declaringType={method.DeclaringType}; " +
                    $"prefixes={prefixes}; postfixes={postfixes}; " +
                    $"functionPointer=0x{fnPtr.ToInt64():X}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillNameCostDrawFieldTrace.LogPatchStatus failed: {ex}");
            }
        }

        private static readonly System.Collections.Generic.Dictionary<int, string> _lastByIdx =
            new System.Collections.Generic.Dictionary<int, string>();

        // See SkillMakeStrColFieldTrace's identical counter for rationale.
        private static long _totalCalls;

        private static void Prefix(int idx, uint Col, ushort SkillID, sbyte MskFlag, sbyte SelFlag)
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
                        "[NocturneModernGameplay] SKILLNAMECOSTDRAW-UNGATED-HEARTBEAT; " +
                        $"totalCalls={_totalCalls}; frame={UnityEngine.Time.frameCount}; seq={seqForHeartbeat}.");
                }

                int seq = seqForHeartbeat;
                if (seq != 21 && seq != 22) return;

                var cursorInfo = gbwk.SkillCursor;
                var cursorPos = cursorInfo?.CursorPos;

                short cursorIndex = cursorPos?.Index ?? -1;
                byte cursorShift = cursorPos?.Shift ?? 0;

                bool bridgeActive = FullCapacityAddNewBridgeState.Active;
                int unit = -1;
                try
                {
                    var stock = gbwk.pCurrentStock;
                    if (stock != null && stock.Pointer != IntPtr.Zero) unit = stock.id;
                }
                catch { /* leave unit=-1 */ }

                string snapshot =
                    $"seq={seq}; bridgeActive={bridgeActive}; unit={unit}; idx={idx}; " +
                    $"skillId={SkillID}; col=0x{Col:X8}; mskFlag={MskFlag}; selFlag={SelFlag}; " +
                    $"cursorIndex={cursorIndex}; cursorShift={cursorShift}; selectSkillID={gbwk.SelectSkillID}";

                if (_lastByIdx.TryGetValue(idx, out var prev) && prev == snapshot) return;
                _lastByIdx[idx] = snapshot;

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLNAMECOSTDRAW-FIELD-TRACE; " +
                    $"frame={frame}; {snapshot}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillNameCostDrawFieldTrace prefix failed safely: {ex.Message}");
            }
        }
    }
}
