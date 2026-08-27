using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace NocturneModernGameplay
{
    internal static class SkillMutationTelemetry
    {
        private sealed class ExperimentalCandidateInfo
        {
            internal Il2Cppnewdata_H.datUnitWork_t Stock = null!;
            internal int Index;
            internal ushort OriginalSkill;
            internal ushort MutatedSkill;
            internal string LastSkills = string.Empty;
            internal bool AwaitingResult = true;
            internal bool Completed;
        }

        private static readonly Dictionary<(IntPtr Stock, int Index), ExperimentalCandidateInfo>
            ExperimentalCandidates = new();
        private static Il2Cppnewdata_H.datUnitWork_t? _candidate;
        private static int _candidateIndex = -1;
        private static ushort _originalSkill;
        private static ushort _mutatedSkill;
        private static string _lastObservedSkills = string.Empty;
        private static string _lastResultState = string.Empty;

        // Read-only summary of the current single-candidate global state, for
        // telemetry only. Does not affect any candidate-handling behavior.
        internal static string CandidateSummary
        {
            get
            {
                if (!ExperimentalInlineRepro.Enabled)
                    return _candidate == null || _candidate.Pointer == IntPtr.Zero
                        ? "none"
                        : $"unit={_candidate.id} index={_candidateIndex} " +
                          $"original={_originalSkill} mutated={_mutatedSkill}";
                if (ExperimentalCandidates.Count == 0) return "none";
                var text = new StringBuilder();
                foreach (var pair in ExperimentalCandidates)
                {
                    if (text.Length > 0) text.Append(" | ");
                    ExperimentalCandidateInfo item = pair.Value;
                    text.Append($"key=0x{pair.Key.Stock.ToInt64():X}:{pair.Key.Index} ")
                        .Append($"unit={item.Stock.id} original={item.OriginalSkill} ")
                        .Append($"mutated={item.MutatedSkill} awaiting={item.AwaitingResult} ")
                        .Append($"completed={item.Completed}");
                }
                return text.ToString();
            }
        }
        internal static int CandidateCount => ExperimentalInlineRepro.Enabled
            ? ExperimentalCandidates.Count
            : ((_candidate != null && _candidate.Pointer != IntPtr.Zero) ? 1 : 0);

        internal static void ObserveResultState(string source)
        {
            try
            {
                var work = rstinit.GBWK;
                var seq = work.SeqInfo;
                int currentId = work.pCurrentStock == null || work.pCurrentStock.Pointer == IntPtr.Zero
                    ? -1 : work.pCurrentStock.id;
                int workId = work.WorkStock == null || work.WorkStock.Pointer == IntPtr.Zero
                    ? -1 : work.WorkStock.id;
                string state = $"seq={seq.Current}/{seq.Next}/{seq.Last}/{seq.Change} " +
                    $"process={rstinit.gProcessStat} currentUnit={currentId} workUnit={workId} " +
                    $"selectSkill={work.SelectSkillID} powerSkill={work.PUpSkillID} " +
                    $"powerIndex={work.PUpSkillIndex} powerResult={work.PUpSkillResult} " +
                    $"defaultResult={work.DefSkillResult} target={work.TargetPos}/{work.TargetIndex}/{work.TargetCnt}";
                if (string.Equals(state, _lastResultState, StringComparison.Ordinal)) return;
                _lastResultState = state;
                MelonLogger.Msg($"[NocturneModernGameplay] RESULT-STATE {source}; {state}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] RESULT-STATE unavailable: {ex.Message}");
            }
        }

        internal static void RecordCandidate(
            Il2Cppnewdata_H.datUnitWork_t stock,
            int index,
            ushort selectionResult)
        {
            SkillMutationLearnAsNew.BeginMutationCandidate();
            if (ExperimentalInlineRepro.Enabled)
            {
                var key = (stock.Pointer, index);
                ushort original = index >= 0 && index < stock.skill.Length
                    ? unchecked((ushort)stock.skill[index])
                    : (ushort)0;
                ExperimentalCandidates[key] = new ExperimentalCandidateInfo
                {
                    Stock = stock,
                    Index = index,
                    OriginalSkill = original,
                    LastSkills = DescribeSkills(stock),
                    AwaitingResult = true
                };
                MelonLogger.Msg(
                    "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO candidate-recorded; " +
                    $"frame={UnityEngine.Time.frameCount} unit={stock.id} " +
                    $"candidateKey=0x{stock.Pointer.ToInt64():X}:{index} " +
                    $"selectionResult={selectionResult} original={original} " +
                    $"handled={SkillMutationLearnAsNew.IsSlotHandled(stock.Pointer, index)}.");
            }
            _candidate = stock;
            _candidateIndex = index;
            _originalSkill = index >= 0 && index < stock.skill.Length
                ? unchecked((ushort)stock.skill[index])
                : (ushort)0;
            _lastObservedSkills = DescribeSkills(stock);
            MelonLogger.Msg(
                "[NocturneModernGameplay] MUTATION-PROBE candidate; " +
                $"unit={stock.id} index={index} selectionResult={selectionResult} " +
                $"original={_originalSkill} " +
                $"skills=[{DescribeSkills(stock)}].");
        }

        internal static void RecordWriteStage(string method, string phase, ushort skill = 0)
        {
            SkillMutationLearnAsNew.NotifyResultActivity();
            string skillText = skill == 0 ? string.Empty : $" argumentSkill={skill}";
            string stockText = _candidate == null || _candidate.Pointer == IntPtr.Zero
                ? "stock=unavailable"
                : $"unit={_candidate.id} skills=[{DescribeSkills(_candidate)}]";
            MelonLogger.Msg(
                $"[NocturneModernGameplay] MUTATION-PROBE write-{method}-{phase}; " +
                $"index={_candidateIndex} original={_originalSkill} " +
                $"mutated={_mutatedSkill}{skillText} {stockText}.");
        }

        internal static void ObserveMutationSequence(string method)
        {
            if (ExperimentalInlineRepro.Enabled)
            {
                ObserveExperimentalMutationSequences(method);
                return;
            }
            if (_mutatedSkill == 0 || _candidate == null || _candidate.Pointer == IntPtr.Zero)
            {
                return;
            }

            string current = DescribeSkills(_candidate);
            if (string.Equals(current, _lastObservedSkills, StringComparison.Ordinal))
            {
                return;
            }

            MelonLogger.Msg(
                $"[NocturneModernGameplay] MUTATION-PROBE sequence-change; " +
                $"method={method} unit={_candidate.id} index={_candidateIndex} " +
                $"original={_originalSkill} mutated={_mutatedSkill} " +
                $"before=[{_lastObservedSkills}] after=[{current}].");
            _lastObservedSkills = current;

            SkillMutationLearnAsNew.TryConvertReplacementToAddition(
                _candidate,
                _candidateIndex,
                _originalSkill,
                _mutatedSkill);
            _lastObservedSkills = DescribeSkills(_candidate);
        }

        internal static void RecordMutationResult(
            Il2Cppnewdata_H.datUnitWork_t stock,
            ushort originalSkill,
            ushort mutatedSkill)
        {
            if (ExperimentalInlineRepro.Enabled)
            {
                ExperimentalCandidateInfo? match = null;
                int count = 0;
                foreach (ExperimentalCandidateInfo item in ExperimentalCandidates.Values)
                {
                    if (item.Stock.Pointer != stock.Pointer || !item.AwaitingResult) continue;
                    match = item;
                    count++;
                }
                if (count != 1 || match == null)
                {
                    MelonLogger.Warning(
                        "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO mutation-result-unmatched; " +
                        $"frame={UnityEngine.Time.frameCount} unit={stock.id} " +
                        $"stockPtr=0x{stock.Pointer.ToInt64():X} original={originalSkill} " +
                        $"mutated={mutatedSkill} awaitingMatches={count}; result not associated.");
                    return;
                }

                match.OriginalSkill = originalSkill;
                match.MutatedSkill = mutatedSkill;
                match.AwaitingResult = false;
                _candidate = match.Stock;
                _candidateIndex = match.Index;
                _originalSkill = originalSkill;
                _mutatedSkill = mutatedSkill;
                _lastObservedSkills = match.LastSkills;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO mutation-result-associated; " +
                    $"frame={UnityEngine.Time.frameCount} unit={stock.id} " +
                    $"candidateKey=0x{stock.Pointer.ToInt64():X}:{match.Index} " +
                    $"original={originalSkill} mutated={mutatedSkill}.");
                return;
            }
            _candidate = stock;
            _originalSkill = originalSkill;
            _mutatedSkill = mutatedSkill;
            MelonLogger.Msg(
                "[NocturneModernGameplay] MUTATION-PROBE mapping; " +
                $"unit={stock.id} original={originalSkill} mutated={mutatedSkill}.");
        }

        private static void ObserveExperimentalMutationSequences(string method)
        {
            foreach (var pair in ExperimentalCandidates)
            {
                ExperimentalCandidateInfo item = pair.Value;
                if (item.Completed || item.AwaitingResult || item.MutatedSkill == 0 ||
                    item.Stock == null || item.Stock.Pointer == IntPtr.Zero) continue;

                string current = DescribeSkills(item.Stock);
                if (string.Equals(current, item.LastSkills, StringComparison.Ordinal)) continue;

                var work = rstinit.GBWK;
                IntPtr currentStockPtr = work.pCurrentStock?.Pointer ?? IntPtr.Zero;
                IntPtr workStockPtr = work.WorkStock?.Pointer ?? IntPtr.Zero;
                int currentUnit = work.pCurrentStock == null || currentStockPtr == IntPtr.Zero
                    ? -1 : work.pCurrentStock.id;
                int workUnit = work.WorkStock == null || workStockPtr == IntPtr.Zero
                    ? -1 : work.WorkStock.id;
                MelonLogger.Warning(
                    "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO sequence-change; " +
                    $"frame={UnityEngine.Time.frameCount} method={method} " +
                    $"candidateKey=0x{pair.Key.Stock.ToInt64():X}:{pair.Key.Index} " +
                    $"unit={item.Stock.id} original={item.OriginalSkill} mutated={item.MutatedSkill} " +
                    $"currentUnit={currentUnit} workUnit={workUnit} " +
                    $"currentStockPtr=0x{currentStockPtr.ToInt64():X} " +
                    $"workStockPtr=0x{workStockPtr.ToInt64():X} " +
                    $"PUpSkillIndex={work.PUpSkillIndex} PUpSkillID={work.PUpSkillID} " +
                    $"SelectSkillID={work.SelectSkillID} " +
                    $"handled={SkillMutationLearnAsNew.IsSlotHandled(pair.Key.Stock, pair.Key.Index)} " +
                    $"before=[{item.LastSkills}] after=[{current}].");

                item.LastSkills = current;
                SkillMutationLearnAsNew.TryConvertReplacementToAddition(
                    item.Stock, item.Index, item.OriginalSkill, item.MutatedSkill);
                item.LastSkills = DescribeSkills(item.Stock);
                item.Completed = SkillMutationLearnAsNew.IsSlotHandled(
                    pair.Key.Stock, pair.Key.Index);
            }
        }

        internal static void ClearExperimentalCandidatesAtLifecycleStart()
        {
            if (!ExperimentalInlineRepro.Enabled) return;
            int previous = ExperimentalCandidates.Count;
            ExperimentalCandidates.Clear();
            _candidate = null;
            _candidateIndex = -1;
            _originalSkill = 0;
            _mutatedSkill = 0;
            _lastObservedSkills = string.Empty;
            MelonLogger.Msg(
                "[NocturneModernGameplay] EXPERIMENTAL_INLINE_REPRO candidates-cleared; " +
                $"frame={UnityEngine.Time.frameCount} previousCount={previous}.");
        }

        internal static void RecordCore(string phase, int result = int.MinValue)
        {
            string resultText = result == int.MinValue ? string.Empty : $" result={result}";
            string stockText = _candidate == null || _candidate.Pointer == IntPtr.Zero
                ? "stock=unavailable"
                : $"unit={_candidate.id} skills=[{DescribeSkills(_candidate)}]";
            MelonLogger.Msg(
                $"[NocturneModernGameplay] MUTATION-PROBE core-{phase}; " +
                $"index={_candidateIndex} original={_originalSkill} " +
                $"mutated={_mutatedSkill}{resultText} {stockText}.");
            DumpResultWork(phase);
        }

        private static void DumpResultWork(string phase)
        {
            try
            {
                var work = rstinit.GBWK;
                if (work == null || work.Pointer == IntPtr.Zero) return;
                const int start = 0x40;
                const int length = 0x50;
                var bytes = new byte[length];
                Marshal.Copy(IntPtr.Add(work.Pointer, start), bytes, 0, length);
                int currentId = work.pCurrentStock == null || work.pCurrentStock.Pointer == IntPtr.Zero
                    ? -1 : work.pCurrentStock.id;
                int workId = work.WorkStock == null || work.WorkStock.Pointer == IntPtr.Zero
                    ? -1 : work.WorkStock.id;
                MelonLogger.Msg(
                    $"[NocturneModernGameplay] MUTATION-WORK {phase}; " +
                    $"ptr=0x{work.Pointer.ToInt64():X} range=0x{start:X}-0x{start + length - 1:X} " +
                    $"currentUnit={currentId} workUnit={workId} " +
                    $"bytes={BitConverter.ToString(bytes).Replace("-", string.Empty)}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] MUTATION-WORK snapshot failed safely: {ex.Message}");
            }
        }

        // Labeled single-unit timeline snapshot: decodes the same GBWK region DumpResultWork
        // reads raw, into named fields, so the normal-learn and mutation flows for one unit
        // (e.g. High Pixie) can be read as a single ordered log without cross-referencing hex.
        // Read-only; adds no behavior.
        internal static void LogUnifiedTimeline(string tag)
        {
            try
            {
                var work = rstinit.GBWK;
                if (work == null || work.Pointer == IntPtr.Zero)
                {
                    MelonLogger.Msg($"[NocturneModernGameplay] TIMELINE {tag}; gbwk=unavailable.");
                    return;
                }

                var currentStock = work.pCurrentStock;
                var workStock = work.WorkStock;
                int currentId = currentStock == null || currentStock.Pointer == IntPtr.Zero
                    ? -1 : currentStock.id;
                int workId = workStock == null || workStock.Pointer == IntPtr.Zero
                    ? -1 : workStock.id;

                byte flag7e = Marshal.ReadByte(work.Pointer, 0x7e);
                var progress80 = new byte[6];
                Marshal.Copy(IntPtr.Add(work.Pointer, 0x80), progress80, 0, 6);
                IntPtr action88 = Marshal.ReadIntPtr(work.Pointer, 0x88);
                byte pUpSkillIndex = Marshal.ReadByte(work.Pointer, 0x4c);
                ushort pUpSkillId = unchecked((ushort)Marshal.ReadInt16(work.Pointer, 0x4e));

                byte currentFlags10 = currentStock != null && currentStock.Pointer != IntPtr.Zero
                    ? Marshal.ReadByte(currentStock.Pointer, 0x10) : (byte)0;
                byte workFlags10 = workStock != null && workStock.Pointer != IntPtr.Zero
                    ? Marshal.ReadByte(workStock.Pointer, 0x10) : (byte)0;

                MelonLogger.Msg(
                    $"[NocturneModernGameplay] TIMELINE {tag}; frame={UnityEngine.Time.frameCount} " +
                    $"currentUnit={currentId} workUnit={workId} " +
                    $"PUpSkillIndex={pUpSkillIndex} PUpSkillID={pUpSkillId} " +
                    $"SelectSkillID={work.SelectSkillID} DefSkillResult={work.DefSkillResult} " +
                    $"gbwk+0x7E={flag7e} gbwk+0x80=[{string.Join(",", progress80)}] " +
                    $"gbwk+0x88=0x{action88.ToInt64():X} " +
                    $"currentStockFlags10=0x{currentFlags10:X2}(bit6={(currentFlags10 & 0x40) != 0}) " +
                    $"workStockFlags10=0x{workFlags10:X2}(bit6={(workFlags10 & 0x40) != 0}) " +
                    $"targetIndex={work.TargetIndex} targetCnt={work.TargetCnt}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] TIMELINE {tag} failed safely: {ex.Message}");
            }
        }

        // Unified cross-site pipeline snapshot. Called with the same field set from every
        // Mutation-related hook (rstRndGetPowerUpSkill, cmbGetMutationSkill,
        // rstCalcSkillPowerUpCore, rstChkAddSkill, rstUpdateSeqSkillPowerUp) so a single
        // native Mutation attempt can be reconstructed by lining up consecutive PIPELINE
        // log lines. Read-only; adds no behavior and applies no correction.
        private static long _pipelineSeq;

        internal static void LogPipelineSnapshot(
            string site, string phase, string extra = "")
        {
            long seq = System.Threading.Interlocked.Increment(ref _pipelineSeq);
            try
            {
                var work = rstinit.GBWK;
                if (work == null || work.Pointer == IntPtr.Zero)
                {
                    MelonLogger.Msg(
                        $"[NocturneModernGameplay] PIPELINE #{seq} {site}-{phase}; gbwk=unavailable.");
                    return;
                }

                var seqInfo = work.SeqInfo;
                var currentStock = work.pCurrentStock;
                var workStock = work.WorkStock;
                int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero
                    ? -1 : currentStock.id;
                int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero
                    ? -1 : workStock.id;
                IntPtr currentStockPtr = currentStock?.Pointer ?? IntPtr.Zero;
                IntPtr workStockPtr = workStock?.Pointer ?? IntPtr.Zero;

                byte gbwk4a = Marshal.ReadByte(work.Pointer, 0x4a);
                byte workStock4a = workStockPtr != IntPtr.Zero
                    ? Marshal.ReadByte(workStockPtr, 0x4a) : (byte)0;
                string workStock4aText = workStockPtr != IntPtr.Zero
                    ? workStock4a.ToString() : "n/a";

                // Candidate "next scheduled normal-learn skill" fields identified in
                // rstAddSkill and in the rstCalc candidate-selection routine feeding
                // rstChkAddSkill. Read-only; the write-side owner is still under
                // investigation (see the investigation notes for this offset pair).
                string workStock32Text = workStockPtr != IntPtr.Zero
                    ? unchecked((ushort)Marshal.ReadInt16(workStockPtr, 0x32)).ToString() : "n/a";
                string workStock34Text = workStockPtr != IntPtr.Zero
                    ? unchecked((ushort)Marshal.ReadInt16(workStockPtr, 0x34)).ToString() : "n/a";

                byte flag7e = Marshal.ReadByte(work.Pointer, 0x7e);
                var progress80 = new byte[6];
                Marshal.Copy(IntPtr.Add(work.Pointer, 0x80), progress80, 0, 6);
                IntPtr action88 = Marshal.ReadIntPtr(work.Pointer, 0x88);

                byte currentFlags10 = currentStockPtr != IntPtr.Zero
                    ? Marshal.ReadByte(currentStockPtr, 0x10) : (byte)0;
                byte workFlags10 = workStockPtr != IntPtr.Zero
                    ? Marshal.ReadByte(workStockPtr, 0x10) : (byte)0;

                bool workSlotHandled = workStockPtr != IntPtr.Zero
                    && SkillMutationLearnAsNew.IsSlotHandled(workStockPtr, work.PUpSkillIndex);
                bool currentSlotHandled = currentStockPtr != IntPtr.Zero
                    && SkillMutationLearnAsNew.IsSlotHandled(currentStockPtr, work.PUpSkillIndex);

                MelonLogger.Msg(
                    $"[NocturneModernGameplay] PIPELINE #{seq} {site}-{phase}; " +
                    $"frame={UnityEngine.Time.frameCount} " +
                    $"seq={seqInfo.Current}/{seqInfo.Next}/{seqInfo.Last}/{seqInfo.Change} " +
                    $"targetIndex={work.TargetIndex} targetCnt={work.TargetCnt} " +
                    $"currentUnit={currentUnit} workUnit={workUnit} " +
                    $"currentStockPtr=0x{currentStockPtr.ToInt64():X} workStockPtr=0x{workStockPtr.ToInt64():X} " +
                    $"gbwk+0x4a={gbwk4a} workStock+0x4a={workStock4aText} " +
                    $"workStock+0x32={workStock32Text} workStock+0x34={workStock34Text} " +
                    $"PUpSkillIndex={work.PUpSkillIndex} PUpSkillID={work.PUpSkillID} " +
                    $"workSlotHandled={workSlotHandled} currentSlotHandled={currentSlotHandled} " +
                    $"currentStockFlags10=0x{currentFlags10:X2}(bit6={(currentFlags10 & 0x40) != 0}) " +
                    $"workStockFlags10=0x{workFlags10:X2}(bit6={(workFlags10 & 0x40) != 0}) " +
                    $"gbwk+0x7E={flag7e} gbwk+0x80=[{string.Join(",", progress80)}] " +
                    $"gbwk+0x88=0x{action88.ToInt64():X} " +
                    $"active=[{SkillMutationLearnAsNew.ActiveSummary}] " +
                    $"handledSlotCount={SkillMutationLearnAsNew.HandledSlotCount} " +
                    $"queueDepth={SkillMutationLearnAsNew.QueueDepth} " +
                    $"{extra}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] PIPELINE #{seq} {site}-{phase} failed safely: {ex.Message}");
            }
        }

        // Read-only HasSkill state logging, shared by the rstChkAddSkill,
        // cmbGetMutationSkill, and rstOverWriteSkill hooks. Never modifies
        // game state or return values; only reads current possession status
        // via SkillMutationLearnAsNew.CheckHasSkill (the mod's own existing
        // HasSkill helper, exposed read-only).
        internal static void LogHasSkillState(
            string site,
            Il2Cppnewdata_H.datUnitWork_t? stock,
            int slotIndex,
            ushort normalLearnSkill,
            ushort mutatedSkill)
        {
            try
            {
                if (stock == null || stock.Pointer == IntPtr.Zero)
                {
                    MelonLogger.Msg($"[NocturneModernGameplay] HASSKILL-TRACE {site}; stock=unavailable.");
                    return;
                }
                bool hasNormal = SkillMutationLearnAsNew.CheckHasSkill(stock, normalLearnSkill);
                bool hasMutated = SkillMutationLearnAsNew.CheckHasSkill(stock, mutatedSkill);
                var seq = rstinit.GBWK.SeqInfo;
                var workStock = rstinit.GBWK.WorkStock;
                string ws32 = workStock != null && workStock.Pointer != IntPtr.Zero
                    ? unchecked((ushort)Marshal.ReadInt16(workStock.Pointer, 0x32)).ToString() : "n/a";
                string ws34 = workStock != null && workStock.Pointer != IntPtr.Zero
                    ? unchecked((ushort)Marshal.ReadInt16(workStock.Pointer, 0x34)).ToString() : "n/a";
                MelonLogger.Msg(
                    "[NocturneModernGameplay] HASSKILL-TRACE " + site + "; " +
                    $"frame={UnityEngine.Time.frameCount} unit={stock.id} " +
                    $"stockPtr=0x{stock.Pointer.ToInt64():X} slot={slotIndex} " +
                    $"normalLearnSkill={normalLearnSkill} hasNormalLearnSkill={hasNormal} " +
                    $"mutatedSkill={mutatedSkill} hasMutatedSkill={hasMutated} " +
                    $"workStock+0x32={ws32} workStock+0x34={ws34} " +
                    $"seqCurrent={seq.Current} seqLast={seq.Last}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] HASSKILL-TRACE {site} failed safely: {ex.Message}");
            }
        }

        private static string DescribeSkills(Il2Cppnewdata_H.datUnitWork_t stock)
        {
            var text = new StringBuilder();
            int count = Math.Min(stock.skillcnt, stock.skill.Length);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(i).Append(':').Append(unchecked((ushort)stock.skill[i]));
            }
            return text.ToString();
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstRndGetPowerUpSkill))]
    internal static class MutationCandidateTelemetryPatch
    {
        private static void Postfix(
            Il2Cppnewdata_H.datUnitWork_t __0,
            sbyte __1,
            ushort __result)
        {
            SkillMutationTelemetry.LogPipelineSnapshot(
                "rstRndGetPowerUpSkill", "postfix",
                $"argIndex={__1} candidateResult={__result} " +
                $"argStockPtr=0x{(__0 == null ? IntPtr.Zero : __0.Pointer).ToInt64():X}");
            if (__0 != null && __0.Pointer != IntPtr.Zero && __result != 0)
            {
                SkillMutationTelemetry.RecordCandidate(__0, __1, __result);
                SkillMutationTelemetry.LogUnifiedTimeline($"mutation-candidate original={__result}");
            }
        }
    }

    [HarmonyPatch(typeof(rstCalcCore), nameof(rstCalcCore.cmbGetMutationSkill))]
    internal static class MutationResultTelemetryPatch
    {
        private static void Prefix(
            ushort __0,
            Il2Cppnewdata_H.datUnitWork_t __1)
        {
            SkillMutationTelemetry.LogPipelineSnapshot(
                "cmbGetMutationSkill", "prefix",
                $"argOriginal={__0} " +
                $"argStockPtr=0x{(__1 == null ? IntPtr.Zero : __1.Pointer).ToInt64():X}");
        }

        private static void Postfix(
            ushort __0,
            Il2Cppnewdata_H.datUnitWork_t __1,
            ushort __result)
        {
            SkillMutationTelemetry.LogPipelineSnapshot(
                "cmbGetMutationSkill", "postfix",
                $"argOriginal={__0} mutatedResult={__result} " +
                $"argStockPtr=0x{(__1 == null ? IntPtr.Zero : __1.Pointer).ToInt64():X}");
            if (__1 != null && __1.Pointer != IntPtr.Zero && __result != 0)
            {
                SkillMutationTelemetry.LogHasSkillState(
                    "mutation-occurred",
                    __1,
                    rstinit.GBWK.PUpSkillIndex,
                    rstinit.GBWK.SelectSkillID,
                    __result);
                SkillMutationTelemetry.RecordMutationResult(__1, __0, __result);
                SkillMutationTelemetry.LogUnifiedTimeline($"mutation-result original={__0} mutated={__result}");
            }
        }
    }

    // Read-only telemetry for rstOverWriteSkill: the single confirmed native write
    // primitive that commits *slot = skill directly into a unit's skill array
    // (see the investigation log, section on rstOverWriteSkill). Added to confirm,
    // with direct evidence, whether this call still fires — and with what skill —
    // for a (stock, index) pair that HandledSlots already reports as handled.
    // Read-only; adds no behavior and blocks nothing.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstOverWriteSkill))]
    internal static class OverWriteSkillTelemetryPatch
    {
        private static void Prefix(ref int __0, ushort __1)
        {
            SkillMutationTelemetry.LogPipelineSnapshot(
                "rstOverWriteSkill", "prefix",
                $"argSlotValueBefore={__0} argSkill={__1}");
            SkillMutationTelemetry.LogHasSkillState(
                "rstOverWriteSkill-prefix",
                rstinit.GBWK.WorkStock,
                rstinit.GBWK.PUpSkillIndex,
                rstinit.GBWK.SelectSkillID,
                __1);
        }

        private static void Postfix(ref int __0, ushort __1)
        {
            SkillMutationTelemetry.LogPipelineSnapshot(
                "rstOverWriteSkill", "postfix",
                $"argSlotValueAfter={__0} argSkill={__1}");
            SkillMutationTelemetry.LogHasSkillState(
                "rstOverWriteSkill-postfix",
                rstinit.GBWK.WorkStock,
                rstinit.GBWK.PUpSkillIndex,
                rstinit.GBWK.SelectSkillID,
                __1);
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class MutationCoreTelemetryPatch
    {
        private static void Prefix()
        {
            SkillMutationTelemetry.RecordCore("before");
        }

        private static void Postfix(sbyte __result)
        {
            SkillMutationTelemetry.RecordCore("after", __result);
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstAddSkill))]
    internal static class MutationAddSkillTelemetryPatch
    {
        private static void Prefix() => SkillMutationTelemetry.RecordWriteStage("rstAddSkill", "before");
        private static void Postfix() => SkillMutationTelemetry.RecordWriteStage("rstAddSkill", "after");
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSeqDestroySkill))]
    internal static class MutationDestroySkillTelemetryPatch
    {
        private static void Prefix() => SkillMutationTelemetry.RecordWriteStage("rstCalcSeqDestroySkill", "before");
        private static void Postfix() => SkillMutationTelemetry.RecordWriteStage("rstCalcSeqDestroySkill", "after");
    }

    [HarmonyPatch(typeof(fclCombineCalcCore), nameof(fclCombineCalcCore.cmbAddSkill))]
    internal static class MutationCombineAddSkillTelemetryPatch
    {
        private static void Prefix(ref ushort __0, Il2Cppnewdata_H.datUnitWork_t __1)
        {
            if (__1 != null && __1.Pointer != IntPtr.Zero)
            {
                SkillMutationLearnAsNew.OverrideQueuedLearnSkill(ref __0, __1);
                SkillMutationTelemetry.RecordWriteStage("cmbAddSkill", "before", __0);
            }
        }

        private static void Postfix(ushort __0, Il2Cppnewdata_H.datUnitWork_t __1)
        {
            if (__1 != null && __1.Pointer != IntPtr.Zero)
            {
                SkillMutationTelemetry.RecordWriteStage("cmbAddSkill", "after", __0);
            }
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
    internal static class MutationPowerUpSequenceTelemetryPatch
    {
        private static bool Prefix()
        {
            if (SkillMutationLearnAsNew.InterceptQueuedPowerUpReturn())
                return false;
            SkillMutationTelemetry.ObserveMutationSequence("rstUpdateSeqSkillPowerUp-before");
            return true;
        }
        private static void Postfix() => SkillMutationTelemetry.ObserveMutationSequence("rstUpdateSeqSkillPowerUp-after");
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDestroySkill))]
    internal static class MutationDestroySequenceTelemetryPatch
    {
        private static void Prefix()
        {
            SkillMutationLearnAsNew.InsideDestroySkillScope = true;
            SkillMutationTelemetry.ObserveMutationSequence("rstUpdateSeqDestroySkill-before");
        }
        private static void Postfix()
        {
            // Synchronize the synthetic ninth-slot selection immediately after
            // native cursor handling and before the frame is rendered. Waiting
            // for the mod's general OnUpdate can leave one stale cached frame.
            SkillMutationLearnAsNew.CorrectQueuedUiSelection();
            SkillMutationTelemetry.ObserveMutationSequence("rstUpdateSeqDestroySkill-after");
            SkillMutationLearnAsNew.InsideDestroySkillScope = false;
        }
    }

    // Read-only scope marker for rstUpdateSeqDestroyConfirm. No prior telemetry
    // existed on this method; this hook adds only the scope flag used to classify
    // datSkillName.Get callers, plus a matching PIPELINE snapshot for consistency
    // with the other Mutation-related hooks. No behavior change.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDestroyConfirm))]
    internal static class DestroyConfirmScopeTelemetryPatch
    {
        private static void Prefix()
        {
            SkillMutationLearnAsNew.InsideDestroyConfirmScope = true;
            SkillMutationTelemetry.LogPipelineSnapshot("rstUpdateSeqDestroyConfirm", "prefix");
        }
        private static void Postfix()
        {
            SkillMutationTelemetry.LogPipelineSnapshot("rstUpdateSeqDestroyConfirm", "postfix");
            SkillMutationLearnAsNew.InsideDestroyConfirmScope = false;
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstChkAddSkill))]
    internal static class StandardSkillCapacityCheckTelemetryPatch
    {
        private static bool Prefix(ushort __0, ref sbyte __result)
        {
            SkillMutationLearnAsNew.NotifyResultActivity();
            SkillMutationTelemetry.ObserveResultState($"capacity-before skill={__0}");
            SkillMutationTelemetry.LogUnifiedTimeline($"capacity-before skill={__0}");
            SkillMutationTelemetry.LogPipelineSnapshot(
                "rstChkAddSkill", "prefix", $"argSkill={__0}");
            SkillMutationTelemetry.LogHasSkillState(
                "rstChkAddSkill-prefix",
                rstinit.GBWK.WorkStock,
                SkillMutationLearnAsNew.ActiveIndex,
                __0,
                SkillMutationLearnAsNew.ActiveMutatedSkill);
            SkillMutationLearnAsNew.TryCompletePendingQueuedReturn("rstChkAddSkill");
            if (SkillMutationLearnAsNew.ShouldSuppressQueuedRecurrence(__0))
            {
                __result = 0;
                return false;
            }
            MelonLogger.Msg(
                $"[NocturneModernGameplay] SKILL-LEARN-FLOW capacity-check; skill={__0}.");
            return true;
        }

        private static void Postfix(ushort __0, sbyte __result)
        {
            MelonLogger.Msg(
                $"[NocturneModernGameplay] SKILL-LEARN-FLOW capacity-result; " +
                $"skill={__0} result={__result}.");
            SkillMutationTelemetry.LogUnifiedTimeline($"capacity-after skill={__0} result={__result}");
            SkillMutationTelemetry.LogPipelineSnapshot(
                "rstChkAddSkill", "postfix", $"argSkill={__0} capacityResult={__result}");
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstInitSkillAct))]
    internal static class StandardSkillActionInitTelemetryPatch
    {
        private static void Prefix(sbyte __0)
        {
            SkillMutationTelemetry.ObserveResultState($"action-before position={__0}");
            if (__0 == 8)
            {
                SkillMutationLearnAsNew.CaptureStandardLearnSequence();
                SkillMutationLearnAsNew.NotifyStandardLearnFlowStarted();
            }
            else
            {
                SkillMutationLearnAsNew.NotifyNonForgetSkillAction();
            }
            MelonLogger.Msg(
                $"[NocturneModernGameplay] SKILL-LEARN-FLOW action-init; position={__0}.");
            SkillMutationTelemetry.LogUnifiedTimeline($"action-init position={__0}");
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class ResultSequenceStatePatch
    {
        private static void Postfix()
        {
            SkillMutationTelemetry.ObserveResultState("update-after");
            SkillMutationLearnAsNew.RecoverQueuedCompletionAtResultExit();
            SkillMutationLearnAsNew.CompleteQueuedAfterOuterUpdate();
            SkillMutationLearnAsNew.TryStartQueuedAtResultBoundary();
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class QueuedMutationDefaultSkillCompletionPatch
    {
        private static void Postfix()
        {
            SkillMutationLearnAsNew.CompleteQueuedAtDefaultSkillBoundary();
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstAddSkill))]
    internal static class StandardSkillAddCompletionPatch
    {
        private static void Postfix()
        {
            SkillMutationLearnAsNew.NotifyStandardLearnFlowCompleted("rstAddSkill");
            SkillMutationTelemetry.LogUnifiedTimeline("normal-add-complete");
        }
    }

    // Read-only diagnostic telemetry for rstCalcSkillPowerUpCore call frequency and
    // GBWK+0x10 / stock+0x10 (bit6=0x40) lifecycle investigation. Does not alter any
    // native state, does not clear bit6, and does not add any behavioral patch.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class SkillPowerUpCoreDetailedTelemetryPatch
    {
        private static long _callCounter;

        private static string DescribeFlags10(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return "unavailable";
            try
            {
                byte flags10 = Marshal.ReadByte(pointer, 0x10);
                return $"0x{flags10:X2}(bit6={(flags10 & 0x40) != 0})";
            }
            catch (Exception ex)
            {
                return $"read-failed:{ex.Message}";
            }
        }

        private static string DescribeGbwk1c(IntPtr gbwkPtr)
        {
            if (gbwkPtr == IntPtr.Zero) return "unavailable";
            try
            {
                return Marshal.ReadInt32(gbwkPtr, 0x1c).ToString();
            }
            catch (Exception ex)
            {
                return $"read-failed:{ex.Message}";
            }
        }

        private static void Prefix()
        {
            _callCounter++;
            try
            {
                var gbwk = rstinit.GBWK;
                IntPtr gbwkPtr = gbwk != null ? gbwk.Pointer : IntPtr.Zero;

                var currentStock = gbwk?.pCurrentStock;
                var workStock = gbwk?.WorkStock;

                int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero
                    ? -1 : currentStock.id;
                int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero
                    ? -1 : workStock.id;

                IntPtr currentStockPtr = currentStock?.Pointer ?? IntPtr.Zero;
                IntPtr workStockPtr = workStock?.Pointer ?? IntPtr.Zero;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] CORE-TRACE prefix; " +
                    $"call={_callCounter} frame={Time.frameCount} " +
                    $"gbwkPtr=0x{gbwkPtr.ToInt64():X} " +
                    $"currentUnit={currentUnit} currentStockPtr=0x{currentStockPtr.ToInt64():X} " +
                    $"workUnit={workUnit} workStockPtr=0x{workStockPtr.ToInt64():X} " +
                    $"gbwkFlags10={DescribeFlags10(gbwkPtr)} " +
                    $"currentStockFlags10={DescribeFlags10(currentStockPtr)} " +
                    $"workStockFlags10={DescribeFlags10(workStockPtr)} " +
                    $"gbwk1c={DescribeGbwk1c(gbwkPtr)} " +
                    $"targetIndex={gbwk?.TargetIndex} targetCnt={gbwk?.TargetCnt}.");
                SkillMutationTelemetry.LogPipelineSnapshot("rstCalcSkillPowerUpCore", "prefix");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] CORE-TRACE prefix unavailable: {ex.Message}");
            }
        }

        private static void Postfix(sbyte __result)
        {
            try
            {
                var gbwk = rstinit.GBWK;
                IntPtr gbwkPtr = gbwk != null ? gbwk.Pointer : IntPtr.Zero;

                var currentStock = gbwk?.pCurrentStock;
                var workStock = gbwk?.WorkStock;

                int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero
                    ? -1 : currentStock.id;
                int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero
                    ? -1 : workStock.id;

                IntPtr currentStockPtr = currentStock?.Pointer ?? IntPtr.Zero;
                IntPtr workStockPtr = workStock?.Pointer ?? IntPtr.Zero;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] CORE-TRACE postfix; " +
                    $"call={_callCounter} frame={Time.frameCount} result={__result} " +
                    $"gbwkPtr=0x{gbwkPtr.ToInt64():X} " +
                    $"currentUnit={currentUnit} currentStockPtr=0x{currentStockPtr.ToInt64():X} " +
                    $"workUnit={workUnit} workStockPtr=0x{workStockPtr.ToInt64():X} " +
                    $"gbwkFlags10={DescribeFlags10(gbwkPtr)} " +
                    $"currentStockFlags10={DescribeFlags10(currentStockPtr)} " +
                    $"workStockFlags10={DescribeFlags10(workStockPtr)} " +
                    $"gbwk1c={DescribeGbwk1c(gbwkPtr)} " +
                    $"targetIndex={gbwk?.TargetIndex} targetCnt={gbwk?.TargetCnt}.");
                SkillMutationTelemetry.LogPipelineSnapshot(
                    "rstCalcSkillPowerUpCore", "postfix", $"coreResult={__result}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] CORE-TRACE postfix unavailable: {ex.Message}");
            }
        }
    }

    // Read-only diagnostic telemetry for rstUpdateSeqSkillPowerUp: captures pCurrentStock
    // and WorkStock flags10/bit6 state, GBWK+0x1c, GBWK+0x7E, and Target cursor state both
    // before and after the call, so the two native bit6-clear sites inside this method can
    // be inferred from the before/after delta without hooking mid-function. Does not alter
    // any native state and does not add any behavioral patch.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqSkillPowerUp))]
    internal static class SkillPowerUpUpdateClearTelemetryPatch
    {
        private static long _callCounter;

        private static string DescribeFlags10(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return "unavailable";
            try
            {
                byte flags10 = Marshal.ReadByte(pointer, 0x10);
                return $"0x{flags10:X2}(bit6={(flags10 & 0x40) != 0})";
            }
            catch (Exception ex)
            {
                return $"read-failed:{ex.Message}";
            }
        }

        private static string DescribeGbwk1c(IntPtr gbwkPtr)
        {
            if (gbwkPtr == IntPtr.Zero) return "unavailable";
            try
            {
                return Marshal.ReadInt32(gbwkPtr, 0x1c).ToString();
            }
            catch (Exception ex)
            {
                return $"read-failed:{ex.Message}";
            }
        }

        private static string DescribeGbwk7e(IntPtr gbwkPtr)
        {
            if (gbwkPtr == IntPtr.Zero) return "unavailable";
            try
            {
                return Marshal.ReadByte(gbwkPtr, 0x7e).ToString();
            }
            catch (Exception ex)
            {
                return $"read-failed:{ex.Message}";
            }
        }

        private static string Snapshot(string phase, long call)
        {
            var gbwk = rstinit.GBWK;
            IntPtr gbwkPtr = gbwk != null ? gbwk.Pointer : IntPtr.Zero;

            var currentStock = gbwk?.pCurrentStock;
            var workStock = gbwk?.WorkStock;

            int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero
                ? -1 : currentStock.id;
            int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero
                ? -1 : workStock.id;

            IntPtr currentStockPtr = currentStock?.Pointer ?? IntPtr.Zero;
            IntPtr workStockPtr = workStock?.Pointer ?? IntPtr.Zero;

            return
                $"[NocturneModernGameplay] UPDATE-CLEAR-TRACE {phase}; " +
                $"call={call} frame={Time.frameCount} " +
                $"gbwkPtr=0x{gbwkPtr.ToInt64():X} " +
                $"currentUnit={currentUnit} currentStockPtr=0x{currentStockPtr.ToInt64():X} " +
                $"currentStockFlags10={DescribeFlags10(currentStockPtr)} " +
                $"workUnit={workUnit} workStockPtr=0x{workStockPtr.ToInt64():X} " +
                $"workStockFlags10={DescribeFlags10(workStockPtr)} " +
                $"gbwk1c={DescribeGbwk1c(gbwkPtr)} gbwk7e={DescribeGbwk7e(gbwkPtr)} " +
                $"targetIndex={gbwk?.TargetIndex} targetCnt={gbwk?.TargetCnt}.";
        }

        private static void Prefix()
        {
            _callCounter++;
            try
            {
                MelonLogger.Msg(Snapshot("before", _callCounter));
                SkillMutationTelemetry.LogPipelineSnapshot("rstUpdateSeqSkillPowerUp", "prefix");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] UPDATE-CLEAR-TRACE before unavailable: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            try
            {
                MelonLogger.Msg(Snapshot("after", _callCounter));
                SkillMutationTelemetry.LogPipelineSnapshot("rstUpdateSeqSkillPowerUp", "postfix");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] UPDATE-CLEAR-TRACE after unavailable: {ex.Message}");
            }
        }
    }

    // Read-only telemetry for rstCalcCore.cmbChkLevelUpEvent: this native function was
    // confirmed stateless (it re-scans a fixed 24-entry static level-up table every call
    // and holds no cursor of its own). This hook exists solely to observe, from the
    // managed side, exactly which candidates it returns and in what order, so the caller
    // that actually selects "which skill to hand to rstChkAddSkill next" can be
    // characterized from real gameplay data instead of further static disassembly.
    // Strictly read-only: does not modify __0, __1 (ref sbyte), or __result.
    [HarmonyPatch(typeof(rstCalcCore), nameof(rstCalcCore.cmbChkLevelUpEvent))]
    internal static class LevelUpEventTelemetryPatch
    {
        private static long _callCounter;

        private static string DescribeCallerAttempt()
        {
            // Best-effort only: Il2Cpp native frames between this managed Postfix and the
            // native caller are normally invisible to a managed StackTrace. This is kept
            // as a diagnostic attempt, not a dependency; if it yields nothing useful the
            // log says so plainly rather than fabricating a caller name.
            try
            {
                var trace = new StackTrace(1, false);
                var frames = trace.GetFrames();
                if (frames == null || frames.Length == 0) return "no-managed-frames";
                var names = new StringBuilder();
                int shown = 0;
                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    if (method == null) continue;
                    if (shown > 0) names.Append(" <- ");
                    names.Append(method.DeclaringType?.Name).Append('.').Append(method.Name);
                    shown++;
                    if (shown >= 4) break;
                }
                return shown == 0 ? "no-named-frames" : names.ToString();
            }
            catch (Exception ex)
            {
                return $"stacktrace-unavailable:{ex.Message}";
            }
        }

        private static void Postfix(
            Il2Cppnewdata_H.datUnitWork_t __0,
            sbyte __1,
            Il2CppReferenceArray<Il2Cppresult2_H.fclSkillParam_t> __result)
        {
            _callCounter++;
            try
            {
                int unitId = __0 == null || __0.Pointer == IntPtr.Zero ? -1 : __0.id;
                IntPtr stockPtr = __0?.Pointer ?? IntPtr.Zero;
                ushort stock14 = stockPtr != IntPtr.Zero
                    ? unchecked((ushort)Marshal.ReadInt16(stockPtr, 0x14)) : (ushort)0;
                byte stock24 = stockPtr != IntPtr.Zero
                    ? Marshal.ReadByte(stockPtr, 0x24) : (byte)0;

                var gbwk = rstinit.GBWK;
                var seqInfo = gbwk?.SeqInfo;
                var currentStock = gbwk?.pCurrentStock;
                var workStock = gbwk?.WorkStock;
                int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero
                    ? -1 : currentStock.id;
                int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero
                    ? -1 : workStock.id;

                IntPtr resultArrayPtr = __result == null ? IntPtr.Zero : __result.Pointer;
                int arrayLength = __result?.Length ?? 0;

                MelonLogger.Msg(
                    "[NocturneModernGameplay] LEVELUP-EVENT-TRACE call; " +
                    $"call={_callCounter} frame={Time.frameCount} " +
                    $"unit={unitId} stockPtr=0x{stockPtr.ToInt64():X} " +
                    $"stock+0x14={stock14} stock+0x24={stock24} " +
                    $"resultArrayPtr=0x{resultArrayPtr.ToInt64():X} arrayLength={arrayLength} " +
                    $"matchCount(ref sbyte)={__1} " +
                    $"currentUnit={currentUnit} workUnit={workUnit} " +
                    $"targetIndex={gbwk?.TargetIndex} targetCnt={gbwk?.TargetCnt} " +
                    $"selectSkillId={gbwk?.SelectSkillID} " +
                    $"pUpSkillId={gbwk?.PUpSkillID} pUpSkillIndex={gbwk?.PUpSkillIndex} " +
                    $"seqCurrent={seqInfo?.Current} seqLast={seqInfo?.Last} " +
                    $"callerAttempt=[{DescribeCallerAttempt()}].");

                if (__result != null)
                {
                    int shown = Math.Min(arrayLength, 24);
                    for (int i = 0; i < shown; i++)
                    {
                        var element = __result[i];
                        if (element == null || element.Pointer == IntPtr.Zero)
                        {
                            MelonLogger.Msg(
                                $"[NocturneModernGameplay] LEVELUP-EVENT-TRACE element; call={_callCounter} index={i} null.");
                            continue;
                        }
                        MelonLogger.Msg(
                            $"[NocturneModernGameplay] LEVELUP-EVENT-TRACE element; call={_callCounter} index={i} " +
                            $"elementPtr=0x{element.Pointer.ToInt64():X} " +
                            $"targetLevel={element.TargetLevel} type={element.Type} param={element.Param}.");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] LEVELUP-EVENT-TRACE call={_callCounter} failed safely: {ex.Message}");
            }
        }
    }

    // Read-only telemetry for datSkillName.Get: captures the skill ID that actually
    // reaches display, correlated with the current WorkStock/CurrentStock contents at
    // that instant. Postfix (not Prefix) is used deliberately, so this observes __0
    // AFTER any existing Prefix (e.g. MutationQueuedSkillNamePatch.OverrideQueuedUiSkillId)
    // has already rewritten it — i.e. the final value actually handed to the native
    // text lookup. Limited to windows where a forget-UI/Mutation flow is plausibly
    // active (seq 21/22, or Learn-As-New has an active/queued item), so this does not
    // spam every ordinary menu/dialog that also calls datSkillName.Get.
    // Strictly read-only: does not modify __0, __1, or any game state.
    [HarmonyPatch(typeof(datSkillName), nameof(datSkillName.Get),
        new[] { typeof(int), typeof(int) })]
    internal static class SkillDisplayTelemetryPatch
    {
        private static void Postfix(int __0, int __1)
        {
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                var seq = gbwk.SeqInfo;
                bool insideDestroySkill = SkillMutationLearnAsNew.InsideDestroySkillScope;
                bool insideDestroyConfirm = SkillMutationLearnAsNew.InsideDestroyConfirmScope;
                // Confirmed caller: set synchronously by the enclosing native method's
                // own Harmony Prefix/Postfix (single-threaded call, so this is a fact,
                // not an inference). "Other" covers every other confirmed caller
                // (rstCalc's Mutation-name presentation, rstUpdateSeqDefaultSkill,
                // rstUpdateSeqHeartsSkill, rstStandbyHeartsSkillPowerUp, etc.).
                string caller = insideDestroySkill ? "DestroySkill"
                    : insideDestroyConfirm ? "DestroyConfirm"
                    : "Other";
                // Auxiliary-only, weaker signal kept for cross-reference; never
                // treated as a confirmed caller by itself.
                string routeLikely = seq.Current == 21 ? "DestroySkill-likely"
                    : seq.Current == 22 ? "DestroyConfirm-likely"
                    : "n/a";

                bool forgetFlowLikely = insideDestroySkill || insideDestroyConfirm
                    || seq.Current == 21 || seq.Current == 22
                    || SkillMutationLearnAsNew.SuppressNestedMutation
                    || SkillMutationLearnAsNew.QueueDepth > 0;
                if (!forgetFlowLikely) return;

                var currentStock = gbwk.pCurrentStock;
                var workStock = gbwk.WorkStock;
                int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero
                    ? -1 : currentStock.id;
                int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero
                    ? -1 : workStock.id;
                IntPtr currentStockPtr = currentStock?.Pointer ?? IntPtr.Zero;
                IntPtr workStockPtr = workStock?.Pointer ?? IntPtr.Zero;

                string skillsList = "unavailable";
                int displaySlot = -1;
                if (workStock != null && workStock.Pointer != IntPtr.Zero)
                {
                    var sb = new StringBuilder();
                    int count = Math.Min(workStock.skill.Length, 8);
                    for (int i = 0; i < count; i++)
                    {
                        ushort s = unchecked((ushort)workStock.skill[i]);
                        if (i > 0) sb.Append(',');
                        sb.Append(i).Append(':').Append(s);
                        if (s == unchecked((ushort)__0)) displaySlot = i;
                    }
                    skillsList = sb.ToString();
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-DISPLAY-TRACE; " +
                    $"frame={UnityEngine.Time.frameCount} skillId={__0} arg1={__1} " +
                    $"caller={caller} routeLikely={routeLikely} " +
                    $"gbwkPtr=0x{gbwk.Pointer.ToInt64():X} " +
                    $"currentUnit={currentUnit} workUnit={workUnit} " +
                    $"currentStockPtr=0x{currentStockPtr.ToInt64():X} workStockPtr=0x{workStockPtr.ToInt64():X} " +
                    $"workStockSkills=[{skillsList}] displaySlot={displaySlot} " +
                    $"PUpSkillIndex={gbwk.PUpSkillIndex} PUpSkillID={gbwk.PUpSkillID} SelectSkillID={gbwk.SelectSkillID} " +
                    $"targetIndex={gbwk.TargetIndex} targetCnt={gbwk.TargetCnt} " +
                    $"seqCurrent={seq.Current} seqLast={seq.Last} " +
                    $"active=[{SkillMutationLearnAsNew.ActiveSummary}] " +
                    $"pendingHead=[{SkillMutationLearnAsNew.PendingHeadSummary}].");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] SKILL-DISPLAY-TRACE failed safely: {ex.Message}");
            }
        }
    }

    // Read-only telemetry for the two native unit-transition entry points identified
    // during Design B (per-unit inline gate) feasibility analysis:
    //   - rstCalcSeqDevilLevelUp: contains an inline WorkStock-advance loop and is
    //     called from rstCalc, which treats a non-zero return as "nothing to do this
    //     frame" and returns early without side effects.
    //   - rstSetCurrentDevil: a standalone method with the same WorkStock-advance
    //     loop structure, called indirectly (no static call site found), so its
    //     actual trigger context is unknown until observed at runtime.
    // Purpose: confirm whether either is re-entered frame-to-frame with the SAME
    // TargetIndex/WorkStock+0x4a/seq state (i.e. genuinely idle/retry-safe) during
    // an active Learn-As-New transaction, before any gating is attempted.
    // Strictly read-only: does not modify return values, arguments, or any game
    // state. Does not gate or block native execution.
    internal static class DevilTransitionTelemetryPatch
    {
        private static long _devilLevelUpCallCounter;
        private static long _setCurrentDevilCallCounter;

        private static string Snapshot(string site, string phase, long call, string extra = "")
        {
            var gbwk = rstinit.GBWK;
            if (gbwk == null || gbwk.Pointer == IntPtr.Zero)
                return $"[NocturneModernGameplay] DEVIL-TRANSITION-TRACE {site}-{phase}; call={call} gbwk=unavailable.";

            var seq = gbwk.SeqInfo;
            var currentStock = gbwk.pCurrentStock;
            var workStock = gbwk.WorkStock;
            int currentUnit = currentStock == null || currentStock.Pointer == IntPtr.Zero ? -1 : currentStock.id;
            int workUnit = workStock == null || workStock.Pointer == IntPtr.Zero ? -1 : workStock.id;
            IntPtr currentStockPtr = currentStock?.Pointer ?? IntPtr.Zero;
            IntPtr workStockPtr = workStock?.Pointer ?? IntPtr.Zero;

            byte gbwk4a = Marshal.ReadByte(gbwk.Pointer, 0x4a);
            string workStock4a = workStockPtr != IntPtr.Zero
                ? Marshal.ReadByte(workStockPtr, 0x4a).ToString() : "n/a";

            return
                "[NocturneModernGameplay] DEVIL-TRANSITION-TRACE " + site + "-" + phase + "; " +
                $"call={call} frame={UnityEngine.Time.frameCount} " +
                $"seqCurrent={seq.Current} seqLast={seq.Last} " +
                $"targetIndex={gbwk.TargetIndex} targetCnt={gbwk.TargetCnt} " +
                $"currentUnit={currentUnit} workUnit={workUnit} " +
                $"currentStockPtr=0x{currentStockPtr.ToInt64():X} workStockPtr=0x{workStockPtr.ToInt64():X} " +
                $"gbwk+0x4a={gbwk4a} workStock+0x4a={workStock4a} " +
                $"PUpSkillIndex={gbwk.PUpSkillIndex} PUpSkillID={gbwk.PUpSkillID} " +
                $"activeTransaction=[{SkillMutationLearnAsNew.ActiveSummary}] " +
                $"candidateCount={SkillMutationTelemetry.CandidateCount} " +
                $"candidate=[{SkillMutationTelemetry.CandidateSummary}] " +
                $"{extra}";
        }

        internal static void LogDevilLevelUpPrefix()
        {
            _devilLevelUpCallCounter++;
            try { MelonLogger.Msg(Snapshot("rstCalcSeqDevilLevelUp", "prefix", _devilLevelUpCallCounter)); }
            catch (Exception ex) { MelonLogger.Warning($"[NocturneModernGameplay] DEVIL-TRANSITION-TRACE prefix failed safely: {ex.Message}"); }
        }

        internal static void LogDevilLevelUpPostfix(int result)
        {
            try { MelonLogger.Msg(Snapshot("rstCalcSeqDevilLevelUp", "postfix", _devilLevelUpCallCounter, $"result={result}")); }
            catch (Exception ex) { MelonLogger.Warning($"[NocturneModernGameplay] DEVIL-TRANSITION-TRACE postfix failed safely: {ex.Message}"); }
        }

        internal static void LogSetCurrentDevilPrefix()
        {
            _setCurrentDevilCallCounter++;
            try { MelonLogger.Msg(Snapshot("rstSetCurrentDevil", "prefix", _setCurrentDevilCallCounter)); }
            catch (Exception ex) { MelonLogger.Warning($"[NocturneModernGameplay] DEVIL-TRANSITION-TRACE prefix failed safely: {ex.Message}"); }
        }

        internal static void LogSetCurrentDevilPostfix(sbyte result)
        {
            try { MelonLogger.Msg(Snapshot("rstSetCurrentDevil", "postfix", _setCurrentDevilCallCounter, $"result={result}")); }
            catch (Exception ex) { MelonLogger.Warning($"[NocturneModernGameplay] DEVIL-TRANSITION-TRACE postfix failed safely: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSeqDevilLevelUp))]
    internal static class DevilLevelUpTelemetryPatch
    {
        private static void Prefix() => DevilTransitionTelemetryPatch.LogDevilLevelUpPrefix();
        private static void Postfix(int __result) => DevilTransitionTelemetryPatch.LogDevilLevelUpPostfix(__result);
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstSetCurrentDevil))]
    internal static class SetCurrentDevilTelemetryPatch
    {
        private static void Prefix() => DevilTransitionTelemetryPatch.LogSetCurrentDevilPrefix();
        private static void Postfix(sbyte __result) => DevilTransitionTelemetryPatch.LogSetCurrentDevilPostfix(__result);
    }
}
