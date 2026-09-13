using System;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - GENERIC RUNTIME SKILL OWNERSHIP WRITER
    // TRACE. Read-only observer only. Never writes any field.
    //
    // Purpose: exhaustive static analysis of GBWK+0x32's writer
    // (0x18227C330), its two direct consumer/callee candidates
    // (0x1821690D0, 0x182280B40), rstUpdateSeqDefaultSkill itself, and
    // every immediately adjacent seq handler (HeartsMaster/HeartsSkill/
    // HeartsEvent/DevilParam) found NO skill[]/skillcnt write anywhere.
    // Rather than keep guessing at native call targets, this class
    // brackets several managed method boundaries with a SHARED, uniform
    // before/after snapshot (SeqInfo.Current/Last, GBWK.Flag, unit,
    // skillcnt, the full skill[] array, GBWK+0x32, sourceObj+0x3c) and
    // logs only when something actually changed across that one method
    // call - letting a real ownership change be localized to whichever
    // boundary first shows it, without guessing at inline hook targets.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdate))]
    internal static class SkillWriterTrace_rstUpdate
    {
        private static SkillWriterBoundaryShared.Snapshot _before;
        private static void Prefix() => _before = SkillWriterBoundaryShared.Capture();
        private static void Postfix()
        {
            var after = SkillWriterBoundaryShared.Capture();
            SkillWriterBoundaryShared.ReportIfChanged("rstupdate.rstUpdate", _before, after);
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class SkillWriterTrace_DefaultSkill
    {
        private static SkillWriterBoundaryShared.Snapshot _before;
        private static void Prefix() => _before = SkillWriterBoundaryShared.Capture();
        private static void Postfix()
        {
            var after = SkillWriterBoundaryShared.Capture();
            SkillWriterBoundaryShared.ReportIfChanged("rstupdate.rstUpdateSeqDefaultSkill", _before, after);
        }
    }

    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDestroySkill))]
    internal static class SkillWriterTrace_DestroySkill
    {
        private static SkillWriterBoundaryShared.Snapshot _before;
        private static void Prefix() => _before = SkillWriterBoundaryShared.Capture();
        private static void Postfix()
        {
            var after = SkillWriterBoundaryShared.Capture();
            SkillWriterBoundaryShared.ReportIfChanged("rstupdate.rstUpdateSeqDestroySkill", _before, after);
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalc))]
    internal static class SkillWriterTrace_rstCalc
    {
        private static SkillWriterBoundaryShared.Snapshot _before;
        private static void Prefix() => _before = SkillWriterBoundaryShared.Capture();
        private static void Postfix()
        {
            var after = SkillWriterBoundaryShared.Capture();
            SkillWriterBoundaryShared.ReportIfChanged("rstcalc.rstCalc", _before, after);
        }
    }

    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstCalcSkillPowerUpCore))]
    internal static class SkillWriterTrace_Core
    {
        private static SkillWriterBoundaryShared.Snapshot _before;
        private static void Prefix() => _before = SkillWriterBoundaryShared.Capture();
        private static void Postfix()
        {
            var after = SkillWriterBoundaryShared.Capture();
            SkillWriterBoundaryShared.ReportIfChanged("rstcalc.rstCalcSkillPowerUpCore", _before, after);
        }
    }

    internal static class SkillWriterBoundaryShared
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why. This one hooks
        // rstupdate.rstUpdate/rstUpdateSeqDefaultSkill AND rstcalc.rstCalc
        // every single frame, making it one of the heavier candidates for
        // diagnostic overhead.
        internal static readonly bool Enabled = false;
        private static int _invocationCounter;

        internal struct Snapshot
        {
            internal bool Valid;
            internal int Seq;
            internal int SeqLast;
            internal sbyte Flag;
            internal int Unit;
            internal int SkillCnt;
            internal int[] Skills;
            internal ushort Pending32;
            internal byte State3c;
        }

        internal static Snapshot Capture()
        {
            var snap = new Snapshot { Skills = Array.Empty<int>() };
            if (!Enabled) return snap;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return snap;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return snap;

                snap.Seq = gbwk.SeqInfo.Current;
                snap.SeqLast = gbwk.SeqInfo.Last;
                snap.Flag = gbwk.Flag;
                snap.Unit = stock.id;
                snap.SkillCnt = stock.skillcnt;

                var ownedArray = stock.skill;
                int len = ownedArray?.Length ?? 0;
                var skills = new int[len];
                for (int i = 0; i < len; i++) skills[i] = ownedArray![i] & 0xFFFF;
                snap.Skills = skills;

                snap.Pending32 = unchecked((ushort)Marshal.ReadInt16(gbwk.Pointer, 0x32));
                snap.State3c = Marshal.ReadByte(gbwk.Pointer, 0x3c);
                snap.Valid = true;
            }
            catch
            {
                snap.Valid = false;
            }
            return snap;
        }

        internal static bool Changed(Snapshot a, Snapshot b)
        {
            if (!a.Valid || !b.Valid) return false;
            if (a.Seq != b.Seq || a.SeqLast != b.SeqLast || a.Flag != b.Flag || a.Unit != b.Unit ||
                a.SkillCnt != b.SkillCnt || a.Pending32 != b.Pending32 || a.State3c != b.State3c) return true;
            if (a.Skills.Length != b.Skills.Length) return true;
            for (int i = 0; i < a.Skills.Length; i++)
                if (a.Skills[i] != b.Skills[i]) return true;
            return false;
        }

        internal static void ReportIfChanged(string methodName, Snapshot before, Snapshot after)
        {
            if (!Enabled) return;
            try
            {
                if (!Changed(before, after)) return;
                int invocation = ++_invocationCounter;
                int frame = UnityEngine.Time.frameCount;

                bool skillCntChanged = before.SkillCnt != after.SkillCnt;
                bool skillsChanged = !SameArray(before.Skills, after.Skills);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-WRITER-BOUNDARY-BEGIN; " +
                    $"method={methodName}; invocation={invocation}; frame={frame}; " +
                    $"unit={before.Unit}; seq={before.Seq}; seqLast={before.SeqLast}; flag={before.Flag}; " +
                    $"skillCnt={before.SkillCnt}; pending32={before.Pending32}; state3c={before.State3c}; " +
                    $"skills=[{string.Join(",", before.Skills)}].");

                MelonLogger.Msg(
                    "[NocturneModernGameplay] SKILL-WRITER-BOUNDARY-END; " +
                    $"method={methodName}; invocation={invocation}; frame={frame}; " +
                    $"unit={after.Unit}; seq={after.Seq}; seqLast={after.SeqLast}; flag={after.Flag}; " +
                    $"skillCnt={after.SkillCnt}; pending32={after.Pending32}; state3c={after.State3c}; " +
                    $"skills=[{string.Join(",", after.Skills)}].");

                if (skillCntChanged || skillsChanged)
                {
                    MelonLogger.Msg(
                        "[NocturneModernGameplay] SKILL-WRITER-CHANGE; " +
                        $"method={methodName}; invocation={invocation}; unit={before.Unit}; " +
                        $"skillCntBefore={before.SkillCnt}; skillCntAfter={after.SkillCnt}; " +
                        $"pendingSkillAtBegin={before.Pending32}; " +
                        $"skillsBefore=[{string.Join(",", before.Skills)}]; " +
                        $"skillsAfter=[{string.Join(",", after.Skills)}].");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SkillWriterBoundaryShared report failed safely: {ex.Message}");
            }
        }

        private static bool SameArray(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
