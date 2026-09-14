using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // HIDDEN NEW SKILL ENTRY - runtime field-offset measurement for
    // GBWK.SelectSkillID (read-only, no writes). Session continuation
    // after AddNewHighlightCorrection.cs's own header comment revealed
    // that the "cursor==8(hidden slot) -> SelectSkillID = pending skill"
    // mapping HIDDEN-SLOT-ARRAY-CHECK observed is done by THIS MOD's own
    // C# code (only while FullCapacityAddNewBridgeState.Active), mirroring
    // what legacy code apparently also had to do by hand - NOT something
    // confirmed to be native's own behavior. For native's OWN forget flow
    // (bridgeActive=False - CursorPosShiftWriteWatchTrace already caught
    // native itself writing Shift=8 unconditionally at VA 0x182288AC1),
    // whether/how native resolves SelectSkillID for that same cursor
    // position 8 is still completely unknown - this investigation's actual
    // next question, per User's 2026-09-14 direction ("どの処理が...
    // pending/new skillを論理選択対象として扱っているか").
    //
    // The managed `gbwk.SelectSkillID` property works fine for reading the
    // VALUE, but a hardware breakpoint on its WRITER (the next planned
    // step, mirroring CursorPosShiftWriteWatchTrace's approach for
    // CursorPos.Shift) needs GBWK's raw byte offset for this field, which
    // is not documented anywhere in this project's prior investigations
    // (unlike SeqInfo.Current/PUpSkillResult/PUpSkillIndex/PUpSkillID/
    // pCurrentStock/WorkStock/SkillCursor, all already pinned down).
    //
    // Same empirical technique as StatusUiFieldOffsetProbe.cs, adapted for
    // a 2-byte SCALAR field instead of an 8-byte POINTER: a single sample
    // (word value == current SelectSkillID) collides far more often by
    // chance than an 8-byte pointer match does, so this takes repeated
    // samples across DIFFERENT observed SelectSkillID values and
    // INTERSECTS the candidate offset sets each time - only an offset that
    // matches on every single distinct value sampled survives, which
    // converges on the true offset (or a very small remaining set) within
    // a handful of samples.
    internal static class SelectSkillIdOffsetProbe
    {
        internal static readonly bool Enabled = true;

        private const int ScanRangeBytes = 0x200;
        private const int MaxSamples = 12;

        private static bool _done = false;
        private static int _sampleCount = 0;
        private static int _lastSampledValue = -1;
        private static HashSet<int>? _candidateOffsets = null;

        internal static void TryProbe()
        {
            if (!Enabled || _done) return;

            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;

                int current = gbwk.SelectSkillID;
                if (current == _lastSampledValue) return; // only sample on an actual change
                _lastSampledValue = current;

                IntPtr gbwkPtr = gbwk.Pointer;
                var matches = new HashSet<int>();
                for (int off = 0; off <= ScanRangeBytes - 2; off += 2)
                {
                    ushort word = unchecked((ushort)Marshal.ReadInt16(gbwkPtr, off));
                    if (word == (ushort)current) matches.Add(off);
                }

                if (_candidateOffsets == null) _candidateOffsets = matches;
                else _candidateOffsets.IntersectWith(matches);

                _sampleCount++;

                string offsetsStr = _candidateOffsets.Count > 0
                    ? string.Join(",", _candidateOffsets.Select(FormatOffset))
                    : "NONE REMAINING (a prior sample's assumption was likely wrong)";

                MelonLogger.Msg(
                    "[NocturneModernGameplay] SELECTSKILLID-OFFSET-PROBE; " +
                    $"sample={_sampleCount}; currentValue={current}; thisSampleMatchCount={matches.Count}; " +
                    $"remainingCandidateCount={_candidateOffsets.Count}; remainingCandidates=[{offsetsStr}].");

                if (_candidateOffsets.Count <= 1 || _sampleCount >= MaxSamples)
                {
                    _done = true;
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SELECTSKILLID-OFFSET-PROBE-DONE; " +
                        $"samples={_sampleCount}; finalCandidateCount={_candidateOffsets.Count}; " +
                        $"finalCandidates=[{offsetsStr}].");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SelectSkillIdOffsetProbe.TryProbe failed safely: {ex.Message}");
            }
        }

        private static string FormatOffset(int off) => $"0x{off:X}";
    }
}
