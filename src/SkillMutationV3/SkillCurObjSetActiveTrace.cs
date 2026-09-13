using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY (master-archive.md Section 22) - SetActive
    // writer trace (read-only, no writes). Session continuation after
    // StatusUiArrayFieldTrace.cs CONFIRMED skillCurObj[i].activeSelf
    // correlates 1:1 with the highlight material, and that index 7 never
    // activated for Frost's swapped/hidden row while other indices did.
    //
    // statusUI itself (IL2CPP metadata-confirmed) has exactly THREE
    // managed methods: Awake, OnDisable, .ctor - no Update, no
    // Refresh/SetSkill-shaped method at all. Every visible field
    // (obtainedText, skillCurObj, etc.) is a plain public [SerializeField]
    // array, so whatever actually calls skillCurObj[i].SetActive(...) must
    // live in a DIFFERENT class that holds a reference to this statusUI
    // instance and pokes its fields directly. Rather than blind-scanning
    // the whole binary for instructions touching instance offset 0x128
    // (skillCurObj's field offset per cpp2il, ambiguous against unrelated
    // classes), this hooks UnityEngine.GameObject.SetActive globally and
    // filters, at the native-pointer level, to only the ~16 GameObjects
    // that are actually statusUI.skillCurObj[i] - cheap per-call check via
    // a pointer->index Dictionary, refreshed periodically in case the
    // statusUI instance is recreated (e.g. across a scene/menu reopen).
    //
    // This does NOT capture the true caller (Harmony can't unwind a
    // native-compiled IL2CPP call stack from a managed Prefix), but it
    // does capture WHEN each skillCurObj[i] is toggled and to what value,
    // which directly answers whether index 7 is ever written at all during
    // Frost's window (never called => skipped entirely; called with False
    // => explicitly cleared/never set True; called with True but we still
    // saw activeSelf=False on the next poll => something else immediately
    // toggles it back off).
    [HarmonyPatch(typeof(UnityEngine.GameObject), nameof(UnityEngine.GameObject.SetActive))]
    internal static class SkillCurObjSetActiveTrace
    {
        internal static readonly bool Enabled = true;

        private static Dictionary<IntPtr, int> _pointerToIndex;
        private static int _lastRefreshFrame = int.MinValue;
        private const int RefreshEveryNFrames = 300;

        internal static void LogPatchStatus()
        {
            try
            {
                var method = typeof(UnityEngine.GameObject).GetMethod(nameof(UnityEngine.GameObject.SetActive));
                if (method == null)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILLCUROBJ-SETACTIVE-PATCH-STATUS; " +
                        "methodInfo=NULL (GetMethod failed to resolve GameObject.SetActive).");
                    return;
                }

                var info = HarmonyLib.Harmony.GetPatchInfo(method);
                int prefixes = info?.Prefixes?.Count ?? 0;
                IntPtr fnPtr = method.MethodHandle.GetFunctionPointer();
                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLCUROBJ-SETACTIVE-PATCH-STATUS; " +
                    $"methodInfo=FOUND; prefixes={prefixes}; functionPointer=0x{fnPtr.ToInt64():X}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCurObjSetActiveTrace.LogPatchStatus failed: {ex}");
            }
        }

        private static void RefreshCacheIfNeeded(int frame)
        {
            if (_pointerToIndex != null && frame - _lastRefreshFrame < RefreshEveryNFrames) return;
            _lastRefreshFrame = frame;

            try
            {
                var raw = UnityEngine.Object.FindObjectsOfType(
                    Il2CppInterop.Runtime.Il2CppType.Of<statusUI>());
                if (raw == null || raw.Length == 0) { _pointerToIndex = null; return; }

                var ui = raw[0].Cast<statusUI>();
                var arr = ui?.skillCurObj;
                if (arr == null) { _pointerToIndex = null; return; }

                var map = new Dictionary<IntPtr, int>();
                for (int i = 0; i < arr.Length; i++)
                {
                    var go = arr[i];
                    if (go != null && go.Pointer != IntPtr.Zero)
                    {
                        map[go.Pointer] = i;
                    }
                }
                _pointerToIndex = map;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLCUROBJ-CACHE-REFRESH; " +
                    $"frame={frame}; trackedCount={map.Count}; arrayLen={arr.Length}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCurObjSetActiveTrace cache refresh failed: {ex.Message}");
                _pointerToIndex = null;
            }
        }

        private static void Prefix(UnityEngine.GameObject __instance, bool value)
        {
            if (!Enabled) return;

            try
            {
                if (__instance == null) return;

                int frame = UnityEngine.Time.frameCount;
                RefreshCacheIfNeeded(frame);
                if (_pointerToIndex == null || _pointerToIndex.Count == 0) return;

                if (!_pointerToIndex.TryGetValue(__instance.Pointer, out int index)) return;

                var gbwk = rstinit.GBWK;
                int seq = (gbwk != null && gbwk.Pointer != IntPtr.Zero) ? gbwk.SeqInfo.Current : -1;
                bool bridgeActive = FullCapacityAddNewBridgeState.Active;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILLCUROBJ-SETACTIVE; " +
                    $"frame={frame}; index={index}; value={value}; seq={seq}; bridgeActive={bridgeActive}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillCurObjSetActiveTrace prefix failed safely: {ex.Message}");
            }
        }
    }
}
