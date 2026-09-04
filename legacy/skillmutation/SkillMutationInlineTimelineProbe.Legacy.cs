using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Temporary, read-only diagnostic probe (Phase 2B). Purpose: capture a
    // single, readable timeline of SeqInfo.Current transitions and key
    // milestone events, from "mutation confirmed" through "next demon's
    // rstCalcSeqDevilLevelUp prefix / pCurrentStock commit", to empirically
    // answer whether current-demon transaction completion always precedes
    // the next demon's ownership commit.
    //
    // Absolute safety: read-only. Never writes to GBWK/SeqInfo/pCurrentStock/
    // WorkStock/TargetIndex/TargetCnt or any other native state. Never
    // returns false from a Prefix, never overrides a return value. Only
    // performs managed getter reads and logging.
    //
    // Entirely independent of SkillMutationLearnAsNew / SkillMutationTelemetry
    // / SkillMutationGbWkFieldProbe - no shared state. Feature-flagged,
    // default ON only for this investigation phase; fully removable.
    internal static class SkillMutationInlineTimelineProbe
    {
        internal static readonly bool Enabled = true;

        private static int _lastSeqCurrent = int.MinValue;
        private static IntPtr _lastOwnershipCurrentPtr = IntPtr.Zero;
        private static IntPtr _lastOwnershipWorkPtr = IntPtr.Zero;
        private static int _lastDefSkillResultLogged = int.MinValue;
        private static HashSet<int>? _byteCandidates;
        private static HashSet<int>? _int16Candidates;
        private static HashSet<int>? _int32Candidates;
        private static int _defResultObservationCount;

        // Phase 2E Correction: re-map SeqInfo's managed fields against raw
        // native bytes, rather than continuing to assume Current=+0x11 (an
        // assumption that a direct rstUpdateSeqDefaultSkill() call just
        // contradicted - SeqLast changed to 21 but SeqCurrent did not, even
        // though the write instruction's address chain resolves to a
        // confirmed-real GBWK). Uses the same intersection-narrowing
        // discipline as the DefSkillResult probe. Read-only.
        private static readonly string[] SeqFieldNames = { "Current", "Last", "Next", "Change", "Flag", "MesFlag", "Timer" };
        private static Dictionary<string, HashSet<int>>? _seqByteCandidates;
        private static string _lastSeqSignature = string.Empty;
        private static int _seqObservationCount;

        private static void ScanSeqInfoLayout()
        {
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                var seq = gbwk.SeqInfo;
                int[] managedValues = { seq.Current, seq.Last, seq.Next, seq.Change, seq.Flag, seq.MesFlag, seq.Timer };
                string signature = string.Join(",", managedValues);
                if (signature == _lastSeqSignature) return; // only log when any managed field changes
                _lastSeqSignature = signature;
                _seqObservationCount++;

                IntPtr seqInfoPtr;
                try { seqInfoPtr = Marshal.ReadIntPtr(gbwk.Pointer, 0x10); }
                catch { return; }
                if (seqInfoPtr == IntPtr.Zero) return;

                _seqByteCandidates ??= new Dictionary<string, HashSet<int>>();
                var perFieldMatches = new Dictionary<string, HashSet<int>>();

                for (int fi = 0; fi < SeqFieldNames.Length; fi++)
                {
                    var matches = new HashSet<int>();
                    int managedValue = managedValues[fi];
                    for (int off = 0x00; off <= 0x1f; off++)
                    {
                        try
                        {
                            byte b = Marshal.ReadByte(seqInfoPtr, off);
                            if (b == unchecked((byte)managedValue)) matches.Add(off);
                        }
                        catch { }
                    }
                    perFieldMatches[SeqFieldNames[fi]] = matches;

                    if (!_seqByteCandidates.TryGetValue(SeqFieldNames[fi], out var existing))
                        _seqByteCandidates[SeqFieldNames[fi]] = matches;
                    else
                        _seqByteCandidates[SeqFieldNames[fi]] = Intersect(existing, matches);
                }

                var sb = new System.Text.StringBuilder();
                foreach (var name in SeqFieldNames)
                {
                    sb.Append(name).Append('=').Append(managedValues[Array.IndexOf(SeqFieldNames, name)]);
                    sb.Append("(candidates=[").Append(FormatOffsets(_seqByteCandidates[name])).Append(']').Append(") ");
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] SEQINFO-LAYOUT-PROBE; " +
                    $"frame={UnityEngine.Time.frameCount} observation={_seqObservationCount} " +
                    $"seqInfoPtr=0x{seqInfoPtr.ToInt64():X} {sb}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] SEQINFO-LAYOUT-PROBE failed safely: {ex.Message}");
            }
        }

        // Read-only, wide-range offset-confirmation scan for DefSkillResult.
        // Per investigation discipline: managed field declaration order does
        // NOT reliably predict native byte-offset order (already confirmed
        // wrong once: pTargetList=+0x58, PUpSkillIndex=+0x4c, PUpSkillID=
        // +0x4e - field 17 sits at a lower offset than fields 22/23). So
        // this scans the whole known-safe range (+0x10..+0x7f) at three
        // widths (byte/int16/int32) and intersects the set of "currently
        // matches managed DefSkillResult" offsets across every observation,
        // automatically eliminating coincidental one-off matches (a field
        // that happened to be 0, a pointer's low byte, etc.) without
        // guessing. Never writes anything.
        private static void ScanDefSkillResultOffset()
        {
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                int managedValue = gbwk.DefSkillResult;
                if (managedValue == _lastDefSkillResultLogged) return; // only log on managed-value change
                _lastDefSkillResultLogged = managedValue;
                _defResultObservationCount++;

                var byteMatches = new HashSet<int>();
                var int16Matches = new HashSet<int>();
                var int32Matches = new HashSet<int>();

                for (int off = 0x10; off <= 0x7f; off++)
                {
                    try
                    {
                        byte b = Marshal.ReadByte(gbwk.Pointer, off);
                        if (b == managedValue) byteMatches.Add(off);
                    }
                    catch { }
                }
                for (int off = 0x10; off <= 0x7e; off++)
                {
                    try
                    {
                        short s = Marshal.ReadInt16(gbwk.Pointer, off);
                        if (s == managedValue) int16Matches.Add(off);
                    }
                    catch { }
                }
                for (int off = 0x10; off <= 0x7c; off++)
                {
                    try
                    {
                        int i = Marshal.ReadInt32(gbwk.Pointer, off);
                        if (i == managedValue) int32Matches.Add(off);
                    }
                    catch { }
                }

                // Intersect with all prior observations - only offsets that
                // have matched EVERY time (across both managed=0 and
                // managed=2, etc.) survive as real candidates.
                _byteCandidates = _byteCandidates == null ? byteMatches : Intersect(_byteCandidates, byteMatches);
                _int16Candidates = _int16Candidates == null ? int16Matches : Intersect(_int16Candidates, int16Matches);
                _int32Candidates = _int32Candidates == null ? int32Matches : Intersect(_int32Candidates, int32Matches);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] DEFRESULT-WIDE-PROBE; " +
                    $"frame={UnityEngine.Time.frameCount} observation={_defResultObservationCount} " +
                    $"managed={managedValue} " +
                    $"byteCandidates=[{FormatOffsets(_byteCandidates)}] " +
                    $"int16Candidates=[{FormatOffsets(_int16Candidates)}] " +
                    $"int32Candidates=[{FormatOffsets(_int32Candidates)}].");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] DEFRESULT-WIDE-PROBE failed safely: {ex.Message}");
            }
        }

        private static HashSet<int> Intersect(HashSet<int> a, HashSet<int> b)
        {
            var result = new HashSet<int>();
            foreach (int x in a) if (b.Contains(x)) result.Add(x);
            return result;
        }

        private static string FormatOffsets(HashSet<int>? set)
        {
            if (set == null || set.Count == 0) return "";
            var list = new List<int>(set);
            list.Sort();
            var sb = new System.Text.StringBuilder();
            foreach (int o in list)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append("0x").Append(o.ToString("X"));
            }
            return sb.ToString();
        }
        private static bool _capturing;

        private static string Snapshot()
        {
            var gbwk = rstinit.GBWK;
            if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return "gbwk=unavailable";
            var seq = gbwk.SeqInfo;
            var currentStock = gbwk.pCurrentStock;
            var workStock = gbwk.WorkStock;
            int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero ? -1 : currentStock.id;
            int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero ? -1 : workStock.id;
            IntPtr currentPtr = currentStock?.Pointer ?? IntPtr.Zero;
            IntPtr workPtr = workStock?.Pointer ?? IntPtr.Zero;

            return
                $"seqCurrent={seq.Current} seqLast={seq.Last} seqNext={seq.Next} seqChange={seq.Change} " +
                $"currentStockPtr=0x{currentPtr.ToInt64():X}(unit={currentUnit}) " +
                $"workStockPtr=0x{workPtr.ToInt64():X}(unit={workUnit}) " +
                $"targetIndex={gbwk.TargetIndex} targetCnt={gbwk.TargetCnt} " +
                $"PUpSkillID={gbwk.PUpSkillID} PUpSkillIndex={gbwk.PUpSkillIndex} " +
                $"SelectSkillID={gbwk.SelectSkillID} DefSkillResult={gbwk.DefSkillResult} " +
                $"active=[{SkillMutationLearnAsNew.ActiveSummary}] " +
                $"inlineAwaiting=[{SkillMutationLearnAsNew.InlineAwaitingSummary}]";
        }

        // Called every frame from rstUpdate's Postfix (existing hook type,
        // dedicated class). Logs only when SeqInfo.Current actually changes,
        // and only once capture has started (see StartCaptureIfNeeded).
        internal static void ObserveSeqChange()
        {
            if (!Enabled || !_capturing) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                // Phase 2E: DefSkillResult offset confirmation, reusing this
                // existing per-frame hook rather than adding a new one.
                ScanDefSkillResultOffset();

                // Phase 2E Correction: SeqInfo raw-layout remapping.
                ScanSeqInfoLayout();

                // Phase 2C: log immediately on any ownership pointer change,
                // independent of SeqInfo.Current change detection - this is
                // the exact signal the A/B test needs to see whether
                // pCurrentStock ever moves off the transaction's own unit
                // while ExperimentalDisableQueueOwnershipBinding is true.
                var currentStock = gbwk.pCurrentStock;
                var workStock = gbwk.WorkStock;
                IntPtr currentPtr = currentStock?.Pointer ?? IntPtr.Zero;
                IntPtr workPtr = workStock?.Pointer ?? IntPtr.Zero;
                if (currentPtr != _lastOwnershipCurrentPtr || workPtr != _lastOwnershipWorkPtr)
                {
                    MelonLogger.Msg(
                        $"[NocturneModernGameplay] INLINE-TIMELINE ownership-change; frame={UnityEngine.Time.frameCount} " +
                        $"{Snapshot()}.");
                    _lastOwnershipCurrentPtr = currentPtr;
                    _lastOwnershipWorkPtr = workPtr;
                }

                int seqCurrent = gbwk.SeqInfo.Current;
                if (seqCurrent == _lastSeqCurrent) return;

                MelonLogger.Msg(
                    $"[NocturneModernGameplay] INLINE-TIMELINE seq-change; frame={UnityEngine.Time.frameCount} " +
                    $"seq={_lastSeqCurrent}->{seqCurrent} {Snapshot()}.");
                _lastSeqCurrent = seqCurrent;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] INLINE-TIMELINE seq-change failed safely: {ex.Message}");
            }
        }

        // Milestone log. Also arms capture on the first milestone call, so
        // the very first mutation-confirmation event in a session starts
        // the timeline without needing a separate "start" signal.
        internal static void Milestone(string tag)
        {
            if (!Enabled) return;
            try
            {
                if (!_capturing)
                {
                    _capturing = true;
                    var gbwk0 = rstinit.GBWK;
                    _lastSeqCurrent = gbwk0 != null && gbwk0.Pointer != IntPtr.Zero ? gbwk0.SeqInfo.Current : int.MinValue;
                }
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] INLINE-TIMELINE milestone={tag}; frame={UnityEngine.Time.frameCount} {Snapshot()}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] INLINE-TIMELINE milestone failed safely: {ex.Message}");
            }
        }
    }

    // Per-frame seq-change sampling. Reuses the existing rstUpdate Postfix
    // hook TYPE (a new, dedicated, minimal patch class - does not touch or
    // duplicate the existing ResultSequenceStatePatch's own logic).
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class InlineTimelineUpdatePatch
    {
        private static void Postfix()
        {
            SkillMutationInlineTimelineProbe.ObserveSeqChange();
            // Phase 2E: process any pending Inline Progression Arbitration
            // once per rstUpdate, matching native's own observed call
            // cadence for rstUpdateSeqDefaultSkill rather than a fixed timer.
            SkillMutationLearnAsNew.ProcessInlineAwaitingTransition();
        }
    }

    // Milestone taps. All Postfix-only except where a Prefix is needed to
    // capture "entry" state before the native body runs; none return false,
    // none alter arguments/results.
    [HarmonyPatch(typeof(rstCalcCore), nameof(rstCalcCore.cmbGetMutationSkill))]
    internal static class InlineTimelineMutationConfirmedPatch
    {
        private static void Postfix(ushort __result)
        {
            if (__result != 0) SkillMutationInlineTimelineProbe.Milestone("mutation-confirmed");
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstOverWriteSkill))]
    internal static class InlineTimelineOverwritePatch
    {
        private static void Postfix() => SkillMutationInlineTimelineProbe.Milestone("overwrite-observed");
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstChkAddSkill))]
    internal static class InlineTimelineCapacityCheckPatch
    {
        private static void Prefix() => SkillMutationInlineTimelineProbe.Milestone("capacity-check");
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDestroySkill))]
    internal static class InlineTimelineForgetEntryPatch
    {
        private static void Prefix() => SkillMutationInlineTimelineProbe.Milestone("forget-flow-entry");
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDestroyConfirm))]
    internal static class InlineTimelineForgetConfirmPatch
    {
        private static void Prefix() => SkillMutationInlineTimelineProbe.Milestone("forget-confirm-entry");
        private static void Postfix() => SkillMutationInlineTimelineProbe.Milestone("forget-confirm-exit");
    }

    [HarmonyPatch(typeof(fclCombineCalcCore), nameof(fclCombineCalcCore.cmbAddSkill))]
    internal static class InlineTimelineFinalCommitPatch
    {
        private static void Postfix() => SkillMutationInlineTimelineProbe.Milestone("final-skill-commit");
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSeqDevilLevelUp))]
    internal static class InlineTimelineDevilLevelUpPatch
    {
        private static void Prefix() => SkillMutationInlineTimelineProbe.Milestone("seq6-devilLevelUp-prefix");
        private static void Postfix() => SkillMutationInlineTimelineProbe.Milestone("seq6-devilLevelUp-postfix");
    }

    // Phase 2E Correction: bracket rstUpdateSeqDefaultSkill's own execution
    // (both when native calls it via the seq=8 dispatch table, and when the
    // Inline PoC calls it directly) with full SeqInfo/DefSkillResult
    // snapshots, to indirectly confirm whether the confirmed DefSkillResult==2
    // branch (and its +0x11 write) is actually reached in either context.
    // Read-only.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class InlineTimelineDefaultSkillBracketPatch
    {
        private static void Prefix() => SkillMutationInlineTimelineProbe.Milestone("defaultskill-entry");
        private static void Postfix() => SkillMutationInlineTimelineProbe.Milestone("defaultskill-exit");
    }

    // Phase 2E Inline Progression Arbitration - the gate itself. Skips
    // rstCalc for the current frame ONLY while SkillMutationLearnAsNew
    // confirms an Inline transition is genuinely pending at seq=10 on the
    // exact same unit; every other frame, and the instant that condition no
    // longer holds, rstCalc runs completely normally. Never writes any
    // native state itself - purely a Prefix-return-false skip, released
    // automatically by SkillMutationLearnAsNew's own guard re-checks.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class InlineProgressionGatePatch
    {
        private static bool Prefix() => SkillMutationLearnAsNew.ShouldAllowRstCalc();
    }
}
