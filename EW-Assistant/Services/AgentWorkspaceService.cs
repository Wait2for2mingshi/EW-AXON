using System;
using System.IO;
using System.Text;

namespace EW_Assistant.Services
{
    internal sealed class AgentWorkspaceService
    {
        public AgentWorkspaceSnapshot LoadSnapshot(DateTime now)
        {
            Directory.CreateDirectory(AgentControlPaths.WorkspaceRoot);
            Directory.CreateDirectory(AgentControlPaths.WorkspaceMemoryRoot);

            var personaMarkdown = EnsureFile(Path.Combine(AgentControlPaths.WorkspaceRoot, "SOUL.md"), AgentWorkspaceDefaults.DefaultPersonaMarkdown);
            var memoryMarkdown = EnsureFile(Path.Combine(AgentControlPaths.WorkspaceRoot, "MEMORY.md"), AgentWorkspaceDefaults.DefaultMemoryMarkdown);
            var dailyMemoryPath = Path.Combine(AgentControlPaths.WorkspaceMemoryRoot, now.ToString("yyyy-MM-dd") + ".md");
            var dailyMemoryMarkdown = EnsureFile(dailyMemoryPath, AgentWorkspaceDefaults.DefaultDailyMemoryMarkdown);
            var executorsJson = EnsureFile(Path.Combine(AgentControlPaths.WorkspaceRoot, "executors.json"), AgentWorkspaceDefaults.CreateDefaultExecutorsJson());

            return new AgentWorkspaceSnapshot
            {
                PersonaMarkdown = personaMarkdown,
                MemoryDigestMarkdown = memoryMarkdown,
                DailyMemoryMarkdown = dailyMemoryMarkdown,
                AvailableExecutorsJson = executorsJson,
                RootDirectory = AgentControlPaths.WorkspaceRoot
            };
        }

        private static string EnsureFile(string path, string defaultContent)
        {
            if (string.IsNullOrWhiteSpace(path))
                return defaultContent ?? string.Empty;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(path))
                File.WriteAllText(path, defaultContent ?? string.Empty, new UTF8Encoding(false));

            return File.ReadAllText(path, Encoding.UTF8);
        }
    }

    internal sealed class AgentWorkspaceSnapshot
    {
        public string PersonaMarkdown { get; set; }
        public string MemoryDigestMarkdown { get; set; }
        public string DailyMemoryMarkdown { get; set; }
        public string AvailableExecutorsJson { get; set; }
        public string RootDirectory { get; set; }
    }
}
