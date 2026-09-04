using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Persisted config shape. Kept as a plain POCO separate from the
    // runtime NativeChanceMode enum so a malformed/unknown string in the
    // file can never throw during deserialization - validation happens
    // explicitly in GameplaySettingsService.Load, field by field, with a
    // fail-safe fallback to "Native" and a warning log per field.
    internal sealed class GameplaySettingsData
    {
        public SkillMutationSettingsData SkillMutation { get; set; } = new();
        public SkillPowerUpSettingsData SkillPowerUp { get; set; } = new();
    }

    internal sealed class SkillMutationSettingsData
    {
        public string Chance { get; set; } = "Native";
    }

    internal sealed class SkillPowerUpSettingsData
    {
        public string Chance { get; set; } = "Native";
        public string Repeat { get; set; } = "Native";
    }

    // Single source of truth for [SkillMutation]/[SkillPowerUp] Chance (and,
    // once implemented, Repeat). Owns the config file
    // (NocturneModernGameplay.settings.json), owns the current in-memory
    // mode values, and is the ONLY thing that calls
    // SkillMutationChanceControl.SetMode / SkillPowerUpChanceControl.SetMode
    // - both ModMain's startup (via Load) and GameplayFeatureRegistry's GUI
    // bridge (via SetSkillMutationChance/SetSkillPowerUpChance) go through
    // here, so runtime state and the saved file never diverge (see
    // NocturneModernGameplay / NocturneModernController integration spec,
    // section 2 - Single Source of Truth).
    //
    // NocturneModernGameplay.settings.json is deliberately a NEW, MOD-name-
    // qualified file - the pre-existing root settings.json
    // ({"language":"ja"}) is an unrelated, currently-unread placeholder and
    // is left untouched.
    internal static class GameplaySettingsService
    {
        internal static NativeChanceMode SkillMutationChance { get; private set; } = NativeChanceMode.Native;
        internal static NativeChanceMode SkillPowerUpChance { get; private set; } = NativeChanceMode.Native;

        // Phase A: Repeat has exactly one legal value (Native). Unlimited is
        // parsed (so a hand-edited file naming it is recognized rather than
        // treated as garbage) but never applied - there is no backend for
        // it yet, and it must never silently no-op as if it worked.
        internal static string Repeat { get; private set; } = "Native";

        private static string SettingsPath =>
            Path.Combine(ModDirectory, "NocturneModernGameplay.settings.json");

        private static string ModDirectory =>
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;

        // Loads the config file (writing a fresh default one if missing),
        // validates every field fail-safe, and APPLIES the result to
        // SkillMutationChanceControl/SkillPowerUpChanceControl immediately
        // (both must already be Initialize()'d - see ModMain.cs ordering).
        internal static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    Save();
                }
                else
                {
                    GameplaySettingsData? data = JsonSerializer.Deserialize<GameplaySettingsData>(
                        File.ReadAllText(SettingsPath));
                    if (data != null)
                    {
                        SkillMutationChance = ParseChanceMode(data.SkillMutation?.Chance, "SkillMutation.Chance");
                        SkillPowerUpChance = ParseChanceMode(data.SkillPowerUp?.Chance, "SkillPowerUp.Chance");
                        Repeat = ParseRepeat(data.SkillPowerUp?.Repeat);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    "[NocturneModernGameplay] Gameplay settings file unreadable/invalid; " +
                    $"falling back to all-Native defaults: {ex.Message}");
                SkillMutationChance = NativeChanceMode.Native;
                SkillPowerUpChance = NativeChanceMode.Native;
                Repeat = "Native";
            }

            SkillMutationChanceControl.SetMode(SkillMutationChance);
            SkillPowerUpChanceControl.SetMode(SkillPowerUpChance);
            // Repeat has no backend yet in Phase A - intentionally not
            // applied anywhere; SkillPowerUpChanceControl's Always logic
            // always behaves as Repeat=Native (see its own header comment).

            MelonLogger.Msg(
                "[NocturneModernGameplay] Gameplay settings loaded; " +
                $"SkillMutation.Chance={SkillMutationChance} SkillPowerUp.Chance={SkillPowerUpChance} " +
                $"SkillPowerUp.Repeat={Repeat}(not yet implemented, no effect).");
        }

        internal static void Save()
        {
            try
            {
                var data = new GameplaySettingsData
                {
                    SkillMutation = new SkillMutationSettingsData { Chance = SkillMutationChance.ToString() },
                    SkillPowerUp = new SkillPowerUpSettingsData
                    {
                        Chance = SkillPowerUpChance.ToString(),
                        Repeat = Repeat
                    }
                };
                Directory.CreateDirectory(ModDirectory);
                File.WriteAllText(
                    SettingsPath,
                    JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[NocturneModernGameplay] Gameplay settings save failed: {ex.Message}");
            }
        }

        // Entry points for GUI-driven changes (GameplayFeatureRegistry).
        // Both apply to the backend immediately AND persist, per the
        // integration spec's sync rule (GUI change -> Settings Service ->
        // runtime -> config).
        internal static void SetSkillMutationChance(NativeChanceMode mode)
        {
            SkillMutationChance = mode;
            SkillMutationChanceControl.SetMode(mode);
            Save();
        }

        internal static void SetSkillPowerUpChance(NativeChanceMode mode)
        {
            SkillPowerUpChance = mode;
            SkillPowerUpChanceControl.SetMode(mode);
            Save();
        }

        private static NativeChanceMode ParseChanceMode(string? raw, string fieldName)
        {
            if (Enum.TryParse(raw, ignoreCase: true, out NativeChanceMode mode))
                return mode;
            MelonLogger.Warning(
                $"[NocturneModernGameplay] config: unknown {fieldName} value '{raw}'; falling back to Native.");
            return NativeChanceMode.Native;
        }

        private static string ParseRepeat(string? raw)
        {
            if (string.Equals(raw, "Native", StringComparison.OrdinalIgnoreCase)) return "Native";
            if (string.Equals(raw, "Unlimited", StringComparison.OrdinalIgnoreCase))
            {
                MelonLogger.Warning(
                    "[NocturneModernGameplay] config: SkillPowerUp.Repeat=Unlimited is not yet implemented " +
                    "in this build; falling back to Native (it will NOT silently behave as Unlimited).");
                return "Native";
            }
            if (!string.IsNullOrEmpty(raw))
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] config: unknown SkillPowerUp.Repeat value '{raw}'; falling back to Native.");
            return "Native";
        }
    }
}
