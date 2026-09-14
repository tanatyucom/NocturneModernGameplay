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
            // Hidden9thSlotPathTrace .Tick()/FlushPendingLogs() are
            // deliberately NOT called this session: all of these classes
            // (plus Hidden9thGateCascadeTrace below) install hardware
            // breakpoints on the same DR0-DR3 registers
            // (InstallHardwareBreakpoint(s)OnCurrentThread unconditionally
            // claims them), and running more than one at once would have
            // the later Install() silently steal registers out from under
            // the earlier one, breaking it without any error. All five
            // already answered the question they were built for: caller
            // converges on cmpSetupObject VA 0x182620A80; highlight gate's
            // `target` is 8 while sitting on the hidden entry, one past
            // loopUpper=8; CursorPos.Shift=8 is written unconditionally by
            // native at VA 0x182288AC1 and conditionally by the AddNew
            // bridge path at VA 0x182289313; GBWK.SelectSkillID's native
            // writer search was abandoned as moot; and Hidden9thSlotPathTrace
            // found the decisive asymmetry - High Pixie's AddNew-bridge
            // case reaches AND completes the dedicated target==8 branch
            // (skillCurObj[8] ends up active/inHierarchy=True), while
            // Frost's AddNew-bridge case reaches the branch-entry
            // breakpoint (VA 0x1822DA48C) ZERO times despite CursorPos.
            // Shift staying 8 for its entire seq21/22 episode - see
            // 01_CURRENT_STATE.md / investigations/HIDDEN_SKILL_ENTRY/
            // PLAN.md, all CONFIRMED. Hidden9thGateCascadeTrace (below) is
            // the active investigation now - it uses all 4 hardware
            // breakpoint registers (DR0-DR3) as a ladder of real EXECUTE
            // breakpoints at each gate's own fallthrough address (an
            // earlier managed-context-replication design was tried and
            // proven unreliable - see that file's own 2026-09-14
            // correction note - one of the gates reads a highly volatile,
            // frequently-reused shared value that a same-frame
            // out-of-band read cannot trust). Uninstall() below still runs
            // for all of them so any leftover state from a prior build is
            // cleaned up.
            Hidden9thGateCascadeTrace.Tick();
            Hidden9thGateCascadeTrace.FlushPendingLogs();
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
            SkillCntWriterCaptureProbe.Uninstall();
            EventParamWriterCaptureProbe.Uninstall();
            GameplayFeatureRegistry.Shutdown();
        }
    }
}
