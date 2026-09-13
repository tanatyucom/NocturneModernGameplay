using System;
using System.Collections.Generic;
using System.Linq;

namespace NocturneModernGameplay
{
    internal sealed class GameplayFeature
    {
        internal string Id { get; init; } = string.Empty;
        internal string Name { get; init; } = string.Empty;
        internal string Description { get; init; } = string.Empty;
        internal string Category { get; init; } = string.Empty;
        internal int SortOrder { get; init; }
        internal bool RequiresRestart { get; init; }

        // Legacy boolean surface, kept for any future plain on/off feature
        // and for backward compatibility with a caller that only knows the
        // old boolean protocol. For a multi-value feature (AllowedValues
        // non-null), Enabled is a DERIVED display flag only
        // (Value != "Disabled") - SetValue/Value are authoritative.
        internal bool Enabled { get; set; }
        internal Action<bool>? SetEnabled { get; init; }

        // Multi-value surface (e.g. Chance: Disabled/Native/Always). Null
        // AllowedValues means this feature is boolean-only.
        internal string[]? AllowedValues { get; init; }
        internal string? Value { get; set; }
        internal Action<string>? SetValue { get; init; }

        internal Action? Sample { get; init; }
        internal Action? Shutdown { get; init; }
    }

    internal static class GameplayFeatureRegistry
    {
        private static readonly string[] ChanceAllowedValues = { "Disabled", "Native", "Always" };
        private static readonly string[] RepeatAllowedValues = { "Native", "Unlimited" };

        private static readonly Dictionary<string, GameplayFeature> Features =
            new(StringComparer.OrdinalIgnoreCase);

        internal static IReadOnlyList<GameplayFeature> GetFeatures() =>
            Features.Values.OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        internal static void Initialize()
        {
            Features.Clear();

            // The old skill_mutation_always feature (SkillMutationAlways,
            // Patch A/B/C) is retired - see ModMain.cs (no longer
            // Initialize()'d) - and intentionally not re-registered here.
            // Its Patch C site (0x18227E39C) is the same VA
            // SkillMutationChanceControl's id-zero-path site patches with
            // different bytes; the two must never both be active.
            //
            // Both Chance features route through GameplaySettingsService
            // (NOT directly through SkillMutationChanceControl/
            // SkillPowerUpChanceControl.SetMode) so a GUI-driven change is
            // also persisted to config immediately - GameplaySettingsService
            // is the single source of truth (see its own header comment).
            RegisterChanceFeature(
                "skill_mutation_chance", "Skill Mutation: Chance",
                "変化可能なスキル候補が存在する場合のgenuine Skill Mutation発生確率です。" +
                "0% = 抑止、通常 = native、100% = 候補があれば必ず試行(成立の可否はnative判定のまま)。",
                GameplaySettingsService.SkillMutationChance,
                GameplaySettingsService.SetSkillMutationChance,
                SkillMutationChanceControl.Shutdown);
            RegisterChanceFeature(
                "skill_powerup_chance", "Skill Power-Up: Chance",
                "ordinary Skill Power-Upの発生確率です。0% = 抑止、通常 = native、" +
                "100% = bit6がCLEARな限りgenuine Mutationより優先して成立させます。",
                GameplaySettingsService.SkillPowerUpChance,
                GameplaySettingsService.SetSkillPowerUpChance,
                SkillPowerUpChanceControl.Shutdown);
            RegisterRepeatFeature();
        }

        // Repeat routes through GameplaySettingsService.SetRepeat (NOT
        // through any ChanceControl.SetMode) - OptionFRepeatUnlimitedControl
        // reads GameplaySettingsService.Repeat directly on every Core
        // invocation (investigations/REPEAT_UNLIMITED/PLAN.md), so there is
        // no separate runtime "mode" object to notify here, unlike Chance.
        // Uses its own small registration (not RegisterChanceFeature, which
        // is typed for the 3-value NativeChanceMode enum) since Repeat's
        // values are a plain 2-value string set with no Disabled state.
        private static void RegisterRepeatFeature()
        {
            const string id = "skill_powerup_repeat";
            Features[id] = new GameplayFeature
            {
                Id = id,
                Name = "Skill Power-Up: Repeat",
                Description = "Skill Power-Upのbit6ゲート(「このサイクルは既にPower-Up済み」)による" +
                              "不成立を、通常成功へ変換するかどうかです。通常 = native挙動のまま、" +
                              "無制限 = R0-C(bit6ゲート由来の不成立)のみ通常成功へ変換します" +
                              "(native除外〈R0-B〉・候補なし〈R0-A〉は変換しません)。",
                Category = "Gameplay Change",
                SortOrder = 91,
                AllowedValues = RepeatAllowedValues,
                Value = GameplaySettingsService.Repeat,
                Enabled = true,
                SetValue = raw => GameplaySettingsService.SetRepeat(raw)
            };
        }

        private static void RegisterChanceFeature(
            string id, string name, string description, NativeChanceMode currentMode,
            Action<NativeChanceMode> setMode, Action shutdown)
        {
            Features[id] = new GameplayFeature
            {
                Id = id,
                Name = name,
                Description = description,
                Category = "Gameplay Change",
                SortOrder = 90,
                AllowedValues = ChanceAllowedValues,
                Value = currentMode.ToString(),
                Enabled = currentMode != NativeChanceMode.Disabled,
                SetValue = raw =>
                {
                    if (!Enum.TryParse(raw, ignoreCase: true, out NativeChanceMode mode))
                        return;
                    setMode(mode);
                },
                Shutdown = shutdown
            };
        }

        internal static bool TrySetEnabled(string featureId, bool enabled)
        {
            if (!Features.TryGetValue(featureId, out GameplayFeature? feature) || feature.SetEnabled == null)
            {
                return false;
            }
            feature.SetEnabled(enabled);
            feature.Enabled = enabled;
            return true;
        }

        internal static bool TrySetValue(string featureId, string value)
        {
            if (!Features.TryGetValue(featureId, out GameplayFeature? feature) || feature.SetValue == null)
            {
                return false;
            }
            feature.SetValue(value);
            feature.Value = value;
            feature.Enabled = !string.Equals(value, "Disabled", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        internal static void Sample()
        {
            foreach (GameplayFeature feature in Features.Values)
            {
                if (feature.Enabled)
                {
                    feature.Sample?.Invoke();
                }
            }
        }

        internal static void Shutdown()
        {
            foreach (GameplayFeature feature in Features.Values)
            {
                feature.Shutdown?.Invoke();
            }
        }
    }
}
