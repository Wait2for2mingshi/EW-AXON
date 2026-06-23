using System.IO;

namespace EW_Assistant.Services
{
    internal static class AgentControlPaths
    {
        internal static string AppConfigFilePath => Path.Combine(@"D:\", "AppConfig.json");

        internal static string AiLogRoot => Path.Combine(@"D:\", "Data", "AiLog");

        internal static string RouterLogRoot => Path.Combine(AiLogRoot, "UiAgentRouter");

        internal static string WorkspaceRoot => Path.Combine(@"D:\", "DataAI", "AgentWorkspace");

        internal static string WorkspaceMemoryRoot => Path.Combine(WorkspaceRoot, "memory");

        internal static string ExecutorLogRoot => Path.Combine(AiLogRoot, "UiCoarseVision");
    }
}
