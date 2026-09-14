using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - direct
    // statusUI field read (read-only, no writes). Session continuation
    // after ForgetUiHierarchyDumpTrace.cs identified the real managing
    // MonoBehaviour: the "statusUI(Clone)" GameObject carries a component
    // of type statusUI (global namespace, Assembly-CSharp), and its
    // TextMeshProUGUI[] obtainedText / GameObject[] obtainedObj fields are
    // exactly the "sskill_obtained01..08" rows we were reading generically
    // through FindObjectsOfType<TextMeshProUGUI>().
    //
    // ForgetUiHierarchyDumpTrace also showed, real-machine, that during
    // this bridge's own AddNew flow (bridgeActive=True), obtainedText[7]
    // (the "obtained08" row) has its TEXT content swapped to the mutated
    // skill's name, and its <material=...> tag alternates TMC00<->TMC21 in
    // sync with the forget-confirmation dialog. That strongly suggests
    // TMC21 IS (or is adjacent to) the highlight/selection material - but
    // text+material alone don't explain a *missing* highlight *frame*.
    //
    // statusUI itself also declares GameObject[] skillCurObj, GameObject[]
    // skill_base, and GameObject[] update_skill - all strong candidates
    // for the actual highlight-box/cursor-frame objects (as opposed to the
    // text's own color). This trace reads all of these arrays' lengths and
    // per-index activeSelf/activeInHierarchy state directly off the live
    // statusUI instance, correlated with obtainedText[i]'s current string,
    // to see whether the array that owns the swapped row's TEXT is the
    // same array (same index count) as the one that owns its HIGHLIGHT -
    // and whether the highlight-side object actually gets toggled when the
    // text-side one is content-swapped for this bridge's hidden entry.
    //
    // Kept to the same discipline as the sibling traces: seq21/22 gate
    // only, sampled at most once every 15 frames, logged only when the
    // built snapshot string changes since the last sample.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class StatusUiArrayFieldTrace
    {
        internal static readonly bool Enabled = true;

        private const int SampleEveryNFrames = 15;

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

                int frame = UnityEngine.Time.frameCount;
                if (frame % SampleEveryNFrames != 0) return;

                var raw = UnityEngine.Object.FindObjectsOfType(
                    Il2CppInterop.Runtime.Il2CppType.Of<statusUI>());
                if (raw == null || raw.Length == 0) return;

                var ui = raw[0].Cast<statusUI>();
                if (ui == null) return;

                int unit = -1;
                try
                {
                    var stock = gbwk.pCurrentStock;
                    if (stock != null && stock.Pointer != IntPtr.Zero) unit = stock.id;
                }
                catch { /* leave unit=-1 */ }

                var sb = new StringBuilder();
                sb.Append(
                    $"seq={seq}; bridgeActive={FullCapacityAddNewBridgeState.Active}; " +
                    $"unit={unit}; eventParam={gbwk.EventParam}; " +
                    $"eventNums={gbwk.EventNums}; eventOfs={gbwk.EventOfs}; ");

                AppendArrayLen(sb, "obtainedText", ui.obtainedText?.Length ?? -1);
                AppendArrayLen(sb, "obtainedObj", ui.obtainedObj?.Length ?? -1);
                AppendArrayLen(sb, "skillCurObj", ui.skillCurObj?.Length ?? -1);
                AppendArrayLen(sb, "skill_base", ui.skill_base?.Length ?? -1);
                AppendArrayLen(sb, "update_skill", ui.update_skill?.Length ?? -1);
                AppendArrayLen(sb, "awaitText", ui.awaitText?.Length ?? -1);
                AppendArrayLen(sb, "awaitObj", ui.awaitObj?.Length ?? -1);
                AppendArrayLen(sb, "await2Obj", ui.await2Obj?.Length ?? -1);

                int n = ui.obtainedText?.Length ?? 0;
                for (int i = 0; i < n; i++)
                {
                    string text = "";
                    try { text = ui.obtainedText[i]?.text ?? ""; } catch { text = "?"; }

                    bool obtainedObjActive = SafeActive(ui.obtainedObj, i);
                    bool curObjActive = SafeActive(ui.skillCurObj, i);
                    bool baseObjActive = SafeActive(ui.skill_base, i);
                    bool updObjActive = SafeActive(ui.update_skill, i);

                    sb.Append(
                        $" | [{i}] text=\"{Escape(text)}\" obtainedObjActive={obtainedObjActive} " +
                        $"curObjActive={curObjActive} baseObjActive={baseObjActive} updObjActive={updObjActive}");
                }

                // "await" (singular) is the 3-column pending/candidate box
                // seen live in the High Pixie screenshot (2026-09-14) -
                // metrically SEPARATE from the 8 obtained slots, not a
                // content-swap of obtainedText[7]. That screenshot showed
                // this box WITH a visible highlight (teal background) on
                // the pending skill row while curriculum was still
                // pending; Frost's screenshot (curriculum exhausted) shows
                // no such third column at all. This is now the primary
                // suspect over skillCurObj[7].
                // 2026-09-14: static disassembly of VA 0x1822DA3FB found a
                // DEDICATED target==8 code path (separate from the
                // ebx=0..7 highlight loop at VA 0x1822D97C0) that renders
                // its own name/icon and calls cmpSetupObject(skillCurObj[8],
                // true) - skillCurObj[8] (NOT part of the 0..7 obtained
                // row loop above, which only ever indexes 0..obtainedText.
                // Length-1) is that dedicated path's own highlight object.
                // Logged unconditionally (not just when a hidden entry is
                // suspected) so its full lifecycle - activeSelf through
                // this same file's activeInHierarchy/alpha/geometry checks -
                // can be correlated after the fact with whenever
                // CursorPos.Shift/target actually equals 8.
                {
                    bool active8 = SafeActive(ui.skillCurObj, 8);
                    bool inHierarchy8 = SafeActiveInHierarchy(ui.skillCurObj, 8);
                    float alpha8 = SafeCanvasGroupAlpha(ui.skillCurObj, 8);
                    string geom8 = GeometryDump(ui.skillCurObj, 8, null);
                    sb.Append(
                        $" | skillCurObj[8] active={active8} inHierarchy={inHierarchy8} " +
                        $"alpha={alpha8:F2} {geom8}");
                }

                int nAwait = ui.awaitText?.Length ?? 0;
                for (int i = 0; i < nAwait; i++)
                {
                    string text = "";
                    try { text = ui.awaitText[i]?.text ?? ""; } catch { text = "?"; }
                    bool active = SafeActive(ui.awaitObj, i);
                    bool inHierarchy = SafeActiveInHierarchy(ui.awaitObj, i);
                    float alpha = SafeCanvasGroupAlpha(ui.awaitObj, i);
                    string animState = AnimatorStateOf(ui.awaitObj, i);
                    string geom = GeometryDump(ui.awaitObj, i, ui.awaitText != null && i < ui.awaitText.Length ? ui.awaitText[i] : null);

                    sb.Append(
                        $" | await[{i}] text=\"{Escape(text)}\" active={active} " +
                        $"inHierarchy={inHierarchy} alpha={alpha:F2} animState={animState} {geom}");
                }

                int nAwait2 = ui.await2Obj?.Length ?? 0;
                for (int i = 0; i < nAwait2; i++)
                {
                    bool active = SafeActive(ui.await2Obj, i);
                    bool inHierarchy = SafeActiveInHierarchy(ui.await2Obj, i);
                    float alpha = SafeCanvasGroupAlpha(ui.await2Obj, i);
                    string text = "";
                    try
                    {
                        var go = (ui.await2Obj != null && i < ui.await2Obj.Length) ? ui.await2Obj[i] : null;
                        if (go != null)
                        {
                            var tmp = go.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>();
                            text = tmp?.text ?? "";
                        }
                    }
                    catch { text = "?"; }
                    string animState = AnimatorStateOf(ui.await2Obj, i);

                    sb.Append(
                        $" | await2[{i}] active={active} inHierarchy={inHierarchy} alpha={alpha:F2} " +
                        $"text=\"{Escape(text)}\" animState={animState}");
                }

                string snapshot = sb.ToString();
                if (snapshot == _lastSnapshot) return;
                _lastSnapshot = snapshot;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] STATUSUI-ARRAY-TRACE; " +
                    $"frame={frame}; {snapshot}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] StatusUiArrayFieldTrace postfix failed safely: {ex.Message}");
            }
        }

        // sskill_await2_01's own ancestor GameObject carries an Animator
        // (not an Image/Graphic) per ForgetUiHierarchyDumpTrace's earlier
        // component dump - the highlight seen in the High Pixie screenshot
        // may be an Animator-driven color/scale state rather than a plain
        // SetActive toggle, which SkillCurObjSetActiveTrace would miss
        // entirely. Reads the currently-playing state's hash + normalized
        // time as a cheap fingerprint (full state names aren't reliably
        // available without the original AnimatorController asset).
        private static string AnimatorStateOf(UnityEngine.GameObject[] arr, int i)
        {
            try
            {
                if (arr == null || i >= arr.Length || arr[i] == null) return "n/a";
                var anim = arr[i].GetComponent<UnityEngine.Animator>();
                if (anim == null) return "noAnimator";
                var info = anim.GetCurrentAnimatorStateInfo(0);
                return $"hash={info.fullPathHash};t={info.normalizedTime:F2}";
            }
            catch
            {
                return "err";
            }
        }

        // Runtime data this session (2026-09-14) showed await[0]/await2[0]
        // reporting activeSelf=True with correct text for Frost even
        // though the reference screenshot showed no 3rd column on screen
        // at all. activeSelf being True does not guarantee the object is
        // actually rendered - an inactive ANCESTOR (activeInHierarchy) or
        // a CanvasGroup.alpha==0 somewhere up the parent chain would both
        // produce exactly this "logically active but invisible" symptom,
        // and neither is visible from activeSelf alone. Checks up to 6
        // ancestor levels for the nearest CanvasGroup.
        private static bool SafeActiveInHierarchy(UnityEngine.GameObject[] arr, int i)
        {
            try
            {
                if (arr == null || i >= arr.Length || arr[i] == null) return false;
                return arr[i].activeInHierarchy;
            }
            catch { return false; }
        }

        private static float SafeCanvasGroupAlpha(UnityEngine.GameObject[] arr, int i)
        {
            try
            {
                if (arr == null || i >= arr.Length || arr[i] == null) return -1f;
                var t = arr[i].transform;
                int guard = 0;
                while (t != null && guard < 6)
                {
                    var cg = t.GetComponent<UnityEngine.CanvasGroup>();
                    if (cg != null) return cg.alpha;
                    t = t.parent;
                    guard++;
                }
                return -1f; // no CanvasGroup found in the checked ancestor range
            }
            catch { return -2f; }
        }

        // Next suspect per User's 2026-09-14 decision tree: activeSelf=True
        // / activeInHierarchy=True / no CanvasGroup dimming (alpha=-1,
        // i.e. none found) still doesn't prove the row is actually ON
        // SCREEN - it could be logically active but positioned off-canvas
        // (RectTransform), zero-scaled, clipped by an ancestor Mask/
        // RectMask2D, or explicitly culled at the render level
        // (CanvasRenderer.cull / GetAlpha()==0) even with GameObject-level
        // alpha untouched. Dumps all of these for one row at once so
        // High Pixie (visible) and Frost (not visible) can be diffed
        // directly on this single line.
        private static string GeometryDump(UnityEngine.GameObject[] arr, int i, Il2CppTMPro.TextMeshProUGUI tmp)
        {
            try
            {
                if (arr == null || i >= arr.Length || arr[i] == null) return "geom=n/a";

                var rt = arr[i].GetComponent<UnityEngine.RectTransform>();
                string rectStr = "rect=n/a";
                if (rt != null)
                {
                    rectStr =
                        $"anchoredPos={rt.anchoredPosition} localPos={rt.localPosition} " +
                        $"localScale={rt.localScale} sizeDelta={rt.sizeDelta}";
                }

                string rendererStr = "renderer=n/a";
                try
                {
                    if (tmp != null)
                    {
                        var cr = tmp.canvasRenderer;
                        if (cr != null)
                        {
                            rendererStr = $"cull={cr.cull} rendererAlpha={cr.GetAlpha():F2}";
                        }
                    }
                }
                catch { rendererStr = "renderer=err"; }

                string maskStr = "noMaskAncestor";
                try
                {
                    var t = arr[i].transform;
                    int guard = 0;
                    while (t != null && guard < 8)
                    {
                        if (t.GetComponent<UnityEngine.UI.Mask>() != null) { maskStr = $"Mask@{t.name}"; break; }
                        if (t.GetComponent<UnityEngine.UI.RectMask2D>() != null) { maskStr = $"RectMask2D@{t.name}"; break; }
                        t = t.parent;
                        guard++;
                    }
                }
                catch { maskStr = "maskCheckErr"; }

                return $"{rectStr} {rendererStr} {maskStr}";
            }
            catch
            {
                return "geom=err";
            }
        }

        private static bool SafeActive(UnityEngine.GameObject[] arr, int i)
        {
            try
            {
                if (arr == null || i >= arr.Length) return false;
                var go = arr[i];
                return go != null && go.activeSelf;
            }
            catch
            {
                return false;
            }
        }

        private static void AppendArrayLen(StringBuilder sb, string name, int len)
        {
            sb.Append($"{name}Len={len}; ");
        }

        private static string Escape(string s)
        {
            return s.Replace("\n", "\\n").Replace(";", ",").Replace("|", "/");
        }
    }
}
