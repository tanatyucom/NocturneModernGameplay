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
        }

        public override void OnDeinitializeMelon()
        {
            PowerUpMutationBit6RawProbe.Uninstall();
            SkillCntWriterCaptureProbe.Uninstall();
            EventParamWriterCaptureProbe.Uninstall();
            GameplayFeatureRegistry.Shutdown();
        }
    }
}
