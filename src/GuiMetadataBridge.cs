using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace NocturneModernGameplay
{
    internal sealed class FeatureMetadataSnapshot
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string Category { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool RequiresRestart { get; set; }
        public bool ReadOnly { get; set; }
        public string Version { get; set; } = string.Empty;
        public string Warning { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        // Optional multi-value surface (e.g. Chance: Disabled/Native/
        // Always). Null/empty AllowedValues means this feature stays
        // boolean-only (Enabled/SetEnabled) for full backward compatibility
        // with a GUI that does not understand Value at all.
        public string[]? AllowedValues { get; set; }
        public string? Value { get; set; }
    }

    internal sealed class ProviderMetadataSnapshot
    {
        public string ProviderId { get; set; } = "nocturne_modern_gameplay";
        public string ProviderName { get; set; } = "Nocturne Modern Gameplay";
        public string Version { get; set; } = "0.1.0";
        public List<FeatureMetadataSnapshot> Features { get; set; } = new();
        public string Error { get; set; } = string.Empty;
    }

    internal sealed class FeatureToggleRequest
    {
        public string ProviderId { get; set; } = string.Empty;
        public string FeatureId { get; set; } = string.Empty;
        public bool Enabled { get; set; }

        // When non-null/non-empty, this is a multi-value selection request
        // (takes precedence over Enabled) - see GameplayFeatureRegistry.TrySetValue.
        public string? Value { get; set; }
    }

    internal static class GuiMetadataBridge
    {
        private const string ProviderId = "nocturne_modern_gameplay";
        private static int _lastRequestWriteTick;

        internal static void WriteSnapshot()
        {
            var provider = new ProviderMetadataSnapshot
            {
                Features = GameplayFeatureRegistry.GetFeatures().Select(feature =>
                    new FeatureMetadataSnapshot
                    {
                        Id = feature.Id,
                        Name = feature.Name,
                        Description = feature.Description,
                        Enabled = feature.Enabled,
                        Category = feature.Category,
                        SortOrder = feature.SortOrder,
                        RequiresRestart = feature.RequiresRestart,
                        Version = "0.1.0",
                        AllowedValues = feature.AllowedValues,
                        Value = feature.Value
                    }).ToList()
            };
            Directory.CreateDirectory(ModDirectory);
            File.WriteAllText(
                SnapshotPath,
                JsonSerializer.Serialize(
                    new[] { provider },
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        internal static void SampleToggleRequests()
        {
            if (!File.Exists(RequestPath))
            {
                return;
            }
            int writeTick = unchecked((int)File.GetLastWriteTimeUtc(RequestPath).Ticks);
            if (writeTick == _lastRequestWriteTick)
            {
                return;
            }
            _lastRequestWriteTick = writeTick;
            try
            {
                List<FeatureToggleRequest>? requests =
                    JsonSerializer.Deserialize<List<FeatureToggleRequest>>(
                        File.ReadAllText(RequestPath));
                if (requests == null)
                {
                    return;
                }
                bool changed = false;
                foreach (FeatureToggleRequest request in requests.Where(item =>
                             string.Equals(item.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase)))
                {
                    changed |= !string.IsNullOrEmpty(request.Value)
                        ? GameplayFeatureRegistry.TrySetValue(request.FeatureId, request.Value)
                        : GameplayFeatureRegistry.TrySetEnabled(request.FeatureId, request.Enabled);
                }
                if (changed)
                {
                    WriteSnapshot();
                }
            }
            catch
            {
                // A malformed optional GUI request must never stop standalone play.
            }
        }

        private static string ModDirectory =>
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        private static string SnapshotPath =>
            Path.Combine(ModDirectory, "NocturneModernGameplay.features.json");
        private static string RequestPath =>
            Path.Combine(ModDirectory, "NocturneModernController.feature-requests.json");
    }
}
