using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - runtime field-offset measurement (read-only,
    // no writes). Session continuation after a static-disassembly hunt for
    // skillCurObj[i].SetActive()'s caller found ZERO direct call sites
    // anywhere in the confirmed-live draw chain (cmpStatus.cmpUpdateStatus
    // -> cmpDrawStatus.cmpDrawStatusCom -> ComEx -> ComEx2 ->
    // cmpDrawStatus.cmpDrawSkill), despite that chain being independently
    // verified alive (it calls datSkillName.Get, matching a fix already
    // recorded elsewhere in this project's history).
    //
    // The static search used cpp2il's REPORTED field offset for
    // skillCurObj (0x128 on statusUI) to filter ~1000 raw SetActive call
    // sites down to plausible ones. cpp2il's offsets are estimates and can
    // be wrong (base-class field layout, padding, etc.) - if 0x128 is
    // wrong, every single byte-pattern search based on it was searching
    // for the wrong needle from the start, which would fully explain zero
    // hits even in verified-live code.
    //
    // This class measures the REAL runtime offset directly instead of
    // trusting cpp2il: it takes the live statusUI instance's own pointer,
    // and the pointer of the ARRAY OBJECT each field holds (via each
    // array wrapper's own .Pointer, obtained through reflection so this
    // works regardless of the exact Il2CppInterop array wrapper type), then
    // scans a window of statusUI's own instance memory for an 8-byte value
    // matching each array object's pointer - the offset where it's found
    // IS the field's true offset. Cross-checks skillCurObj against several
    // other known fields (obtainedText, obtainedObj, skill_base,
    // update_skill, awaitObj) in the same pass for mutual confirmation.
    //
    // Runs once (first statusUI instance with a populated skillCurObj
    // array found), not per-frame - field offsets are compile-time
    // constants, they don't change with gameplay state.
    //
    // NOT a HarmonyPatch on rstupdate.rstUpdate (as originally written).
    // Real-machine testing showed rstUpdate's own postfix chain (13 other
    // patches) never reached ours even at HarmonyPriority.First and with
    // the patch confirmed registered - meanwhile UnityEngine.Time.
    // frameCount and GameObject.SetActive-driven traces kept advancing
    // normally. That combination means rstUpdate itself (the NATIVE
    // gameplay-logic tick) most likely never fires at all while merely
    // viewing a plain status screen outside the forget-flow context (a
    // menu-only view, distinct from the seq21/22 forget flow every other
    // sibling trace in this investigation was gated on) - not a Harmony
    // chain-abort as first suspected. Driven from ModMain.OnUpdate()
    // instead (a genuine per-Unity-frame MelonMod callback, independent
    // of whether native game logic is ticking) via TryProbe().
    internal static class StatusUiFieldOffsetProbe
    {
        internal static readonly bool Enabled = true;

        private static bool _done = false;
        private const int ScanRangeBytes = 0x400;

        internal static void TryProbe()
        {
            if (!Enabled || _done) return;

            try
            {
                var raw = UnityEngine.Object.FindObjectsOfType(
                    Il2CppInterop.Runtime.Il2CppType.Of<statusUI>());
                if (raw == null || raw.Length == 0) return;

                var ui = raw[0].Cast<statusUI>();
                if (ui == null || ui.skillCurObj == null || ui.skillCurObj.Length == 0) return;

                IntPtr uiPtr = ui.Pointer;
                if (uiPtr == IntPtr.Zero) return;

                _done = true; // only ever attempt this once, success or failure

                MelonLogger.Msg(
                    "[NocturneModernGameplay] STATUSUI-OFFSET-PROBE-START; " +
                    $"statusUIPtr=0x{uiPtr.ToInt64():X}.");

                ProbeField(uiPtr, "skillCurObj", GetArrayPointer(ui.skillCurObj));
                ProbeField(uiPtr, "obtainedText", GetArrayPointer(ui.obtainedText));
                ProbeField(uiPtr, "obtainedObj", GetArrayPointer(ui.obtainedObj));
                ProbeField(uiPtr, "skill_base", GetArrayPointer(ui.skill_base));
                ProbeField(uiPtr, "update_skill", GetArrayPointer(ui.update_skill));
                ProbeField(uiPtr, "awaitObj", GetArrayPointer(ui.awaitObj));
                ProbeField(uiPtr, "awaitText", GetArrayPointer(ui.awaitText));
                ProbeField(uiPtr, "await2Obj", GetArrayPointer(ui.await2Obj));

                // Element pointers, for reference only (not field-offset
                // relevant - these live inside the array object, not
                // statusUI itself - but useful to have logged alongside).
                try
                {
                    IntPtr e0 = (ui.skillCurObj.Length > 0 && ui.skillCurObj[0] != null)
                        ? ui.skillCurObj[0].Pointer : IntPtr.Zero;
                    IntPtr e7 = (ui.skillCurObj.Length > 7 && ui.skillCurObj[7] != null)
                        ? ui.skillCurObj[7].Pointer : IntPtr.Zero;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] STATUSUI-OFFSET-PROBE-ELEMENTS; " +
                        $"skillCurObj[0]=0x{e0.ToInt64():X}; skillCurObj[7]=0x{e7.ToInt64():X}.");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[NocturneModernGameplay] StatusUiFieldOffsetProbe element read failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] StatusUiFieldOffsetProbe postfix failed safely: {ex.Message}");
            }
        }

        private static IntPtr GetArrayPointer(object arrObj)
        {
            if (arrObj == null) return IntPtr.Zero;
            try
            {
                var type = arrObj.GetType();
                var prop = type.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(arrObj);
                    if (val is IntPtr ip) return ip;
                }
            }
            catch { /* fall through */ }
            return IntPtr.Zero;
        }

        private static void ProbeField(IntPtr uiPtr, string fieldName, IntPtr arrayPtr)
        {
            if (arrayPtr == IntPtr.Zero)
            {
                MelonLogger.Msg(
                    "[NocturneModernGameplay] STATUSUI-OFFSET-PROBE; " +
                    $"field={fieldName}; arrayPtr=0x0 (could not resolve array object pointer via reflection).");
                return;
            }

            long target = arrayPtr.ToInt64();
            var foundOffsets = new System.Collections.Generic.List<int>();

            try
            {
                for (int off = 0; off <= ScanRangeBytes - 8; off += 8)
                {
                    long candidate = Marshal.ReadInt64(uiPtr, off);
                    if (candidate == target)
                    {
                        foundOffsets.Add(off);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] StatusUiFieldOffsetProbe scan failed for {fieldName}: {ex.Message}");
                return;
            }

            string offsetsStr = foundOffsets.Count > 0
                ? string.Join(",", foundOffsets.ConvertAll(o => $"0x{o:X}"))
                : "NONE FOUND";

            MelonLogger.Msg(
                "[NocturneModernGameplay] STATUSUI-OFFSET-PROBE; " +
                $"field={fieldName}; arrayPtr=0x{target:X}; matchingOffsetsInStatusUI=[{offsetsStr}].");
        }
    }
}
