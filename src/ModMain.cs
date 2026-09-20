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
            SkillMakeStrColFieldTrace.LogPatchStatus();
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
            // StatusUiFieldOffsetProbe.TryProbe() and SelectSkillIdOffsetProbe
            // .TryProbe() (both HIDDEN NEW SKILL ENTRY field-offset probes,
            // closed below) were removed from here and moved to
            // legacy/skillmutationv3/*.Legacy.cs, alongside CmpMenuCursorTrace
            // and SkillCurObjSetActiveTrace. SkillCurObjSetActiveTrace (an
            // unconditional Harmony Prefix on UnityEngine.GameObject.SetActive,
            // i.e. every SetActive() call in the whole game) was confirmed by
            // real-machine A/B/A to cause a lasting Field slowdown after
            // opening SMT3HD's native Controller Key Config screen; the other
            // three were retired at the same time since this investigation was
            // already closed and none of them should still be patching/probing
            // in production.
            // HIDDEN NEW SKILL ENTRY investigation: CLOSED (2026-09-15, see
            // investigations/HIDDEN_SKILL_ENTRY/PLAN.md). Root cause
            // (Frost's ordinary, non-transformed `hensinmae==0` state means
            // native's own curriculum scan in rstcalc.rstCreateBeforeSkillList
            // structurally never finds a slot-9 presentation candidate,
            // unlike a transformed demon such as High Pixie) is fully
            // characterized, and the fix (HiddenSlotCandidateInjection.cs, a
            // Harmony Postfix that supplies one candidate to native's own
            // outList only when native found none) is CONFIRMED working
            // end-to-end on real hardware with no regression on the
            // already-working case.
            //
            // SkillCurObjNativeCallerProbe / HighlightTargetGateTrace /
            // CursorPosShiftWriteWatchTrace / SelectSkillIdWriteWatchTrace /
            // Hidden9thSlotPathTrace / Hidden9thGateCascadeTrace /
            // FclChkMessageResultTrace / CmpDrawSkillR13LoopBoundTrace /
            // R13FetchAndWriteWatchTrace / CurriculumGateChainTrace were this
            // investigation's successive hardware-breakpoint (DR0-DR3)
            // probes - each superseded the last as the root cause narrowed
            // (full chain in PLAN.md), and none run any more now that the
            // fix is confirmed. Left retired-but-present (Tick()/
            // FlushPendingLogs() calls commented out, Uninstall() still
            // called below for prior-build cleanup) rather than deleted, in
            // case regression testing or a similar future investigation
            // needs one of them again.
            // CurriculumGateChainTrace.Tick();
            // CurriculumGateChainTrace.FlushPendingLogs();
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
            CurriculumGateChainTrace.Uninstall();
            SkillCntWriterCaptureProbe.Uninstall();
            EventParamWriterCaptureProbe.Uninstall();
            EventParamActualWriterTrace.Uninstall();
            GameplayFeatureRegistry.Shutdown();
        }
    }
}
