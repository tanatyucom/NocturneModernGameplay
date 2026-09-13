using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - broader-net
    // trace (read-only, no writes). Session continuation of
    // SkillDrawListEntryTrace.cs / SkillNameCostDrawFieldTrace.cs.
    //
    // Background: real-machine testing this session CONFIRMED both
    // cmpDrawSkill.cmpDrawSkillList and cmpDrawSkill.cmpSkillNameCostDraw
    // are correctly Harmony-patched (SKILLDRAWLIST-PATCH-STATUS /
    // SKILLNAMECOSTDRAW-PATCH-STATUS both report prefixes=1, valid
    // functionPointer) yet NEITHER fired even once while the user visually
    // confirmed the forget-skill list itself WAS on screen (both the High
    // Pixie case and the Frost case, up through the point Frost actually
    // learned the mutated skill). This REJECTS the standing assumption
    // that cmpDrawSkillList is the row-list draw function used by this
    // screen - some other function renders it.
    //
    // New lead: static disassembly of cmpDrawSkillList's own (unreached,
    // per the above) per-row block shows a call at VA 0x1825253E8 with the
    // exact register pattern of cmpMisc.cmpMakeStrCol(UInt32 FontCol,
    // SByte SelFlag, SByte MskFlag, cmpCursorEff_t pEff, ref Int32 MatCol)
    // - rcx=FontCol, rdx=SelFlag, r8=MskFlag, r9=pEff, stack1=&MatCol.
    // Target VA 0x1822ED9D0 sits inside a contiguous methodPointers block
    // (raw-pointer-table check, file offsets 0x2DC3998-0x2DC3A50) that also
    // contains 0x1822EEFA0 - already independently identified in this
    // investigation as a cmpMisc cursor-related helper - so this cluster is
    // STRONGLY SUPPORTED as cmpMisc's own method block, not proof by itself
    // but a second independent corroboration beyond the register-pattern
    // match alone.
    //
    // cmpMakeStrCol is a generic Sel/Msk-flag-to-color helper, plausibly
    // shared by MANY row-drawing call sites across the whole cmp UI system
    // - not just cmpDrawSkillList's (apparently unused, for this screen)
    // call site. Hooking it directly sidesteps needing to first identify
    // which top-level function actually draws this list: if some other
    // function reuses this same helper, this Prefix still sees the real
    // SelFlag/MskFlag regardless of the caller's identity.
    //
    // No idx/row identity is available at this entry point (cmpMakeStrCol
    // takes no row index), so calls are numbered by order-of-occurrence
    // within a single frame instead, to allow correlating call count per
    // frame (e.g. 8 vs 9 calls) against known owned-skill counts.
    [HarmonyPatch(typeof(cmpMisc), nameof(cmpMisc.cmpMakeStrCol))]
    internal static class SkillMakeStrColFieldTrace
    {
        internal static readonly bool Enabled = true;

        private static int _lastFrame = -1;
        private static int _callIndexInFrame = -1;

        // Ungated total-call counter, independent of the seq==21/22 gate
        // below. Three consecutive real-machine tests produced zero
        // MAKESTRCOL-FIELD-TRACE / SKILLDRAWLIST-ENTRY-TRACE /
        // SKILLNAMECOSTDRAW-FIELD-TRACE lines despite the user visually
        // confirming the forget-skill list was on screen and all three
        // Harmony patches reporting prefixes=1. This counter answers a
        // narrower question standalone from that: does this method EVER
        // run, anywhere in the game, regardless of SeqInfo.Current timing?
        // If it stays 0 for an entire play session, the method is not
        // reached at all under current gameplay (not a seq-gate timing
        // artifact). If it grows but seq21/22-gated logging below stays
        // silent, SeqInfo.Current does not overlap this call's actual
        // timing the way the sibling logic-side traces assume.
        private static long _totalCalls;

        internal static void LogPatchStatus()
        {
            try
            {
                var method = typeof(cmpMisc).GetMethod(nameof(cmpMisc.cmpMakeStrCol));
                if (method == null)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] MAKESTRCOL-PATCH-STATUS; " +
                        "methodInfo=NULL (GetMethod failed to resolve cmpMisc.cmpMakeStrCol).");
                    return;
                }

                var info = HarmonyLib.Harmony.GetPatchInfo(method);
                int prefixes = info?.Prefixes?.Count ?? 0;
                int postfixes = info?.Postfixes?.Count ?? 0;
                IntPtr fnPtr = method.MethodHandle.GetFunctionPointer();
                MelonLogger.Msg(
                    "[NocturneModernGameplay] MAKESTRCOL-PATCH-STATUS; " +
                    $"methodInfo=FOUND; declaringType={method.DeclaringType}; " +
                    $"prefixes={prefixes}; postfixes={postfixes}; " +
                    $"functionPointer=0x{fnPtr.ToInt64():X}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillMakeStrColFieldTrace.LogPatchStatus failed: {ex}");
            }
        }

        private static void Prefix(uint FontCol, sbyte SelFlag, sbyte MskFlag)
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
                        "[NocturneModernGameplay] MAKESTRCOL-UNGATED-HEARTBEAT; " +
                        $"totalCalls={_totalCalls}; frame={UnityEngine.Time.frameCount}; seq={seqForHeartbeat}.");
                }

                int seq = seqForHeartbeat;
                if (seq != 21 && seq != 22) return;

                int frame = UnityEngine.Time.frameCount;
                if (frame != _lastFrame)
                {
                    _lastFrame = frame;
                    _callIndexInFrame = 0;
                }
                else
                {
                    _callIndexInFrame++;
                }

                bool bridgeActive = FullCapacityAddNewBridgeState.Active;
                int unit = -1;
                try
                {
                    var stock = gbwk.pCurrentStock;
                    if (stock != null && stock.Pointer != IntPtr.Zero) unit = stock.id;
                }
                catch { /* leave unit=-1 */ }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] MAKESTRCOL-FIELD-TRACE; " +
                    $"frame={frame}; callIndexInFrame={_callIndexInFrame}; seq={seq}; " +
                    $"bridgeActive={bridgeActive}; unit={unit}; " +
                    $"fontCol=0x{FontCol:X8}; selFlag={SelFlag}; mskFlag={MskFlag}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillMakeStrColFieldTrace prefix failed safely: {ex.Message}");
            }
        }
    }
}
