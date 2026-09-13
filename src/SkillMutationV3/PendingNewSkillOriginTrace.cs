using System;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - PENDING NEW-SKILL ORIGIN trace.
    // Read-only observer only. Never writes any field.
    //
    // Purpose: runtime has now CONFIRMED (Native/Native/Native settings,
    // PUpSkillResult/Index/ID all 0 throughout) that a genuine full-capacity
    // level-up's forget flow results in an atomic replacement - an existing
    // skill is deleted, the array compacts, and a NEW skill (396 in the
    // observed case) lands in the freed slot, with skillcnt staying at 8
    // across the rstUpdateSeqDestroyConfirm call boundary. Static analysis
    // of that method's own confirmed-delete path found no write of any
    // "new skill" value - only delete+compact+skillcnt-recompute+the
    // (misleadingly named) rstAddSkill HP/MP-recalc call. This means the
    // pending "skill to learn this level" value must be stored SOMEWHERE
    // before seq21 is even entered, and something consumes it later to
    // perform the actual insertion.
    //
    // Strategy: rather than continuing to guess at native field offsets,
    // dump readable/candidate state (stock.levelupparam, stock.skillparam -
    // both SByte[] fields already confirmed accessible via the same interop
    // pattern as stock.skill/param) plus a bounded raw hex dump of both
    // GBWK's own struct and pCurrentStock's struct, every time
    // rstUpdateSeqDefaultSkill runs with a new (unit, level, Flag, seq)
    // combination. Once a case's final inserted skill ID is known (from
    // ForgetFlowRuntimeTrace / DestroyConfirmImmediateTrace's own logs),
    // these snapshots can be searched (grep) for that value to find which
    // field actually carried it - empirical correlation instead of blind
    // static guessing.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class PendingNewSkillOriginTrace
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why.
        internal static readonly bool Enabled = false;

        private static string _lastKey = string.Empty;

        private static void Prefix()
        {
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return;
                var stock = gbwk.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                int unit = stock.id;
                int level = stock.level;
                sbyte flag = gbwk.Flag;
                int seq = gbwk.SeqInfo.Current;
                int seqLast = gbwk.SeqInfo.Last;
                sbyte defResult = gbwk.DefSkillResult;

                string key = $"{unit}/{level}/{flag}/{seq}/{seqLast}/{defResult}";
                if (string.Equals(key, _lastKey, StringComparison.Ordinal)) return;
                _lastKey = key;

                int frame = UnityEngine.Time.frameCount;

                var levelupparam = stock.levelupparam;
                var skillparam = stock.skillparam;
                var param = stock.param;

                string levelupparamStr = "null";
                if (levelupparam != null)
                {
                    var sbLevelup = new StringBuilder();
                    int n = Math.Min(levelupparam.Length, 64);
                    for (int i = 0; i < n; i++)
                    {
                        if (i > 0) sbLevelup.Append(',');
                        sbLevelup.Append(levelupparam[i]);
                    }
                    levelupparamStr = sbLevelup.ToString();
                }

                string skillparamStr = "null";
                if (skillparam != null)
                {
                    var sbSkillparam = new StringBuilder();
                    int n = Math.Min(skillparam.Length, 64);
                    for (int i = 0; i < n; i++)
                    {
                        if (i > 0) sbSkillparam.Append(',');
                        sbSkillparam.Append(skillparam[i]);
                    }
                    skillparamStr = sbSkillparam.ToString();
                }

                string paramStr = "null";
                if (param != null)
                {
                    var sbParam = new StringBuilder();
                    int n = Math.Min(param.Length, 64);
                    for (int i = 0; i < n; i++)
                    {
                        if (i > 0) sbParam.Append(',');
                        sbParam.Append(param[i]);
                    }
                    paramStr = sbParam.ToString();
                }

                MelonLogger.Msg(
                    "[NocturneModernGameplay] PENDING-SNAPSHOT-SMALL; " +
                    $"frame={frame}; unit={unit}; level={level}; flag={flag}; " +
                    $"seq={seq}; seqLast={seqLast}; defSkillResult={defResult}; " +
                    $"levelupparam=[{levelupparamStr}]; skillparam=[{skillparamStr}]; param=[{paramStr}].");

                string gbwkHex = DumpHex(gbwk.Pointer, 0x200);
                string stockHex = DumpHex(stock.Pointer, 0x200);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] PENDING-SNAPSHOT-RAW-GBWK; " +
                    $"frame={frame}; unit={unit}; level={level}; hex={gbwkHex}.");
                MelonLogger.Msg(
                    "[NocturneModernGameplay] PENDING-SNAPSHOT-RAW-STOCK; " +
                    $"frame={frame}; unit={unit}; level={level}; hex={stockHex}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] PendingNewSkillOriginTrace prefix failed safely: {ex.Message}");
            }
        }

        private static string DumpHex(IntPtr baseAddress, int length)
        {
            var sb = new StringBuilder(length * 2);
            for (int i = 0; i < length; i++)
            {
                sb.Append(Marshal.ReadByte(baseAddress, i).ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
