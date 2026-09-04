using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Read-only investigation telemetry: mirrors (does not hook or alter)
    // the native pre-Core gate chain inside rstcalc.rstCalc, resolved this
    // session via offline disassembly (.analysis/disasm_precore_gate.py) of
    // the region around both rstCalcSkillPowerUpCore call sites
    // (0x18227E9C9 / 0x18227F0A1). Independently re-reads the same live
    // memory native is about to evaluate; never writes to it, never hooks
    // any instruction inside rstCalc itself (only a Prefix on the managed
    // rstCalc method boundary, the same pattern already used by
    // PowerUpMutationCfgDiagnostics.cs).
    //
    // GATE1 (static disassembly, this session):
    //   TARGET(VA 0x182e4ed30) -> +0xb8 -> [0] -> +0x58 -> array
    //   -> count(+0x18) -> array[0](+0x20) -> +0x24 (int16) = value24
    //   value24 < 7  -> native forces PUpSkillResult=0 without calling Core
    //   value24 >= 7 -> proceeds to GATE2B
    //
    // GATE2B (NEW this session, static disassembly at VA ~0x18227EF93-EFAC):
    //   source object (GBWK.Pointer -> +0xb8 -> [0], the same "source
    //   object" already tracked for +0x91/+0x92/+0x94/+0x98) -> +0x60
    //   (CONFIRMED qword pointer field, see PRESENTATION_CONSUMER/PLAN.md)
    //   -> +0x14 (int16) = value.
    //   value == 0 -> native calls the roll (VA 0x1821690d0) but IGNORES
    //                 its result and unconditionally proceeds toward Core.
    //   value != 0 -> native gates Core on the roll's result via
    //                 "test al,3" (Patch A's own site, VA 0x18227EFD0).
    //
    // The roll itself (VA 0x1821690d0) is a raw native instruction sequence,
    // not a managed method Harmony can Prefix/Postfix - reading its result
    // safely without an inline/raw hook (explicitly disallowed for this
    // investigation) is not possible. When GATE1 passes and GATE2B is
    // non-zero, this class does not read the roll outcome directly; the
    // roll outcome must be inferred from whether Core was subsequently
    // reached (see V3-CFG-CORE / V3-CFG-RNDPOWERUP in
    // PowerUpMutationCfgDiagnostics.cs).
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class PreCoreGateDiagnostics
    {
        private const long TargetRva = 0x182e4ed30 - 0x180000000;

        private static void Prefix()
        {
            if (!PowerUpMutationCfgDiagnostics.Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                int unit = stock.id;
                if (!PowerUpMutationCfgDiagnostics.IsTargetUnit(unit)) return;

                int frame = UnityEngine.Time.frameCount;
                int seq = gbwk.SeqInfo.Current;
                uint flagRaw = stock.flag;
                bool bit6 = (flagRaw & 0x40) != 0;

                string gate1Value24 = "UNRESOLVED";
                string gate1Pass = "UNRESOLVED";
                TryReadGate1(out int? value24);
                if (value24.HasValue)
                {
                    gate1Value24 = value24.Value.ToString();
                    gate1Pass = (value24.Value >= 7).ToString();
                }

                string gate2bValue = "UNRESOLVED";
                string gate2bZero = "UNRESOLVED";
                TryReadGate2B(gbwk.Pointer, out short? gate2b);
                if (gate2b.HasValue)
                {
                    gate2bValue = gate2b.Value.ToString();
                    gate2bZero = (gate2b.Value == 0).ToString();
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] V3-PRECORE-GATE; " +
                    $"frame={frame} unit={unit} seq={seq} " +
                    $"gate1Value24={gate1Value24} gate1Pass={gate1Pass} " +
                    $"gate2bValue={gate2bValue} gate2bZero={gate2bZero} " +
                    $"stockFlag=0x{flagRaw:X} bit6={bit6}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] V3-PRECORE-GATE failed safely: {ex.Message}");
            }
        }

        // Read-only. Never throws past this point; returns null on any
        // unresolved intermediate pointer rather than guessing.
        private static void TryReadGate1(out int? value24)
        {
            value24 = null;
            try
            {
                IntPtr moduleBase = NativeChancePatchUtility.ResolveModuleBase();
                IntPtr slotAddr = NativeChancePatchUtility.ResolveVa(moduleBase, 0x182e4ed30);
                IntPtr slotValue = Marshal.ReadIntPtr(slotAddr);
                if (slotValue == IntPtr.Zero) return;

                IntPtr intermediate = Marshal.ReadIntPtr(slotValue, 0xb8);
                if (intermediate == IntPtr.Zero) return;

                IntPtr targetSourcePtr = Marshal.ReadIntPtr(intermediate, 0);
                if (targetSourcePtr == IntPtr.Zero) return;

                IntPtr arrayPtr = Marshal.ReadIntPtr(targetSourcePtr, 0x58);
                if (arrayPtr == IntPtr.Zero) return;

                int count = Marshal.ReadInt32(arrayPtr, 0x18);
                if (count <= 0) return;

                IntPtr entry0Ptr = Marshal.ReadIntPtr(arrayPtr, 0x20);
                if (entry0Ptr == IntPtr.Zero) return;

                value24 = Marshal.ReadInt16(entry0Ptr, 0x24);
            }
            catch
            {
                value24 = null;
            }
        }

        // Read-only. Reuses the already-CONFIRMED "source object" chain
        // (GBWK.Pointer -> +0xb8 -> [0]) documented in
        // PRESENTATION_CONSUMER/PLAN.md, then reads +0x60 (CONFIRMED qword
        // pointer field) -> +0x14 (int16, GATE2B this session's finding).
        private static void TryReadGate2B(IntPtr gbwkPointer, out short? gate2bValue)
        {
            gate2bValue = null;
            try
            {
                IntPtr intermediate = Marshal.ReadIntPtr(gbwkPointer, 0xb8);
                if (intermediate == IntPtr.Zero) return;

                IntPtr sourcePtr = Marshal.ReadIntPtr(intermediate, 0);
                if (sourcePtr == IntPtr.Zero) return;

                IntPtr src60Ptr = Marshal.ReadIntPtr(sourcePtr, 0x60);
                if (src60Ptr == IntPtr.Zero) return;

                gate2bValue = Marshal.ReadInt16(src60Ptr, 0x14);
            }
            catch
            {
                gate2bValue = null;
            }
        }
    }
}
