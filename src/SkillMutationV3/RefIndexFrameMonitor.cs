using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // DEFAULTSKILL REFINDEX ROLLBACK INVESTIGATION - FRAME-BOUNDARY TIMING.
    // Read-only observer only. Never writes any field, never touches native
    // code (no new hardware breakpoint, no inline/native hook - this only
    // ADDS more read frequency to an already-hooked, already-safe Postfix
    // pattern that several other diagnostics in this file already use:
    // rstupdate.rstUpdate(), the per-frame top-level dispatcher).
    //
    // Purpose: DefaultSkillExternalStateProbe already proved (via its
    // rstCalcEventInfo Prefix/Postfix cross-call diff) that refTableObj+0x24
    // ("refIndex") genuinely DECREASES by exactly 1 across some
    // rstCalcEventInfo call gaps, with refTableObj/counterObj identity and
    // counter/tableBuiltFlag all UNCHANGED - and separately proved the
    // bit0x200-gated overwrite inside twin_commit_advance (VA 0x196547EFE-
    // F02) is NOT the cause (bit0x200 was observed False in every single
    // rollback event). Since rstCalcEventInfo is only called sparingly
    // (during actual classification), its own before/after snapshots can
    // straddle many frames and many SeqInfo.Current transitions at once -
    // too coarse to isolate WHICH transition (seq21->22, 22->10, 8->8, etc)
    // the decrement happens next to. This class polls the SAME refIndex
    // value once per frame (via rstupdate.rstUpdate's existing per-frame
    // Postfix hook) and logs ONLY when the value actually changes,
    // alongside SeqInfo.Current/Last/Change and the other fields the
    // investigation asked for - turning a "which call-gap" question into a
    // "which frame, right after which seq transition" answer.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class RefIndexFrameMonitor
    {
        internal static readonly bool Enabled = true;

        // Same static-slot derivation as DefaultSkillExternalStateProbe -
        // see that file for the full offline VA computation and cross-check
        // against the cpp2il ISIL literal address. Duplicated here (rather
        // than shared) to keep this frame-polling probe fully self-
        // contained and independently toggleable.
        private const long RefTableSlotRva = 0x182E464B8L - 0x180000000L;

        private static IntPtr _moduleBase = IntPtr.Zero;
        private static bool _moduleBaseResolved;

        private static bool _haveLast;
        private static short _lastRefIndex;
        private static long _lastRefTableObj;
        private static int _lastUnit = int.MinValue;

        private static IntPtr ResolveModuleBase()
        {
            if (_moduleBaseResolved) return _moduleBase;
            _moduleBaseResolved = true;
            try
            {
                _moduleBase = GetModuleHandle("GameAssembly.dll");
            }
            catch
            {
                _moduleBase = IntPtr.Zero;
            }
            return _moduleBase;
        }

        private static bool TryReadRefIndex(IntPtr moduleBase, out short refIndex, out long refTableObj)
        {
            refIndex = 0;
            refTableObj = 0;
            try
            {
                long slotAddr = moduleBase.ToInt64() + RefTableSlotRva;
                long q1 = Marshal.ReadInt64(new IntPtr(slotAddr));
                if (q1 == 0) return false;
                long obj1 = Marshal.ReadInt64(new IntPtr(q1 + 0xb8));
                if (obj1 == 0) return false;
                long obj2 = Marshal.ReadInt64(new IntPtr(obj1), 0);
                if (obj2 == 0) return false;
                long refTable = Marshal.ReadInt64(new IntPtr(obj2 + 0x68));
                if (refTable == 0) return false;
                refTableObj = refTable;
                refIndex = Marshal.ReadInt16(new IntPtr(refTable), 0x24);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                var moduleBase = ResolveModuleBase();
                if (moduleBase == IntPtr.Zero) return;

                if (!TryReadRefIndex(moduleBase, out short refIndex, out long refTableObj)) return;

                bool changed = !_haveLast || refIndex != _lastRefIndex || refTableObj != _lastRefTableObj;
                if (!changed)
                {
                    return;
                }

                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                int unit = stock?.id ?? -1;
                int frame = UnityEngine.Time.frameCount;

                short prevRefIndex = _lastRefIndex;
                long prevRefTableObj = _lastRefTableObj;
                bool hadLast = _haveLast;

                _haveLast = true;
                _lastRefIndex = refIndex;
                _lastRefTableObj = refTableObj;
                _lastUnit = unit;

                if (!hadLast) return; // first observation - nothing to diff yet

                int seqCurrent = gbwk?.SeqInfo.Current ?? -1;
                int seqLast = gbwk?.SeqInfo.Last ?? -1;
                sbyte seqChange = gbwk?.SeqInfo.Change ?? 0;
                sbyte eventNums = gbwk?.EventNums ?? 0;
                sbyte eventOfs = gbwk?.EventOfs ?? 0;
                ushort eventParam = gbwk?.EventParam ?? 0;
                sbyte defSkillResult = gbwk?.DefSkillResult ?? 0;
                sbyte flag = gbwk?.Flag ?? 0;
                sbyte pUpSkillResult = gbwk?.PUpSkillResult ?? 0;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] REFINDEX-FRAME-CHANGE; " +
                    $"frame={frame}; unit={unit}; " +
                    $"refTableObjSame={refTableObj == prevRefTableObj}; " +
                    $"refTableObj 0x{prevRefTableObj:X}->0x{refTableObj:X}; " +
                    $"refIndex {prevRefIndex}->{refIndex}; " +
                    $"seqCurrent={seqCurrent}; seqLast={seqLast}; seqChange={seqChange}; " +
                    $"eventNums={eventNums}; eventOfs={eventOfs}; eventParam={eventParam}; " +
                    $"defSkillResult={defSkillResult}; flag={flag}; pUpSkillResult={pUpSkillResult}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] RefIndexFrameMonitor postfix failed safely: {ex.Message}");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }
}
