using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // OLD QUEUE LOOP SUPPRESSION ARCHAEOLOGY - lifecycle boundary check
    // (read-only, no clearing, no suppression). Never writes any field.
    //
    // Purpose: the legacy Queue-era implementation (legacy/skillmutation/
    // SkillMutationLearnAsNew.LegacyFinal.cs, ResultLifecycleStartClearPatch,
    // line 1898) used rstinit.rstCreateTargetList's Prefix as the signal
    // that a brand-new result lifecycle (a fresh batch of level-up/result
    // processing) is starting, and cleared HandledSlots there. Before
    // building an analogous clear boundary for the current V3
    // HandledCandidatesObserver/CoreReentryHandledCheck pair, this class
    // confirms - purely by observation - whether rstCreateTargetList fires
    // ZERO times during a single-unit sequence like the one already
    // reproduced this session (349 declined -> Power-Up -> AddNew bridge
    // (299) -> 349 re-offered ~28s later with a fresh Core roll). If it
    // fires mid-sequence, using it as an unconditional clear boundary would
    // erase HandledCandidates too early and let the exact recurrence we are
    // trying to suppress slip through again.
    [HarmonyPatch(typeof(rstinit), nameof(rstinit.rstCreateTargetList))]
    internal static class ResultLifecycleBoundaryObserver
    {
        internal static readonly bool Enabled = true;

        private static void Prefix()
        {
            if (!Enabled) return;
            try
            {
                var gbwk = rstinit.GBWK;
                int frame = UnityEngine.Time.frameCount;
                int unit = -1;
                long stockPtr = 0;
                try
                {
                    var stock = gbwk?.pCurrentStock;
                    if (stock != null && stock.Pointer != IntPtr.Zero)
                    {
                        unit = stock.id;
                        stockPtr = stock.Pointer.ToInt64();
                    }
                }
                catch { /* leave unit=-1/stockPtr=0 */ }

                int seq = gbwk?.SeqInfo.Current ?? -1;
                int handledCountBefore = HandledCandidatesObserver.DebugCount;

                var (handledCleared, consumedCleared) = HandledCandidatesObserver.ClearForNewLifecycle();

                MelonLogger.Msg(
                    "[NocturneModernGameplay] RSTCREATE-TARGETLIST-LIFECYCLE; " +
                    $"frame={frame}; unit={unit}; stockPtr=0x{stockPtr:X}; seq={seq}; " +
                    $"handledCountBefore={handledCountBefore}; " +
                    $"handledCleared={handledCleared}; consumedCleared={consumedCleared}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] ResultLifecycleBoundaryObserver prefix failed safely: {ex.Message}");
            }
        }
    }
}
