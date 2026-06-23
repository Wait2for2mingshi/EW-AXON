using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EW_Assistant.Services
{
    internal sealed class ExecutorRegistryService
    {
        private readonly AgentWorkspaceService _workspaceService = new AgentWorkspaceService();

        public ExecutorDescriptor Resolve(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;

            return LoadAll()
                .FirstOrDefault(x => x.Enabled && string.Equals(x.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private IReadOnlyList<ExecutorDescriptor> LoadAll()
        {
            var snapshot = _workspaceService.LoadSnapshot(DateTime.Now);
            var root = AgentControlValueHelper.ParseJsonLike(snapshot.AvailableExecutorsJson, new JObject());
            var executors = new List<ExecutorDescriptor>();

            IEnumerable<JToken> items = root switch
            {
                JObject jo when jo["executors"] is JArray arr => arr,
                JArray arr => arr,
                _ => Enumerable.Empty<JToken>()
            };

            foreach (var item in items.OfType<JObject>())
            {
                var key = item.Value<string>("key")?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                executors.Add(new ExecutorDescriptor
                {
                    Key = key,
                    DisplayName = item.Value<string>("display_name")?.Trim() ?? string.Empty,
                    ApiKey = item.Value<string>("api_key")?.Trim() ?? string.Empty,
                    DefaultCommandCatalogMode = item.Value<string>("default_command_catalog_mode")?.Trim() ?? "grounded_light_compact",
                    Enabled = item["enabled"] == null || item.Value<bool?>("enabled") != false
                });
            }

            return executors;
        }

        public static IReadOnlyCollection<string> ParseExecutorKeys(string availableExecutorsJson)
        {
            var root = AgentControlValueHelper.ParseJsonLike(availableExecutorsJson, new JObject());
            IEnumerable<JToken> items = root switch
            {
                JObject jo when jo["executors"] is JArray arr => arr,
                JArray arr => arr,
                _ => Enumerable.Empty<JToken>()
            };

            return items
                .OfType<JObject>()
                .Select(x => x.Value<string>("key")?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
