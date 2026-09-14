using MelonLoader;

[assembly: MelonInfo(
    typeof(NocturneModernGameplay.ModMain),
    "Nocturne Modern Gameplay",
    "0.1.0",
    "Gray Ghost")]
[assembly: MelonGame(null, "smt3hd")]

namespace NocturneModernGameplay
{
    public sealed class ModMain : MelonMod
    {
        public override void OnInitializeMelon()
        {
            // SkillMutationAlways (old Patch A/B/C) is intentionally NOT
            // initialized here - it is retired in favor of
            // SkillMutationChanceControl/SkillPowerUpChanceControl. Its
            // source is kept (unmodified) but never runs.
            //
            // SharedSkillChangeOuterGateControl MUST resolve before either
            // ChanceControl - both of their Initialize() methods end with a
            // defensive ApplyMode(Native) that calls
            // SharedSkillChangeOuterGateControl.Recompute(), which would be
            // silently ignored (logged as unresolved) if the shared gate
            // had not run first.
            SharedSkillChangeOuterGateControl.Initialize();
            SkillMutationChanceControl.Initialize();
            SkillPowerUpChanceControl.Initialize();
            // Settings Service is the single source of truth: it reads
            // NocturneModernGameplay.settings.json (writing sane defaults
            // if missing) and applies the result via SetMode immediately -
            // this must run AFTER Initialize() (native sites resolved) and
            // BEFORE GameplayFeatureRegistry.Initialize() (which reads the
            // current mode for its initial GUI snapshot).
            GameplaySettingsService.Load();
            GameplayFeatureRegistry.Initialize();
            GuiMetadataBridge.WriteSnapshot();
            GetDefaultSkillCallBoundaryTrace.LogPatchStatus();
            SkillDrawListEntryTrace.LogPatchStatus();
            SkillNameCostDrawFieldTrace.LogPatchStatus();
            SkillCurObjSetActiveTrace.LogPatchStatus();
            SkillMakeStrColFieldTrace.LogPatchStatus();
            CmpMenuCursorTrace.LogPatchStatus();
            LoggerInstance.Msg(
                "[NocturneModernGameplay] Loaded standalone; " +
                "GUI metadata bridge is optional.");
        }

        public override void OnUpdate()
        {
            // Diagnostics-only (POWERUP_MUTATION_CFG investigation), default
            // OFF - see PowerUpMutationCfgDiagnostics.Enabled. Installed
            // here (not OnInitializeMelon) so the hardware breakpoint would
            // be armed on the same OS thread that runs Unity's Update loop;
            // no-ops entirely while diagnostics are disabled, so the
            // official Chance build never touches Dr0-Dr3/VEH.
            if (PowerUpMutationCfgDiagnostics.Enabled)
            {
                PowerUpMutationBit6RawProbe.EnsureInstalledOnGameplayThread();
                PowerUpMutationBit6RawProbe.FlushPendingLogs();
            }
            GuiMetadataBridge.SampleToggleRequests();
            GameplayFeatureRegistry.Sample();
            StatusUiFieldOffsetProbe.TryProbe();
            SelectSkillIdOffsetProbe.TryProbe();
            // SkillCurObjNativeCallerProbe / HighlightTargetGateTrace /
            // CursorPosShiftWriteWatchTrace / SelectSkillIdWriteWatchTrace /
            // Hidden9thSlotPathTrace / Hidden9thGateCascadeTrace /
            // FclChkMessageResultTrace / CmpDrawSkillR13LoopBoundTrace
            // .Tick()/FlushPendingLogs() are deliberately NOT called this
            // session: all of these classes (plus R13FetchAndWriteWatchTrace
            // below) install hardware breakpoints on the same DR0-DR3
            // registers (InstallHardwareBreakpoint(s)OnCurrentThread
            // unconditionally claims them), and running more than one at
            // once would have the later Install() silently steal registers
            // out from under the earlier one, breaking it without any
            // error. This investigation's history (all CONFIRMED, see
            // 01_CURRENT_STATE.md / investigations/HIDDEN_SKILL_ENTRY/
            // PLAN.md for full detail) narrowed from "does the presentation
            // function even get called" down to a single byte:
            // CmpDrawSkillR13LoopBoundTrace found the candidate-scan loop
            // bound ([r13+0x10] inside cmpDrawSkill) is 1 or 2 for High
            // Pixie's succeeding AddNew-bridge case and exactly 0 for
            // Frost's failing one (242 vs 238 hits, zero exceptions either
            // way) - structurally meaning Frost's loop (and the dedicated
            // target==8 path deeper in it) is skipped entirely.
            // R13FetchAndWriteWatchTrace (below) is the active investigation
            // now - identifying the WRITER of that count via a dynamic
            // hardware write-watch (DR1 is reprogrammed to the current
            // frame's [r13+0x10] every time cmpDrawStatusComEx2's fetch
            // point fires, since `r13` is a fresh object each call and a
            // static-address watch cannot pre-exist it), plus the
            // earliest-observable value at fetch time and the final value
            // cmpDrawSkill's own loop sees, all in one pass. Uninstall()
            // below still runs for all of them so any leftover state from a
            // prior build is cleaned up.
            R13FetchAndWriteWatchTrace.Tick();
            R13FetchAndWriteWatchTrace.FlushPendingLogs();
        }

        public override void OnDeinitializeMelon()
        {
            PowerUpMutationBit6RawProbe.Uninstall();
            SkillCurObjNativeCallerProbe.Uninstall();
            HighlightTargetGateTrace.Uninstall();
            CursorPosShiftWriteWatchTrace.Uninstall();
            SelectSkillIdWriteWatchTrace.Uninstall();
            Hidden9thSlotPathTrace.Uninstall();
            Hidden9thGateCascadeTrace.Uninstall();
            FclChkMessageResultTrace.Uninstall();
            CmpDrawSkillR13LoopBoundTrace.Uninstall();
            R13FetchAndWriteWatchTrace.Uninstall();
            SkillCntWriterCaptureProbe.Uninstall();
            EventParamWriterCaptureProbe.Uninstall();
            GameplayFeatureRegistry.Shutdown();
        }
    }
}
