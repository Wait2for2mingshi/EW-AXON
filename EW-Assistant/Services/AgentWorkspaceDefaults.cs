namespace EW_Assistant.Services
{
    internal static class AgentWorkspaceDefaults
    {
        internal const string DefaultPersonaMarkdown =
@"# 角色
你是一个冷静、直接、可靠的桌面智能体主脑。

# 风格
- 回答简洁
- 不夸张
- 不假装完成
- 优先给出可执行判断

# 边界
- 不伪造观察结果
- 不把不确定说成确定
- 不输出底层工具参数
- 高风险或目标不清时优先 need_human

# 决策偏好
- 能直接回答的问题就直接回答
- 明确的桌面执行任务才交给 executor
- executor_goal 要单任务、单场景、可观察完成";

        internal const string DefaultMemoryMarkdown =
@"# 长期记忆摘要
- 用户偏好先设计清楚再逐步落地
- 当前系统采用 Brain / Executor 分层
- Brain 负责路由与任务重写
- Executor 负责 observe / act / verify
- 当前默认桌面执行器 key 为 desktop_default";

        internal const string DefaultDailyMemoryMarkdown =
@"# 当日记忆
- 当前正在构建 Brain V1
- 当前阶段先做主脑路由，不直接在 Brain 中调用工具
- 当前只有一个桌面通用执行器";

        internal static string CreateDefaultExecutorsJson()
        {
            return
@"{
  ""version"": ""v1"",
  ""default_executor_key"": ""desktop_default"",
  ""executors"": [
    {
      ""key"": ""desktop_default"",
      ""display_name"": ""桌面通用执行器"",
      ""api_key"": """",
      ""default_command_catalog_mode"": ""grounded_light_compact"",
      ""enabled"": true,
      ""capabilities"": [
        ""Windows桌面导航"",
        ""资源管理器打开与定位"",
        ""窗口切换"",
        ""基础点击"",
        ""基础输入"",
        ""基础界面验证""
      ],
      ""good_for"": [
        ""打开D盘"",
        ""打开文件夹"",
        ""切换到某个窗口"",
        ""在已知界面中点击、输入、验证""
      ],
      ""not_good_for"": [
        ""纯解释问答"",
        ""目标不明确的请求"",
        ""开放式闲聊""
      ]
    }
  ]
}";
        }
    }
}
