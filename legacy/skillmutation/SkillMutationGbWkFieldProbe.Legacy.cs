using System;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Temporary, read-only diagnostic probe (Phase 1I). Purpose: empirically
    // determine the exact native offset of GBWK.pCurrentStock (and, once
    // that is known, TargetIndex/TargetCnt) by scanning pointer-aligned
    // candidate offsets on the real-GBWK object and comparing each against
    // the already-known managed property values every time something
    // relevant changes.
    //
    // Absolute safety guarantees, per the Phase 1I directive:
    //   - Never calls Marshal.Write* of any kind.
    //   - Never mutates GBWK, WorkStock, pCurrentStock, SeqInfo, or any
    //     other native state.
    //   - Never returns false from a Prefix, never overrides a return value,
    //     never skips native execution.
    //   - Only performs: managed getter reads, Marshal.ReadIntPtr /
    //     Marshal.ReadByte (read-only), pointer-equality comparisons, and
    //     logging.
    //
    // This is entirely independent of SkillMutationLearnAsNew /
    // SkillMutationTelemetry - no shared state, no interaction with
    // HandledSlots/Candidates/Pending/DrainClosing/etc. It exists solely to
    // answer one empirical question and is designed to be fully deleted
    // once that question is answered.
    //
    // Toggle with Enabled to fully disable (default ON only for the
    // duration of this investigation phase; set to false or delete this
    // file entirely once pCurrentStock's offset is confirmed).
    internal static class SkillMutationGbWkFieldProbe
    {
        internal static readonly bool Enabled = true;

        // Scan range and stride, per directive: pointer-aligned offsets only,
        // GBWK+0x00 through GBWK+0x100.
        private const int ScanStart = 0x00;
        private const int ScanEndExclusive = 0x100;
        private const int PointerStride = 8;

        // Change-detection state, to keep log volume minimal (only log when
        // something the directive cares about actually changes).
        private static IntPtr _lastCurrentStockPtr = IntPtr.Zero;
        private static IntPtr _lastWorkStockPtr = IntPtr.Zero;
        private static int _lastSeqCurrent = int.MinValue;
        private static string _lastCurrentMatchSet = string.Empty;
        private static string _lastWorkMatchSet = string.Empty;

        internal static void Observe(string checkpoint)
        {
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                var currentStock = gbwk.pCurrentStock;
                var workStock = gbwk.WorkStock;
                IntPtr currentPtr = currentStock?.Pointer ?? IntPtr.Zero;
                IntPtr workPtr = workStock?.Pointer ?? IntPtr.Zero;
                var seq = gbwk.SeqInfo;
                int seqCurrent = seq.Current;

                bool coreStateChanged =
                    currentPtr != _lastCurrentStockPtr ||
                    workPtr != _lastWorkStockPtr ||
                    seqCurrent != _lastSeqCurrent;

                // Read-only pointer-aligned scan of GBWK+0x00..GBWK+0x100,
                // comparing each candidate against both known managed
                // pointer values. Every read is Marshal.ReadIntPtr - no
                // writes of any kind occur here.
                var currentMatches = new StringBuilder();
                var workMatches = new StringBuilder();
                for (int off = ScanStart; off < ScanEndExclusive; off += PointerStride)
                {
                    IntPtr candidate;
                    try
                    {
                        candidate = Marshal.ReadIntPtr(gbwk.Pointer, off);
                    }
                    catch (Exception)
                    {
                        continue; // unreadable region, skip - never treat as a match
                    }
                    if (candidate == IntPtr.Zero) continue; // exclude null, per directive's filtering rule

                    if (currentPtr != IntPtr.Zero && candidate == currentPtr)
                    {
                        if (currentMatches.Length > 0) currentMatches.Append(',');
                        currentMatches.Append("0x").Append(off.ToString("X"));
                    }
                    if (workPtr != IntPtr.Zero && candidate == workPtr)
                    {
                        if (workMatches.Length > 0) workMatches.Append(',');
                        workMatches.Append("0x").Append(off.ToString("X"));
                    }
                }
                string currentMatchSet = currentMatches.ToString();
                string workMatchSet = workMatches.ToString();

                bool matchSetChanged =
                    currentMatchSet != _lastCurrentMatchSet ||
                    workMatchSet != _lastWorkMatchSet;

                if (!coreStateChanged && !matchSetChanged) return; // avoid log spam

                _lastCurrentStockPtr = currentPtr;
                _lastWorkStockPtr = workPtr;
                _lastSeqCurrent = seqCurrent;
                _lastCurrentMatchSet = currentMatchSet;
                _lastWorkMatchSet = workMatchSet;

                int currentUnit = currentStock == null || currentPtr == IntPtr.Zero ? -1 : currentStock.id;
                int workUnit = workStock == null || workPtr == IntPtr.Zero ? -1 : workStock.id;

                // Flag the specific window the directive calls out as most
                // important for false-positive rejection: pCurrentStock and
                // WorkStock pointing at different units at the same instant.
                bool divergentOwnership = currentPtr != IntPtr.Zero && workPtr != IntPtr.Zero && currentPtr != workPtr;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] GBWK-FIELD-PROBE " + checkpoint + "; " +
                    $"frame={UnityEngine.Time.frameCount} " +
                    $"gbwk=0x{gbwk.Pointer.ToInt64():X} " +
                    $"managedCurrent=0x{currentPtr.ToInt64():X}(unit={currentUnit}) " +
                    $"managedWork=0x{workPtr.ToInt64():X}(unit={workUnit}) " +
                    $"seqCurrent={seqCurrent} " +
                    $"divergentOwnership={divergentOwnership} " +
                    $"matchCurrentOffsets=[{currentMatchSet}] " +
                    $"matchWorkOffsets=[{workMatchSet}] " +
                    $"knownWorkOffset=0x60.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] GBWK-FIELD-PROBE failed safely: {ex.Message}");
            }
        }
    }

    // Dedicated, minimal Harmony hooks for this probe only - entirely
    // separate from the existing SkillMutationLearnAsNew/SkillMutationTelemetry
    // hook set. Prefix/Postfix around rstCalcSeqDevilLevelUp brackets the
    // one confirmed WorkStock=candidate assignment point (0x18227d409);
    // rstUpdate's Postfix additionally samples every frame's forget-UI /
    // multi-demon window, where pCurrentStock != WorkStock is already known
    // to legitimately occur (the case the directive flags as most important
    // for rejecting false-positive offset candidates).
    //
    // None of these Prefixes return false, none override __result, none
    // write anything - Postfix-only observation plus one no-op Prefix call
    // used purely to capture a "before" sample.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSeqDevilLevelUp))]
    internal static class GbWkFieldProbeDevilLevelUpPatch
    {
        private static void Prefix() => SkillMutationGbWkFieldProbe.Observe("rstCalcSeqDevilLevelUp-prefix");
        private static void Postfix() => SkillMutationGbWkFieldProbe.Observe("rstCalcSeqDevilLevelUp-postfix");
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class GbWkFieldProbeUpdatePatch
    {
        private static void Postfix() => SkillMutationGbWkFieldProbe.Observe("rstUpdate-postfix");
    }
}
