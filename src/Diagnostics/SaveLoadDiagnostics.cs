using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Read-only investigation diagnostic - NOT an official Gameplay
    // feature: no GUI, no settings.json entry, not registered with
    // GameplayFeatureRegistry/GameplaySettingsService.
    //
    // ===== Runtime validation history =====
    //
    // Round 1 (gslMode==5 edge on slLoadProc_closeFile only): deployed,
    // tested across two real Load-menu Loads. REJECTED - zero
    // V3-SAVE-LOAD/LoadComplete lines despite confirmed real Loads.
    //
    // Round 2 (unconditional trace on 5 candidates: slLoadData,
    // slLoadProc_openFile/readFile/closeFile, slHeadLoadProc_closeFile):
    // deployed, tested across one real Load-menu Load.
    //
    //   CONFIRMED runtime observed path: a single ordinary Load produced
    //     exactly one Postfix firing each, in order, for
    //     slLoadData (frame 1974) -> slLoadProc_openFile (frame 1978)
    //     -> slLoadProc_readFile (frame 1983) -> slLoadProc_closeFile
    //     (frame 1988). slHeadLoadProc_closeFile fired multiple times
    //     BEFORE this sequence - a separate code path, not part of this
    //     Load's own body, and not adopted as a hook candidate.
    //   CONFIRMED runtime observed path: slMain.gslMode was 1 (not 5) at
    //     every one of those four firings.
    //   REJECTED for this observed path: gslMode==5 as a LoadComplete
    //     condition (Round 1's hypothesis) - the real path never showed 5
    //     at all; the earlier absence of any log was because gslMode was
    //     simply never 5 here, not because the Postfix itself failed to
    //     fire. The managed-wrapper-bypass concern from Round 2's own
    //     header comment is therefore also not needed to explain Round 1's
    //     result - Harmony postfixes on this method group DO fire on a
    //     real Load.
    //   NOT generalized: "slLoadProc_closeFile fires exactly once per
    //     Load" is this session's single observed path, not yet a
    //     cross-validated rule (see Round 3 below).
    //
    // ===== Round 3 =====
    //
    // slLoadProc_closeFile's unconditional Postfix firing (no gslMode
    // condition - Round 1's condition is dropped entirely, not just
    // loosened) was promoted to the sole active reload-boundary diagnostic
    // CANDIDATE ("type=LoadCompleteCandidate"), pending a real
    // two-Loads-in-one-session test (expect two lines) and a Save-only
    // test (expect zero lines).
    //
    // The four other Round-2 trace hooks (slLoadData, slLoadProc_openFile,
    // slLoadProc_readFile, slHeadLoadProc_closeFile) were removed for
    // normal operation once they had served their Step-1/Step-3 purpose
    // (confirming the managed Postfix boundary does fire on a real Load,
    // and identifying slHeadLoadProc_closeFile as a separate, not
    // adopted, code path). Their VAs/observations remain recorded above
    // and in investigations/REPEAT_UNLIMITED/PLAN.md rather than re-run
    // every session.
    //
    // ===== Round 4 (final validation, current) =====
    //
    // Actual sequence tested in one real play session: title-screen Load
    // (x1) -> field-return, then Load again (x2, three Loads total) ->
    // Save-only (x1, no further Load). Observed:
    //   V3-SAVE-LOAD; type=LoadCompleteCandidate lines at frame=1332,
    //     frame=2097, frame=3203 - exactly 3 lines for exactly 3 real
    //     Load-menu Loads.
    //   Save-only produced zero additional lines.
    //
    //   CONFIRMED runtime observed path: 3 ordinary Load-menu Loads in one
    //     session produced exactly 3 slLoadProc_closeFile Postfix-derived
    //     markers (1:1, including a Load performed after already being
    //     back in the field, not just from the title screen).
    //   CONFIRMED runtime observed path: 1 Save-only operation in the same
    //     session produced 0 additional markers (no false positive from
    //     Save).
    //   REJECTED: this investigation's own prior "expected 2, got 3 ->
    //     FAIL" judgment from an earlier test round - the discrepancy was
    //     test-procedure confusion (3 real Loads were actually performed,
    //     not 2), not a diagnostic defect.
    //   NOT generalized: "1 Load == 1 marker" is confirmed only for the
    //     Load-menu path exercised across this session's 3 observed Loads
    //     (one from the title screen, two from in-field) plus the earlier
    //     Round-2 single-Load trace. Other Load entry points (Continue/
    //     resume-from-suspend, which this investigation's static analysis
    //     traced to a separate slContinueYesNoProc/slLoadData-direct-call
    //     path per the local KeptSuspense reference mod - see git history
    //     of this file - rather than through slLoadProc_closeFile) are NOT
    //     covered by this marker and have not been tested.
    //
    // Tested validation scope for the "type=LoadComplete" promotion below:
    //   Load-menu Load: 3/3 matched (title-screen Load + 2 in-field Loads,
    //     one session).
    //   Save-only false positive: 0/1 (no marker from a pure Save).
    //   NOT tested: Continue/resume-from-suspend, New Game, multiple Loads
    //     across separate game sessions/process restarts, back-to-back
    //     Loads with no gameplay in between.
    //
    // Never writes any native memory (no Marshal.Write* call anywhere in
    // this class) and never writes SeqInfo.Current/TargetIndex/TargetCnt/
    // pCurrentStock/WorkStock/gslSelect/slChooseSaveIndex/any save data.
    // gslMode is still read (informational only, logged alongside the
    // candidate line) via the same raw class-info-slot -> +0xb8
    // static-fields-block chain already established for State_182e31630
    // elsewhere in this codebase (SeqSkillPowerUpBit6ClearDiagnostics.
    // TryReadState1C) - no new kind of pointer chain introduced.
    [HarmonyPatch(typeof(slMain), nameof(slMain.slLoadProc_closeFile))]
    internal static class SaveLoadDiagnostics
    {
        internal static readonly bool Enabled = true;

        // Same class-info-slot address slLoadProc1st and slLoadProc_closeFile
        // both resolve at their own entry (ISIL: both
        // "Move rax/rcx, [0x182E4DC78]") - a stable, shared resolved-
        // static-fields cache slot for slMain, not a one-off per-callsite
        // value.
        private const long SlMainClassInfoSlotVa = 0x182E4DC78L;
        private const int GslModeFieldOffset = 0x174; // slMain.gslMode

        private static int? TryReadGslMode()
        {
            try
            {
                IntPtr moduleBase = NativeChancePatchUtility.ResolveModuleBase();
                IntPtr slotAddr = NativeChancePatchUtility.ResolveVa(moduleBase, SlMainClassInfoSlotVa);
                IntPtr classInfo = Marshal.ReadIntPtr(slotAddr);
                if (classInfo == IntPtr.Zero) return null;
                IntPtr staticFields = Marshal.ReadIntPtr(classInfo, 0xb8);
                if (staticFields == IntPtr.Zero) return null;
                return Marshal.ReadInt32(staticFields, GslModeFieldOffset);
            }
            catch
            {
                return null;
            }
        }

        // Unconditional - no gslMode gate, no dedup. Each call is exactly
        // one log line by design, matching the Round 4 real-game
        // validation (3 Load-menu Loads -> 3 lines, 1 Save-only -> 0
        // lines) this promotion to "type=LoadComplete" is based on.
        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                int frame = UnityEngine.Time.frameCount;
                int? gslMode = TryReadGslMode();
                string line = "[NocturneModernGameplay] V3-SAVE-LOAD; type=LoadComplete; " +
                    $"frame={frame}; gslMode={(gslMode.HasValue ? gslMode.Value.ToString() : "NULL")}";

                // Best-effort, optional enrichment only. Omitted entirely
                // (not printed as NULL) when unavailable, per this
                // investigation's "certainty over information" guidance.
                try
                {
                    var gbwk = rstinit.GBWK;
                    var stock = gbwk?.pCurrentStock;
                    if (stock != null && stock.Pointer != IntPtr.Zero)
                        line += $"; currentStockId={stock.id}";
                    if (gbwk != null)
                        line += $"; seq={gbwk.SeqInfo.Current}";
                }
                catch
                {
                    // Enrichment only; must not suppress the core line.
                }

                MelonLogger.Msg(line + ".");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] SaveLoadDiagnostics postfix failed safely: {ex.Message}");
            }
        }
    }
}
