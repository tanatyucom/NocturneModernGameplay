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
            GameplayFeatureRegistry.Initialize();
            ExperimentalInlineRepro.Initialize();
            SkillMutationAlways.Initialize();
            GuiMetadataBridge.WriteSnapshot();
            LoggerInstance.Msg(
                "[NocturneModernGameplay] Loaded standalone; " +
                "GUI metadata bridge is optional.");
        }

        public override void OnUpdate()
        {
            GuiMetadataBridge.SampleToggleRequests();
            GameplayFeatureRegistry.Sample();
        }

        public override void OnDeinitializeMelon()
        {
            GameplayFeatureRegistry.Shutdown();
        }
    }
}
