namespace EW_Assistant.Services
{
    /// <summary>
    /// 智能体控制模块状态门禁。
    /// 当前最小闭环仅使用该门禁判断模块是否冻结。
    /// </summary>
    public static class AgentAutomationService
    {
        public static bool ModuleEnabled => ConfigService.IsAgentControlModuleEnabled();
    }
}
