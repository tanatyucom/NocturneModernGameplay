using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - direct hook on the real writer (read-only,
    // no writes). Session continuation after SkillCurObjNativeCallerProbe
    // (hardware breakpoint on GameObject.SetActive's entry, filtered to
    // skillCurObj[0..15]'s pointers) caught a live return address that
    // resolved to cmpUpdate.cmpSetupObject <- cmpUpdate.cmpMenuCursor(Int32
    // idx, GameObject CursorObj, GameObject[] CursorList) - an
    // input/cursor-logic class, NOT the cmpDrawStatus draw chain this
    // investigation had been searching until then.
    //
    // Static disassembly of cmpMenuCursor (VA 0x1826207F0) found the exact
    // gate, byte-confirmed:
    //   cmp edi, dword ptr [r8+0x18]   ; idx vs CursorList.Length
    //   jge <return>                    ; if idx >= Length, skip the
    //                                    ; cmpSetupObject(CursorObj, true)
    //                                    ; call ENTIRELY - never reached
    // CONFIRMED only as far as: this gate exists and this is real code
    // that runs. NOT yet confirmed: that a genuine hidden-entry selection
    // moment actually drives idx >= CursorList.Length (the hardware-
    // breakpoint capture that found this function was index=8's ordinary
    // blink cycle, not necessarily the failing case itself).
    //
    // This hook settles that directly: it reads idx/CursorList.Length/the
    // out-of-range verdict straight from cmpMenuCursor's own real
    // arguments whenever CursorObj is one of the known skillCurObj[]
    // elements (cheap pointer-array scan, same filtering discipline as
    // SkillCurObjSetActiveTrace.cs), alongside the same context fields
    // this investigation has used throughout (seq, bridgeActive,
    // CursorPos.Index/Shift, SelectSkillID, stock.skill[0..7], EventParam)
    // so a "ブフ" (normal, highlighted) call and a "会心"/"息吹の具足"
    // (hidden, not highlighted) call can be diffed directly on idx and
    // CursorList.Length.
    [HarmonyPatch(typeof(cmpUpdate), nameof(cmpUpdate.cmpMenuCursor))]
    internal static class CmpMenuCursorTrace
    {
        internal static readonly bool Enabled = true;

        private static Dictionary<IntPtr, int>? _skillCurObjIndex;
        private static int _lastRefreshFrame = int.MinValue;
        private const int RefreshEveryNFrames = 300;

        private static string _lastSnapshot = "";

        internal static void LogPatchStatus()
        {
            try
            {
                var method = typeof(cmpUpdate).GetMethod(nameof(cmpUpdate.cmpMenuCursor));
                if (method == null)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] CMPMENUCURSOR-PATCH-STATUS; " +
                        "methodInfo=NULL (GetMethod failed to resolve cmpUpdate.cmpMenuCursor).");
                    return;
                }

                var info = HarmonyLib.Harmony.GetPatchInfo(method);
                int prefixes = info?.Prefixes?.Count ?? 0;
                IntPtr fnPtr = method.MethodHandle.GetFunctionPointer();
                MelonLogger.Msg(
                    "[NocturneModernGameplay] CMPMENUCURSOR-PATCH-STATUS; " +
                    $"methodInfo=FOUND; prefixes={prefixes}; functionPointer=0x{fnPtr.ToInt64():X}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] CmpMenuCursorTrace.LogPatchStatus failed: {ex}");
            }
        }

        private static void RefreshSkillCurObjIndex(int frame)
        {
            if (_skillCurObjIndex != null && frame - _lastRefreshFrame < RefreshEveryNFrames) return;
            _lastRefreshFrame = frame;

            try
            {
                var raw = UnityEngine.Object.FindObjectsOfType(
                    Il2CppInterop.Runtime.Il2CppType.Of<statusUI>());
                if (raw == null || raw.Length == 0) { _skillCurObjIndex = null; return; }

                var ui = raw[0].Cast<statusUI>();
                var arr = ui?.skillCurObj;
                if (arr == null) { _skillCurObjIndex = null; return; }

                var map = new Dictionary<IntPtr, int>();
                for (int i = 0; i < arr.Length; i++)
                {
                    var go = arr[i];
                    if (go != null && go.Pointer != IntPtr.Zero) map[go.Pointer] = i;
                }
                _skillCurObjIndex = map;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] CmpMenuCursorTrace cache refresh failed: {ex.Message}");
                _skillCurObjIndex = null;
            }
        }

        private static long _callCount;
        private static long _cursorObjNullCount;
        private static long _cacheNullCount;
        private static long _cacheMissCount;

        private static void Prefix(int idx, UnityEngine.GameObject CursorObj, UnityEngine.GameObject[] CursorList)
        {
            if (!Enabled) return;

            try
            {
                _callCount++;
                bool cursorObjNull = CursorObj == null || CursorObj.Pointer == IntPtr.Zero;
                if (cursorObjNull) _cursorObjNullCount++;

                if (_callCount == 1 || _callCount % 500 == 0)
                {
                    int fFrame = UnityEngine.Time.frameCount;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] CMPMENUCURSOR-DIAG-HEARTBEAT; " +
                        $"frame={fFrame}; callCount={_callCount}; idx={idx}; cursorObjNull={cursorObjNull}; " +
                        $"cursorListNull={CursorList == null}; cursorListLen={CursorList?.Length ?? -1}; " +
                        $"cacheNullCount={_cacheNullCount}; cacheMissCount={_cacheMissCount}; " +
                        $"cursorObjNullCount={_cursorObjNullCount}.");
                }
                if (CursorObj == null || CursorObj.Pointer == IntPtr.Zero) return;

                int frame = UnityEngine.Time.frameCount;
                RefreshSkillCurObjIndex(frame);
                if (_skillCurObjIndex == null) { _cacheNullCount++; return; }
                if (!_skillCurObjIndex.TryGetValue(CursorObj.Pointer, out int skillCurObjIdx)) { _cacheMissCount++; return; }

                int length = CursorList?.Length ?? -1;
                bool outOfRange = length < 0 || idx >= length;

                var gbwk = rstinit.GBWK;
                int seq = -1;
                bool bridgeActive = FullCapacityAddNewBridgeState.Active;
                short cursorIndex = -1;
                byte cursorShift = 0;
                ushort selectSkillID = 0;
                int eventParam = -1;
                string skillArr = "?";

                if (gbwk != null && gbwk.Pointer != IntPtr.Zero)
                {
                    try { seq = gbwk.SeqInfo.Current; } catch { }
                    try { selectSkillID = gbwk.SelectSkillID; } catch { }
                    try { eventParam = gbwk.EventParam; } catch { }
                    try
                    {
                        var cursorPos = gbwk.SkillCursor?.CursorPos;
                        if (cursorPos != null)
                        {
                            cursorIndex = cursorPos.Index;
                            cursorShift = cursorPos.Shift;
                        }
                    }
                    catch { }
                    try
                    {
                        var stock = gbwk.pCurrentStock;
                        if (stock != null && stock.Pointer != IntPtr.Zero)
                        {
                            var skill = stock.skill;
                            if (skill != null)
                            {
                                var vals = new List<int>();
                                for (int i = 0; i < 8 && i < skill.Length; i++) vals.Add(skill[i]);
                                skillArr = string.Join(",", vals);
                            }
                        }
                    }
                    catch { }
                }

                string snapshot =
                    $"skillCurObjIdx={skillCurObjIdx}; idx={idx}; cursorListLen={length}; outOfRange={outOfRange}; " +
                    $"seq={seq}; bridgeActive={bridgeActive}; cursorIndex={cursorIndex}; cursorShift={cursorShift}; " +
                    $"selectSkillID={selectSkillID}; eventParam={eventParam}; stockSkill=[{skillArr}]";

                if (snapshot == _lastSnapshot) return;
                _lastSnapshot = snapshot;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] CMPMENUCURSOR-TRACE; " +
                    $"frame={frame}; {snapshot}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] CmpMenuCursorTrace prefix failed safely: {ex.Message}");
            }
        }
    }
}
