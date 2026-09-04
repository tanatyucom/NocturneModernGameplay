using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Read-only investigation telemetry for the native bit6 CLEAR mechanism
    // inside rstupdate.rstUpdateSeqSkillPowerUp (VA 0x18228C770), added this
    // session to observe when/whether the two CLEAR sites actually fire
    // during normal reload-free play (the "bit6 persistence" question -
    // 01_CURRENT_STATE.md, "native bit6 CLEAR siteの通常プレイでの発火
    // タイミング"). Byte-exact static CFG for both sites this session:
    // .analysis/disasm_bit6_clear_sites.py,
    // .analysis/disasm_rstupdateseqskillpowerup_full.py.
    //
    // Static CFG summary (both sites share the same shape):
    //   - Site1 (Flag==4 side): VA 0x18228CCB2, State+0x1C tested at
    //     0x18228CC9F ("cmp dword ptr [state+0x1c], 1" then jne - exact
    //     equality, not a nonzero/AND test). No State+0x1C reset after.
    //   - Site2 (Flag==3 side): VA 0x18228CD89, State+0x1C tested at
    //     0x18228CD60 (same exact-equality form). State+0x1C reset to 0
    //     immediately after, at 0x18228CDAA.
    //   - Both sites clear bit 0x40 on "[source+0x60]+0x10". This session's
    //     disassembly of the Flag dispatch feeding both sites (0x18228CBDB
    //     region) plus the decompiled field list
    //     (.analysis/cpp2il_cs/DiffableCs/Assembly-CSharp/result2_H/
    //     rstData_t.cs) confirms "source" here is rstinit.GBWK itself
    //     (rstData_t), and rstData_t's own +0x60 field IS pCurrentStock -
    //     so both CLEAR sites target GBWK.pCurrentStock.flag bit6, the same
    //     bit rstCalcSkillPowerUpCore reads/writes. This also confirms the
    //     Presentation Consumer investigation's "source object" (fields
    //     +0x91/+0x92/+0x94/+0x98, i.e. rstData_t.MotionReq/MotionReqMode/
    //     MotionReqBeforeFrame/MotionReqAfterFrame per the same decompiled
    //     list) and "GBWK" as used throughout this file's siblings are the
    //     same object - not two separate objects.
    //   - Flag(+0x7E) is read once at entry (0x18228CBE7,
    //     "movzx eax, byte ptr [source+0x7e]") into a value that is NOT
    //     re-read before either CLEAR site runs, so flagBefore captured in
    //     this class's Prefix reliably distinguishes which site could have
    //     fired: flagBefore==4 -> only Site1 reachable, flagBefore==3 ->
    //     only Site2 reachable (mutually exclusive branches, confirmed by
    //     the "cmp al,3/je Site2; cmp al,4/je Site1" dispatch at
    //     0x18228CBEB/0x18228CBF3). No hardware breakpoint needed for
    //     Site1/Site2 attribution.
    //   - Flag==3 vs Flag==4's own semantic meaning is NOT determined by
    //     this static pass (the value is produced by a separate
    //     rstChkSkillAct-result-driven dispatch earlier in the function) -
    //     left UNRESOLVED per investigation discipline.
    //
    // Never writes State_182e31630+0x1C, SeqInfo, pCurrentStock, or Flag -
    // the State slot is read via Marshal.ReadIntPtr/ReadInt32 only (no
    // Marshal.Write* calls anywhere in this class); every other field is a
    // plain property read through the existing Il2Cpp interop wrapper.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
    internal static class SeqSkillPowerUpBit6ClearDiagnostics
    {
        // moduleBase + (0x182e31630 - 0x180000000) == the same
        // State_182e31630 static slot documented in 01_CURRENT_STATE.md.
        private const long StateSlotVa = 0x182e31630L;

        private static bool _captured;
        private static int _unit;
        private static int _seqBefore;
        private static sbyte _flagBefore;
        private static bool _bit6Before;
        private static sbyte _pUpResultBefore;
        private static sbyte _pUpIndexBefore;
        private static int? _state1CBefore;

        // Shared with RstCalcState1CDiagnostics - both need the same
        // read-only slot chase, and duplicating a 6-line pointer walk
        // twice was judged worse than one shared internal helper.
        internal static int? TryReadState1C()
        {
            try
            {
                IntPtr moduleBase = NativeChancePatchUtility.ResolveModuleBase();
                IntPtr slotAddr = NativeChancePatchUtility.ResolveVa(moduleBase, StateSlotVa);
                IntPtr slotValue = Marshal.ReadIntPtr(slotAddr);
                if (slotValue == IntPtr.Zero) return null;
                IntPtr statePtr = Marshal.ReadIntPtr(slotValue, 0xb8);
                if (statePtr == IntPtr.Zero) return null;
                return Marshal.ReadInt32(statePtr, 0x1c);
            }
            catch
            {
                return null;
            }
        }

        private static bool ShouldLog(out Il2Cppnewdata_H.datUnitWork_t? stock)
        {
            stock = null;
            var gbwk = rstinit.GBWK;
            if (gbwk == null) return false;
            stock = gbwk.pCurrentStock;
            if (stock == null || stock.Pointer == IntPtr.Zero) return false;
            return PowerUpMutationCfgDiagnostics.IsTargetUnit(stock.id);
        }

        private static void Prefix()
        {
            _captured = false;
            if (!PowerUpMutationCfgDiagnostics.Enabled) return;
            try
            {
                if (!ShouldLog(out var stock) || stock == null) return;
                var gbwk = rstinit.GBWK;

                _unit = stock.id;
                _seqBefore = gbwk.SeqInfo.Current;
                _flagBefore = gbwk.Flag;
                _bit6Before = (stock.flag & 0x40) != 0;
                _pUpResultBefore = gbwk.PUpSkillResult;
                _pUpIndexBefore = gbwk.PUpSkillIndex;
                _state1CBefore = TryReadState1C();
                _captured = true;
            }
            catch (Exception ex)
            {
                _captured = false;
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SeqSkillPowerUpBit6ClearDiagnostics prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!_captured) return;
            _captured = false; // consume once per Prefix/Postfix pair
            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                if (gbwk == null || stock == null || stock.Pointer == IntPtr.Zero) return;

                int frame = UnityEngine.Time.frameCount;
                int seqAfter = gbwk.SeqInfo.Current;
                sbyte flagAfter = gbwk.Flag;
                bool bit6After = (stock.flag & 0x40) != 0;
                sbyte pUpResultAfter = gbwk.PUpSkillResult;
                sbyte pUpIndexAfter = gbwk.PUpSkillIndex;
                int? state1CAfter = TryReadState1C();

                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-SEQ10-BIT6; " +
                    $"unit={_unit} frame={frame} " +
                    $"seqBefore={_seqBefore} seqAfter={seqAfter} " +
                    $"flagBefore={_flagBefore} flagAfter={flagAfter} " +
                    $"bit6Before={_bit6Before} bit6After={bit6After} " +
                    $"pUpResultBefore={_pUpResultBefore} pUpResultAfter={pUpResultAfter} " +
                    $"pUpIndexBefore={_pUpIndexBefore} pUpIndexAfter={pUpIndexAfter} " +
                    $"state1CBefore={(_state1CBefore.HasValue ? _state1CBefore.Value.ToString() : "NULL")} " +
                    $"state1CAfter={(state1CAfter.HasValue ? state1CAfter.Value.ToString() : "NULL")}.");

                if (_bit6Before && !bit6After)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] V3-BIT6-CLEAR; " +
                        $"unit={_unit} frame={frame} seqBefore={_seqBefore} seqAfter={seqAfter} " +
                        $"flagBefore={_flagBefore} flagAfter={flagAfter} " +
                        $"pUpResult={pUpResultAfter} " +
                        $"state1C={(state1CAfter.HasValue ? state1CAfter.Value.ToString() : "NULL")}.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SeqSkillPowerUpBit6ClearDiagnostics postfix failed safely: {ex.Message}");
            }
        }
    }
}
