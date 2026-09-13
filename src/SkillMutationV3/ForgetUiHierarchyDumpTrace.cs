using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - pivot to
    // Unity UI (read-only, no writes). Session continuation after
    // SkillDrawListEntryTrace.cs / SkillNameCostDrawFieldTrace.cs /
    // SkillMakeStrColFieldTrace.cs all CONFIRMED (via ungated call
    // counters, zero hits across a full seq21/22 play session with the
    // forget-skill list visibly on screen) that none of the native
    // cmpDrawSkill / cmpMisc functions this investigation had been
    // chasing are ever invoked in this build. Static disassembly-based
    // identification of the row-draw path is therefore REJECTED for this
    // HD-remaster build; this game's actual skill-list rendering almost
    // certainly goes through Unity's own UI (TextMeshProUGUI/Canvas)
    // rather than the legacy native "cmp" draw functions, consistent with
    // cmpPanel.cmpDrawDevilName's own signature already accepting an
    // optional TextMeshProUGUI parameter (partial hybrid retrofit).
    //
    // Purpose: enumerate every TextMeshProUGUI in the scene while the
    // forget UI's logic-side state (SeqInfo.Current 21/22) is active, find
    // which GameObjects actually carry skill-name text (e.g. a
    // learn-as-new mutated skill's name), and read off the attached
    // MonoBehaviour/Component names on that object and its immediate
    // parent - the parent's component list is the most likely place to
    // find the real managing class name (something like a
    // "SkillListPanel"/"DevilSkillWindow"-shaped MonoBehaviour), which is
    // this investigation's actual target: whatever component owns that
    // class is the next thing to reverse-engineer/hook, in place of the
    // now-REJECTED native cmp functions.
    //
    // Kept deliberately narrow per explicit design agreement this session:
    //   - seq21/22 gate only (same window as every sibling trace)
    //   - TMP text must be non-empty
    //   - logged only on first-seen-or-changed text per hierarchy path
    //     (a Dictionary<path,text> dedup), not every sampled tick
    //   - sampled at most once every 15 frames (not every frame) while the
    //     gate is open, to bound both CPU cost (FindObjectsOfType scans
    //     the whole scene) and log volume
    // This does not attempt to pre-filter to "looks like a skill name" -
    // that requires knowing the skill name table ahead of time, which is
    // exactly the kind of speculative narrowing this investigation's own
    // Evidence Discipline (00_PROJECT_RULES.md) warns against. Any
    // non-empty, changed TMP text during this window is logged; a human
    // (or a later pass) picks out the skill-name-looking entries from a
    // much smaller, deduplicated candidate set.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class ForgetUiHierarchyDumpTrace
    {
        internal static readonly bool Enabled = true;

        private const int SampleEveryNFrames = 15;

        private static readonly Dictionary<string, string> _lastTextByPath =
            new Dictionary<string, string>();

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

                var texts = UnityEngine.Object.FindObjectsOfType(
                    Il2CppInterop.Runtime.Il2CppType.Of<Il2CppTMPro.TextMeshProUGUI>());
                if (texts == null) return;

                foreach (var raw in texts)
                {
                    if (raw == null) continue;
                    var tmp = raw.Cast<Il2CppTMPro.TextMeshProUGUI>();
                    if (tmp == null) continue;

                    string text = tmp.text;
                    if (string.IsNullOrEmpty(text)) continue;

                    string path = BuildPath(tmp.transform);

                    if (_lastTextByPath.TryGetValue(path, out var prevText) && prevText == text)
                        continue;
                    _lastTextByPath[path] = text;

                    var go = tmp.gameObject;
                    bool activeSelf = go.activeSelf;
                    bool activeInHierarchy = go.activeInHierarchy;

                    string selfComponents = ListComponents(go);

                    // Walk up to 4 ancestor levels - the actual managing
                    // MonoBehaviour (a status-panel/list-controller script)
                    // is more likely on a container a few levels up (e.g.
                    // "statusUI(Clone)" itself) than on the immediate
                    // parent, which is often just a layout row prefab
                    // instance with purely visual components.
                    var ancestors = new List<string>();
                    var cur = tmp.transform.parent;
                    int levels = 0;
                    while (cur != null && levels < 4)
                    {
                        ancestors.Add($"{cur.name}:[{ListComponents(cur.gameObject)}]");
                        cur = cur.parent;
                        levels++;
                    }
                    string ancestorDump = string.Join(" > ", ancestors);

                    MelonLogger.Msg(
                        "[NocturneModernGameplay] FORGETUI-TMP-TRACE; " +
                        $"frame={frame}; seq={seq}; bridgeActive={FullCapacityAddNewBridgeState.Active}; " +
                        $"path={path}; activeSelf={activeSelf}; activeInHierarchy={activeInHierarchy}; " +
                        $"text=\"{Escape(text)}\"; selfComponents=[{selfComponents}]; " +
                        $"ancestors=({ancestorDump}).");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] ForgetUiHierarchyDumpTrace postfix failed safely: {ex.Message}");
            }
        }

        private static string BuildPath(UnityEngine.Transform t)
        {
            var segments = new List<string>();
            var cur = t;
            int guard = 0;
            while (cur != null && guard < 32)
            {
                segments.Add(cur.name);
                cur = cur.parent;
                guard++;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        private static string ListComponents(UnityEngine.GameObject go)
        {
            try
            {
                var comps = go.GetComponents<UnityEngine.Component>();
                if (comps == null) return "";
                var names = new List<string>();
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    // c.GetType().Name is the STATIC wrapper type (often
                    // just "Component"/"MonoBehaviour") for Il2Cpp objects -
                    // GetIl2CppType() queries the actual runtime type,
                    // which is what we need to find the real managing
                    // MonoBehaviour class name (e.g. a status-panel script).
                    string name;
                    try
                    {
                        name = c.GetIl2CppType()?.FullName ?? c.GetType().Name;
                    }
                    catch
                    {
                        name = c.GetType().Name;
                    }
                    names.Add(name);
                }
                return string.Join(",", names);
            }
            catch
            {
                return "?";
            }
        }

        private static string Escape(string s)
        {
            return s.Replace("\n", "\\n").Replace(";", ",");
        }
    }
}
