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
        internal bool Enabled { get; set; }
        internal Action<bool>? SetEnabled { get; init; }
        internal Action? Sample { get; init; }
        internal Action? Shutdown { get; init; }
    }

    internal static class GameplayFeatureRegistry
    {
        private static readonly Dictionary<string, GameplayFeature> Features =
            new(StringComparer.OrdinalIgnoreCase);

        internal static IReadOnlyList<GameplayFeature> GetFeatures() =>
            Features.Values.OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        internal static void Initialize()
        {
            Features.Clear();
            Features["skill_mutation_always"] = new GameplayFeature
            {
                Id = "skill_mutation_always",
                Name = "Skill Mutation: Always",
                Description = "変化可能なスキルがある仲魔は、レベルアップ時のスキル変化判定に必ず成功します。",
                Category = "Gameplay Change",
                SortOrder = 90,
                Enabled = true,
                SetEnabled = SkillMutationAlways.SetEnabled,
                Shutdown = SkillMutationAlways.Shutdown
            };
            Features["skill_mutation_learn_as_new"] = new GameplayFeature
            {
                Id = "skill_mutation_learn_as_new",
                Name = "Skill Mutation: Learn as New",
                Description = "スキル変化で元スキルを残し、変化後スキルを新規習得します。満杯時は忘れるスキルを選択します。",
                Category = "Gameplay Change",
                SortOrder = 100,
                Enabled = true,
                SetEnabled = SkillMutationLearnAsNew.SetEnabled,
                Sample = SkillMutationLearnAsNew.Sample
            };
        }

        internal static bool TrySetEnabled(string featureId, bool enabled)
        {
            if (!Features.TryGetValue(featureId, out GameplayFeature? feature))
            {
                return false;
            }
            feature.SetEnabled?.Invoke(enabled);
            feature.Enabled = enabled;
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
