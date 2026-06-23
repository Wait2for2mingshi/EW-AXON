using EW_Assistant.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace EW_Assistant.Services
{
    public static class UiLanguageService
    {
        public const string Chinese = "zh-CN";
        public const string English = "en-US";

        private static readonly DependencyProperty StaticTextAppliedProperty =
            DependencyProperty.RegisterAttached(
                "StaticTextApplied",
                typeof(bool),
                typeof(UiLanguageService),
                new PropertyMetadata(false));

        private static readonly Dictionary<string, string> EnglishText = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["当前模块"] = "Current Module",
            ["AI运行信息"] = "AI Activity",
            ["复制选中"] = "Copy Selected",
            ["复制全部"] = "Copy All",
            ["清空"] = "Clear",

            ["总览"] = "Dashboard",
            ["AI助手"] = "AI Assistant",
            ["AI文档"] = "AI Docs",
            ["产能看板"] = "Production Board",
            ["报警看板"] = "Alarm Board",
            ["异常诊断"] = "Exception Diagnosis",
            ["性能监控"] = "Performance Monitor",
            ["报表中心"] = "Reports",
            ["预警中心"] = "Warning Center",
            ["机台控制"] = "Machine Control",
            ["库存管理"] = "Inventory",
            ["设置"] = "Settings",

            ["程序设置"] = "Program Settings",
            ["打开日志文件夹"] = "Open Logs",
            ["重新读取"] = "Reload",
            ["保存"] = "Save",
            ["提示"] = "Notice",
            ["错误"] = "Error",
            ["警告"] = "Warning",
            ["设置保护："] = "Settings Lock:",
            ["锁定"] = "Lock",
            ["解锁密码："] = "Password:",
            ["解锁编辑"] = "Unlock",
            ["密码保护："] = "Password Lock:",
            ["启用设置页密码保护"] = "Enable settings password lock",
            ["新密码："] = "New Password:",
            ["确认密码："] = "Confirm:",
            ["界面语言："] = "UI Language:",
            ["切换为英文版"] = "Switch to English",
            ["切换为中文版"] = "Switch to Chinese",
            ["产能 CSV 路径："] = "Production CSV Path:",
            ["报警 CSV 路径："] = "Alarm CSV Path:",
            ["IO 映射 Excel："] = "IO Map Excel:",
            ["MCP Server 地址："] = "MCP Server:",
            ["机台命令网关："] = "Machine Gateway:",
            ["本地 HTTP 监听："] = "Local HTTP:",
            ["API URL："] = "API URL:",
            ["Auto API Key："] = "Auto API Key:",
            ["Chat API Key："] = "Chat API Key:",
            ["Document API Key："] = "Document API Key:",
            ["智能体 Dify Key："] = "Agent Dify Key:",
            ["分析类 Dify Key："] = "Analysis Dify Key:",
            ["预警 Key："] = "Warning Key:",
            ["性能监控 Key："] = "Performance Key:",
            ["性能监控开关："] = "Performance Monitor:",
            ["清除报警影子模式："] = "Alarm Clear Shadow Mode:",
            ["磁盘报警阈值(%)："] = "Disk Threshold (%):",
            ["报警文件层级模式："] = "Alarm Folder Mode:",
            ["OK/NG 分表模式："] = "OK/NG Split Tables:",
            ["机台编码："] = "Machine Code:",
            ["User："] = "User:",
            ["顶部标题栏："] = "Title Bar:",
            ["智能体控制模块："] = "Agent Control Module:",
            ["AUTO Windows 推送："] = "AUTO Windows Toast:",
            ["状态记录路径："] = "State Log Path:",

            ["启用后，修改配置前需要先输入密码解锁；密码仅保存哈希值，不保存明文。"] = "When enabled, settings must be unlocked before editing. Only the password hash is stored.",
            ["关闭后保存会移除编辑保护；已启用时，留空新密码表示保持当前密码不变。"] = "Saving while disabled removes edit protection. Leave the new password blank to keep the current password.",
            ["当前已启用密码保护，设置页已解锁。"] = "Password protection is enabled. Settings are unlocked.",
            ["当前已启用密码保护，设置页已锁定。"] = "Password protection is enabled. Settings are locked.",
            ["当前未启用密码保护，本次保存后生效。"] = "Password protection is off. It will take effect after saving.",
            ["当前未启用密码保护，可直接编辑配置。"] = "Password protection is off. Settings can be edited directly.",
            ["当前界面语言：中文。点击按钮切换为英文版，保存并重启程序后生效。"] = "Current UI language: Chinese. Click the button to switch to English, then save and restart.",
            ["当前界面语言：英文。点击按钮切换回中文版，保存并重启程序后生效。"] = "Current UI language: English. Click the button to switch back to Chinese, then save and restart.",
            ["只切换界面展示文案；业务字段、日志、Dify 输入输出和内部中文路由不做强制翻译。"] = "Only display labels are switched. Business fields, logs, Dify I/O, and internal Chinese routes are not forced into translation.",
            ["机台控制与 MCP 机台命令统一使用该地址，例如 http://127.0.0.1:8081。"] = "Machine Control and MCP machine commands use this address, for example http://127.0.0.1:8081.",
            ["WorkHttpServer 监听地址，建议保留尾部 /，保存后重启程序生效。"] = "WorkHttpServer listener address. Keep the trailing /. Restart after saving.",
            ["视觉相关报警命中时使用 AutoVision Key；未命中或未标记 TRUE 时使用 Auto Key。"] = "Vision-related alarms use AutoVision Key. Unmatched alarms or alarms not marked TRUE use Auto Key.",
            ["视觉图片路径"] = "Vision Image Path",
            ["A工位视觉图片根目录"] = "A Station Vision Image Root",
            ["A路视觉图片路径"] = "Vision Image Path A",
            ["B路视觉图片路径"] = "Vision Image Path B",
            ["测试模式"] = "Test Mode",
            ["AUTO触发前取图秒数"] = "AUTO Trigger Lookback Seconds",
            ["冷却秒数"] = "Cooldown Seconds",
            ["测试模式会跳过时间校验，直接使用路径中的测试图片；正式模式只选择收到 AUTO 触发信号时刻前指定秒数内生成的最新图片，并按冷却秒数限制视觉 AUTO 重复触发。"] = "Test mode skips time validation and uses the test image from the path. Normal mode chooses the newest image generated within the configured seconds before the AUTO trigger signal is received and uses the cooldown to limit repeated Vision AUTO triggers.",
            ["本地路由分别使用 Brain Key 和 Executor Key。"] = "Local routing uses Brain Key and Executor Key separately.",
            ["MachineState Key 保留为兼容配置项；当前没有独立页面使用。"] = "MachineState Key is retained for config compatibility. No standalone page currently uses it.",
            ["开启后仅记录 ClearMachineAlarms 调用，不向机台下发真实清除报警命令。"] = "When enabled, ClearMachineAlarms is logged only and no real alarm-clear command is sent.",
            ["关闭后会隐藏左侧“智能体控制”入口，并停止本地 UI 自动化与粗识别图像联调入口。"] = "When disabled, the Agent Control entry is hidden and local UI automation/runtime entry points are stopped.",
            ["AUTO 触发后通过 Windows 通知提醒，点击可直接打开应用并切到异常诊断页面。"] = "After AUTO triggers, Windows toast opens the app directly on Exception Diagnosis.",
            ["报表生成时按日期读取 yyyy-MM-dd-状态记录表.csv，并纳入运行/空闲/LINEDOWN 分析。"] = "Reports read yyyy-MM-dd-状态记录表.csv by date and include running/idle/LINEDOWN analysis.",
            ["当前设置页已锁定，请先输入密码解锁后再保存。"] = "Settings are locked. Enter the password to unlock before saving.",
            ["内容不能为空。"] = "Required fields cannot be empty.",
            ["机台命令网关地址格式无效，请输入类似 http://127.0.0.1:8081 的地址。"] = "Invalid machine gateway address. Enter an address like http://127.0.0.1:8081.",
            ["本地 HTTP 监听地址格式无效，请输入类似 http://127.0.0.1:8091/ 的地址。"] = "Invalid local HTTP listener address. Enter an address like http://127.0.0.1:8091/.",
            ["磁盘报警阈值需在 1-100 之间。"] = "Disk alert threshold must be between 1 and 100.",
            ["视觉图片 AUTO 触发前取图秒数需在 1-600 之间。"] = "Vision image AUTO trigger lookback must be between 1 and 600 seconds.",
            ["视觉 AUTO 冷却秒数需在 0-3600 之间。"] = "Vision AUTO cooldown must be between 0 and 3600 seconds.",
            ["保存失败：\n"] = "Save failed:\n",
            ["已从磁盘重新读取配置："] = "Reloaded config from disk: ",
            ["重新读取失败："] = "Reload failed: ",
            ["打开日志目录失败："] = "Failed to open log folder: ",
            ["设置页密码不正确，无法解锁。"] = "Settings password is incorrect. Cannot unlock.",
            ["设置页已解锁，可编辑配置。"] = "Settings are unlocked and can be edited.",
            ["设置页已锁定。"] = "Settings are locked.",
            ["当前设置页已锁定，请先输入密码解锁后再切换界面语言。"] = "Settings are locked. Unlock before switching UI language.",
            ["界面语言已选择，保存配置并重启程序后生效。"] = "UI language selected. Save config and restart to apply.",
            ["配置对象无效。"] = "Config object is invalid.",
            ["设置页密码至少需要 "] = "Settings password must be at least ",
            [" 位。"] = " characters.",
            ["两次输入的设置页密码不一致。"] = "The two settings passwords do not match.",
            ["已启用设置页密码保护，请先输入新密码并确认。"] = "Settings password protection is enabled. Enter and confirm a new password first.",
            ["界面语言修改请重启程序后生效；"] = "Restart the app for UI language changes to take effect; ",
            ["配置已保存："] = "Config saved: ",
            ["。"] = ".",
            ["IP/端口相关修改请重启程序后生效。"] = "Restart the app for IP/port changes to take effect.",

            ["最小化"] = "Minimize",
            ["最大化/还原"] = "Maximize/Restore",
            ["关闭"] = "Close",
            ["AI 助手"] = "AI Assistant",
            ["生成中"] = "Generating",
            ["进度"] = "Progress",
            ["正在执行："] = "Running:",
            ["当日产能报表"] = "Daily Production Report",
            ["产能周报"] = "Weekly Production Report",
            ["当日报警报表"] = "Daily Alarm Report",
            ["报警周报"] = "Weekly Alarm Report",
            ["低良率扫描"] = "Low Yield Scan",
            ["移除"] = "Remove",
            ["上传图片"] = "Upload Image",
            ["发送"] = "Send",
            ["停止"] = "Stop",
            ["开始"] = "Start",
            ["超时"] = "Timeout",
            ["选择图片"] = "Choose Image",
            ["图片文件"] = "Image Files",
            ["图片不可用"] = "Image Unavailable",
            ["图片预览"] = "Image Preview",
            ["图片文件无法打开："] = "Unable to open image file: ",
            ["调用失败："] = "Call failed: ",
            ["对话超时，已在 {0} 分钟后自动停止。"] = "Chat timed out and was stopped automatically after {0} minutes.",
            ["未找到流式处理器，无法发送当前请求。"] = "Streaming handler was not found. The request cannot be sent.",
            ["当前回复尚未完成，或没有可导出的 Markdown 内容。"] = "The current reply is not complete, or there is no Markdown content to export.",
            ["Markdown 文件"] = "Markdown File",
            ["已导出当前回复。"] = "Current reply exported.",
            ["导出成功"] = "Export Successful",
            ["图片：{0}"] = "Image: {0}",
            ["图片：{0}（{1}）"] = "Image: {0} ({1})",
            ["AI生成报表中，请稍候"] = "Generating AI report, please wait",
            ["AI生成报警报表中，请稍候"] = "Generating AI alarm report, please wait",
            ["AI产能周报提示注入完成"] = "AI production weekly report prompt prepared",
            ["AI报警周报提示注入完成"] = "AI alarm weekly report prompt prepared",
            ["扫描低良率窗口…"] = "Scanning low-yield windows...",

            ["当前文档"] = "Current Document",
            ["选择文件"] = "Choose File",
            ["生成思维导图"] = "Generate Mind Map",
            ["生成 Checklist"] = "Generate Checklist",
            ["导出"] = "Export",
            ["展开/折叠"] = "Expand/Collapse",
            ["拖拽或选择文档，然后点击想执行的模式"] = "Drop or choose a document, then choose a mode",
            ["尚未加载文档"] = "No document loaded",
            ["未选择文件"] = "No file selected",
            ["未找到可处理的文件，请重新选择"] = "No supported file found. Choose another file.",
            ["所有文件"] = "All Files",
            ["文件不可用，请重新选择"] = "The file is unavailable. Choose another file.",
            ["已选择 {0}，请点击上方按钮生成"] = "{0} selected. Click a button above to generate.",
            ["当前模式暂无可导出的内容"] = "There is nothing to export in the current mode",
            ["正在处理其他任务，请稍候"] = "Another task is running. Please wait.",
            ["请先选择文件"] = "Choose a file first",
            ["已加载缓存的思维导图（{0}）"] = "Loaded cached mind map ({0})",
            ["正在解析：{0}"] = "Parsing: {0}",
            ["解析完成（{0}）"] = "Parsing completed ({0})",
            ["解析失败："] = "Parsing failed: ",
            ["已加载缓存的 Checklist（{0}）"] = "Loaded cached Checklist ({0})",
            ["正在生成 Checklist：{0}"] = "Generating Checklist: {0}",
            ["Checklist 生成完成（{0}）"] = "Checklist generated ({0})",
            ["Checklist 生成失败："] = "Checklist generation failed: ",
            ["暂无可导出的思维导图"] = "No mind map available to export",
            ["请切换到思维导图模式后再导出"] = "Switch to Mind Map mode before exporting",
            ["OPML 文件"] = "OPML File",
            ["思维导图 OPML 导出成功"] = "Mind map OPML exported",
            ["思维导图导出失败："] = "Mind map export failed: ",
            ["暂无 Checklist 可导出"] = "No Checklist available to export",
            ["Excel 文件"] = "Excel File",
            ["分组序号"] = "Group No.",
            ["分组标题"] = "Group Title",
            ["步骤序号"] = "Step No.",
            ["步骤标题"] = "Step Title",
            ["步骤内容"] = "Step Detail",
            ["状态编码"] = "Status Code",
            ["状态文本"] = "Status Text",
            ["Checklist Excel 导出成功"] = "Checklist Excel exported",
            ["尚未生成思维导图"] = "No mind map generated",
            ["节点 {0} 个 · 最大层级 {1} · 展开中"] = "{0} nodes - max level {1} - expanded",
            ["拖入 .docx / .pdf 文档即可在此查看思维导图"] = "Drop a .docx / .pdf document here to view the mind map",
            ["选择文件后，请点击上方按钮生成 Mindmap 或 Checklist"] = "Choose a file, then use the buttons above to generate a Mindmap or Checklist",
            ["全部展开"] = "Expand All",
            ["全部折叠"] = "Collapse All",
            ["{0} 步"] = "{0} steps",
            ["状态"] = "Status",
            ["备注"] = "Notes",
            ["暂未生成 Checklist"] = "No Checklist Generated",
            ["请选择文件并点击“生成 Checklist”"] = "Choose a file and click Generate Checklist",

            ["◀ 前一天"] = "< Previous Day",
            ["今天"] = "Today",
            ["后一天 ▶"] = "Next Day >",
            ["刷新"] = "Refresh",
            ["打开报警CSV"] = "Open Alarm CSV",
            ["当日报警条数"] = "Daily Alarm Count",
            ["单位：条"] = "Unit: count",
            ["当日报警总时长"] = "Daily Alarm Duration",
            ["单位：秒 / 分钟自动换算"] = "Unit: seconds / minutes auto converted",
            ["平均单次时长"] = "Average Duration",
            ["单位：秒"] = "Unit: seconds",
            ["最近 7 天报警次数"] = "Alarm Count in Last 7 Days",
            ["报警时长占比"] = "Alarm Duration Share",
            ["24 小时报警次数"] = "24-Hour Alarm Count",
            ["报警类别 TOP"] = "Top Alarm Categories",
            ["最长连续报警窗口（当天）"] = "Longest Continuous Alarm Window (Day)",
            ["时间段："] = "Time Range:",
            ["持续时长："] = "Duration:",
            ["合计报警时长："] = "Total Alarm Duration:",
            ["明细 · 未选择"] = "Details - Not Selected",
            ["时间"] = "Time",
            ["代码"] = "Code",
            ["类别"] = "Category",
            ["时长"] = "Duration",
            ["内容"] = "Content",
            ["在上面的『报警类别 TOP』中点击一个类别条，查看该类别的原始记录"] = "Click a category bar in Top Alarm Categories above to view its raw records",

            ["最近一周产能趋势"] = "Production Trend in Last Week",
            ["统计：PASS+FAIL"] = "Stats: PASS+FAIL",
            ["今日良率"] = "Today's Yield",
            ["今日报警类型 TOP5"] = "Today's Top 5 Alarm Types",
            ["今日 24 小时产能分布"] = "Today's 24-Hour Production",
            ["今日 24 小时报警次数"] = "Today's 24-Hour Alarm Count",
            ["统计：按小时计数"] = "Stats: hourly count",
            ["最近一周报警次数"] = "Alarm Count in Last Week",
            ["统计：按天计数"] = "Stats: daily count",
            ["今日报警时长占比"] = "Today's Alarm Duration Share",
            ["报警路径未配置，无法打开目录。"] = "Alarm path is not configured. Cannot open folder.",
            ["最近 7 天报警次数（截至 {0:MM-dd}）"] = "Alarm Count in Last 7 Days (as of {0:MM-dd})",
            ["报警时长占比（{0:MM-dd}）"] = "Alarm Duration Share ({0:MM-dd})",
            ["24 小时报警次数（{0:MM-dd}）"] = "24-Hour Alarm Count ({0:MM-dd})",
            ["报警类别 TOP（{0:MM-dd}）"] = "Top Alarm Categories ({0:MM-dd})",
            ["当前："] = "Current: ",
            ["无报警数据"] = "No alarm data",
            ["暂无时长数据"] = "No duration data",
            ["总报警时长："] = "Total alarm duration: ",
            ["明细 · {0}（{1:yyyy-MM-dd}）"] = "Details - {0} ({1:yyyy-MM-dd})",
            ["{0} 次 · {1}"] = "{0} times - {1}",

            ["打开 CSV 文件夹"] = "Open CSV Folder",
            ["当日产出（件）"] = "Daily Output (pcs)",
            ["良率 "] = "Yield ",
            ["良品 PASS"] = "Good PASS",
            ["累计良品（件）"] = "Cumulative Good (pcs)",
            ["不良 FAIL"] = "Defect FAIL",
            ["累计不良（件）"] = "Cumulative Defect (pcs)",
            ["峰值/低谷小时"] = "Peak/Low Hours",
            ["最近 7 天产能（PASS + FAIL）"] = "Production in Last 7 Days (PASS + FAIL)",
            ["高产小时 TOP"] = "Top High-Output Hours",
            ["良率"] = "Yield",
            ["当日 24 小时产能"] = "Daily 24-Hour Production",
            ["产能路径未配置，无法打开目录。"] = "Production path is not configured. Cannot open folder.",
            ["产能看板刷新失败："] = "Production board refresh failed: ",
            ["{0:yyyy-MM-dd} · 24 小时产出"] = "{0:yyyy-MM-dd} - 24-Hour Output",
            ["vs 昨日"] = "vs yesterday",
            ["暂无昨日对比数据"] = "No comparison data for yesterday",
            ["{0} 件"] = "{0} pcs",
            ["Config：未填写产能路径，无法读取 CSV"] = "Config: production path is empty. CSV cannot be read.",
            ["{0:yyyy-MM-dd} 缺少 OK/NG 分表，未能读取当日产能。"] = "{0:yyyy-MM-dd} is missing OK/NG split tables, so daily production could not be read.",
            ["未找到 {0:yyyy-MM-dd} · {1}"] = "Not found: {0:yyyy-MM-dd} - {1}",
            ["OK 表"] = "OK table",
            ["NG 表"] = "NG table",
            ["{0:yyyy-MM-dd} 缺少 {1}，统计为缺失部分按 0 处理。"] = "{0:yyyy-MM-dd} is missing {1}. Missing parts are counted as 0.",
            ["缺少数据"] = "Missing data",
            ["统计：PASS(蓝)+FAIL(红)"] = "Stats: PASS (blue) + FAIL (red)",
            ["今日暂无报警"] = "No alarms today",
            ["自动刷新失败："] = "Auto refresh failed: ",

            ["待处理"] = "Pending",
            ["已处理"] = "Processed",
            ["已关闭"] = "Closed",
            ["标记已处理"] = "Mark Processed",
            ["时间："] = "Time:",
            ["指标："] = "Metric:",
            ["状态："] = "Status:",
            ["AI 分析"] = "AI Analysis",
            ["严重"] = "Critical",
            ["预警"] = "Warning",
            ["已确认"] = "Acknowledged",
            ["已忽略"] = "Ignored",
            ["报警"] = "Alarm",
            ["产能"] = "Production",
            ["综合"] = "Combined",
            ["指标"] = "Metric",
            ["上次更新："] = "Last updated: ",
            ["当前还未接入 AI 分析，下面仅展示预警基本信息。"] = "AI analysis is not connected yet. Basic warning information is shown below.",
            ["最近 24 小时没有触发任何预警。"] = "No warnings were triggered in the last 24 hours.",
            ["请选择一条预警"] = "Select a warning",
            ["已检测到预警，但 AI 分析尚未生成，请稍后。"] = "A warning was detected, but AI analysis is not ready yet. Please wait.",
            ["{0} 当前 {1}，阈值 {2}"] = "{0} current {1}, threshold {2}",
            ["{0} 当前 {1}"] = "{0} current {1}",
            ["{0} 指标：{1}"] = "{0} metric: {1}",

            ["启动"] = "Start",
            ["暂停"] = "Pause",
            ["复位"] = "Reset",
            ["清除报警"] = "Clear Alarms",
            ["一键标定"] = "One-Click Calibration",
            ["一键点检"] = "One-Click Inspection",
            ["是否继续？"] = "Continue?",
            ["已取消："] = "Cancelled: ",
            ["执行："] = "Executing: ",
            ["成功"] = "Success",
            ["失败"] = "Failed",
            ["成功: "] = " succeeded: ",
            ["失败, HTTP 状态码: "] = " failed, HTTP status: ",
            ["返回: "] = " response: ",
            ["请求异常: "] = " request exception: ",
            ["启动机台确认"] = "Start Machine Confirmation",
            ["将执行【启动】操作，请确认现场已具备安全条件（防护门/急停/人员离开危险区）。"] = "This will start the machine. Confirm safety conditions are ready: guards closed, emergency stop released, and personnel outside hazardous areas.",
            ["设备启动"] = "Machine Start",
            ["暂停机台确认"] = "Pause Machine Confirmation",
            ["将执行【暂停】操作，设备可能进入保持/等待状态。"] = "This will pause the machine. The equipment may enter hold/wait state.",
            ["设备暂停"] = "Machine Pause",
            ["复位确认"] = "Reset Confirmation",
            ["将执行【复位】操作，轴/气缸等机构可能移动至初始位。请确认无人员在危险区域。"] = "This will reset the machine. Axes/cylinders may move to home position. Confirm no personnel are in hazardous areas.",
            ["设备复位"] = "Machine Reset",
            ["清除报警确认"] = "Clear Alarm Confirmation",
            ["将执行【清除报警】。若根因未排除，设备可能再次报警。是否继续？"] = "This will clear machine alarms. If the root cause remains, alarms may recur. Continue?",
            ["机台报警消除"] = "Machine Alarm Clear",
            ["一键视觉标定确认"] = "One-Click Vision Calibration Confirmation",
            ["将执行【视觉一键标定】。\n注意：机构/光源/镜头可能自动运动与切换，请确认治具与安全防护到位。"] = "This will run one-click vision calibration.\nNote: mechanisms, lights, and lenses may move or switch automatically. Confirm fixtures and safety protection are ready.",
            ["视觉标定"] = "Vision Calibration",
            ["一键点检确认"] = "One-Click Inspection Confirmation",
            ["将执行【一键点检】。\n可能依次触发 IO/气压/真空/相机连通/轴原点等检查，部分检查会短暂动作，请确认现场安全。"] = "This will run one-click inspection.\nIt may check IO, air pressure, vacuum, camera connectivity, axis home positions, and may trigger brief actions. Confirm site safety.",

            ["节点记录"] = "Node Records",
            ["MCP记录"] = "MCP Records",
            ["最近记录"] = "Recent Records",
            ["暂无视觉图片"] = "No Vision Image",
            ["已归类"] = "Classified",
            ["待填写"] = "Pending Entry",
            ["CSV记录详情"] = "CSV Record Details",
            ["现象"] = "Symptom",
            ["人工对策"] = "Manual Countermeasure",
            ["报警内容"] = "Alarm Content",
            ["触发时间"] = "Trigger Time",
            ["视觉图片"] = "Vision Image",
            ["AI回答"] = "AI Response",
            ["MCP调用"] = "MCP Calls",
            ["备选列表"] = "Candidate List",
            ["加入备选"] = "Add Candidate",
            ["更新选中"] = "Update Selected",
            ["删除选中"] = "Delete Selected",
            ["还没有备选项。可以先在左侧填写现象与人工对策，再加入备选。"] = "No candidates yet. Fill in Symptom and Manual Countermeasure on the left, then add one.",
            ["现象: "] = "Symptom: ",
            ["对策: "] = "Countermeasure: ",
            ["现象："] = "Symptom: ",
            ["对策："] = "Countermeasure: ",
            ["正在加载备选项..."] = "Loading candidates...",
            ["来源文件"] = "Source File",
            ["流程尚未开始。"] = "Workflow has not started.",
            ["当前还没有记录到 MCP 调用。"] = "No MCP calls recorded yet.",
            ["暂无最终回答。"] = "No final response yet.",
            ["等待本地 WorkHttpServer 受理。"] = "Waiting for local WorkHttpServer acceptance.",
            ["请求已受理，等待进入第一个 workflow 节点。"] = "Request accepted; waiting for the first workflow node.",
            ["本次请求未被执行。"] = "This request was not executed.",
            ["workflow 调度失败。"] = "Workflow dispatch failed.",
            ["正在等待新的节点执行记录。"] = "Waiting for new node execution records.",
            ["本次流程没有触发任何 MCP 调用。"] = "This workflow did not trigger any MCP calls.",
            ["MCP 已执行动作"] = "MCP action executed",
            ["未填写报警内容"] = "No alarm content",
            ["点击查看后补录"] = "Open to fill in",
            ["已归类。"] = "Classified.",
            ["无"] = "None",
            ["未命名节点"] = "Unnamed Node",
            ["等待受理"] = "Waiting",
            ["已受理"] = "Accepted",
            ["分析中"] = "Analyzing",
            ["已完成"] = "Completed",
            ["完成"] = "Done",
            ["执行失败"] = "Failed",
            ["未受理"] = "Rejected",
            ["启动失败"] = "Start Failed",
            ["未知状态"] = "Unknown Status",
            ["开始节点"] = "Start Node",
            ["AI 判断节点"] = "AI Decision Node",
            ["Agent 节点"] = "Agent Node",
            ["条件判断节点"] = "Condition Node",
            ["知识库检索节点"] = "Knowledge Retrieval Node",
            ["HTTP 请求节点"] = "HTTP Request Node",
            ["代码执行节点"] = "Code Execution Node",
            ["工具执行节点"] = "Tool Execution Node",
            ["输出节点"] = "Output Node",
            ["结束节点"] = "End Node",
            ["请先填写现象和人工对策。"] = "Fill in Symptom and Manual Countermeasure first.",
            ["请先确认当前记录带有报警内容，并填写现象和人工对策，再加入备选列表。"] = "Make sure the current record has alarm content, then fill in Symptom and Manual Countermeasure before adding it to the candidate list.",
            ["加入备选失败，请稍后重试。"] = "Failed to add candidate. Try again later.",
            ["更新备选项失败，请稍后重试。"] = "Failed to update candidate. Try again later.",
            ["确定删除当前备选项吗？删除后不会影响已保存到 CSV 的历史记录。"] = "Delete the selected candidate? Saved CSV history will not be affected.",
            ["删除备选项失败，请稍后重试。"] = "Failed to delete candidate. Try again later.",
            ["经验项"] = "Experience Item",
            ["刷新异常："] = "Refresh exception: ",
            ["打开记录失败：未找到对应 CSV 记录。"] = "Failed to open record: no matching CSV record was found.",
            ["保存失败：未找到对应 CSV 行。"] = "Save failed: no matching CSV row was found.",
            ["已保存到本地 CSV。"] = "Saved to local CSV.",
            ["导入信息"] = "Import Information",
            ["条件分支"] = "Condition Branch",
            ["知识库检索"] = "Knowledge Retrieval",
            ["整理知识库"] = "Prepare Knowledge Base",
            ["判断是否读取IO"] = "Check Whether to Read IO",
            ["获取IO实时状态"] = "Get Real-time IO Status",
            ["规划下一步动作"] = "Plan Next Action",
            ["提取结果2"] = "Extract Result 2",
            ["整理分析结果"] = "Prepare Analysis Result",
            ["结果分支"] = "Result Branch",
            ["输出"] = "Output",
            ["修复执行"] = "Repair Execution",
            ["提取结果1 (1)"] = "Extract Result 1 (1)",
            ["输出报告"] = "Output Report",
            ["决策解析"] = "Decision Parsing",
            ["提取结果1 (2)"] = "Extract Result 1 (2)",
            ["AI生成回复"] = "Generate AI Reply",
            ["提取结果1 (3)"] = "Extract Result 1 (3)",
            ["结束 4 (1)"] = "End 4 (1)",
            ["异常结束"] = "Abnormal End",
            ["提取结果1"] = "Extract Result 1",
            ["代码执行 4"] = "Code Execution 4",
            ["判断情况"] = "Assess Situation",
            ["清报警"] = "Clear Alarm",
            ["提取结果1 (4)"] = "Extract Result 1 (4)",
            ["建议结论输出"] = "Suggestion and Conclusion Output",
            ["⚡ AUTO触发：machineCode={0}。捕获到机台报警：报警代码为：{1}，进入AI分析流程。"] = "AUTO triggered: machineCode={0}. Machine alarm captured: alarm code {1}. AI analysis has started.",
            ["捕获到机台报警，进入AI分析流程。"] = "Machine alarm captured. AI analysis has started.",
            ["捕获到机台报警：报警代码为：{0}，进入AI分析流程。"] = "Machine alarm captured: alarm code {0}. AI analysis has started.",
            ["待命中"] = "Waiting",
            ["等待报警触发"] = "Waiting for alarm trigger",
            ["0.0 秒"] = "0.0 sec",
            ["{0} 次"] = "{0} times",
            ["{0} 秒"] = "{0} sec",
            ["等待分配"] = "Pending Assignment",
            ["就绪"] = "Ready",
            ["流程启动中"] = "Starting Flow",
            ["本地 WorkHttpServer 已受理请求。"] = "Local WorkHttpServer accepted the request.",
            ["本地已受理"] = "Accepted locally",
            ["本地服务 accepted，等待 workflow_started。"] = "Local service accepted; waiting for workflow_started.",
            ["workflow 未启动。"] = "Workflow did not start.",
            ["请求未受理"] = "Request Rejected",
            ["workflow 未返回有效结果。"] = "Workflow did not return a valid result.",
            ["AI分析中"] = "AI Analyzing",
            ["开始分析"] = "Analysis Started",
            ["请求已受理并完成 workflow 调度。"] = "Request accepted and workflow dispatch completed.",
            ["workflow_started，正在分析报警上下文。"] = "workflow_started; analyzing alarm context.",
            ["workflow 已启动"] = "Workflow started",
            ["动作执行中"] = "Executing Action",
            ["正在准备执行："] = "Preparing to execute: ",
            ["结果整理中"] = "Preparing Result",
            ["正在汇总最终回答。"] = "Summarizing final answer.",
            ["当前节点："] = "Current node: ",
            ["进入 "] = "Entering ",
            ["动作阶段"] = "Action stage",
            ["分析阶段"] = "Analysis stage",
            ["（{0:0.##} 秒）"] = " ({0:0.##} sec)",
            ["工具节点已返回："] = "Tool node returned: ",
            ["正在整理最终回答。"] = "Preparing final answer.",
            ["最近完成："] = "Recently completed: ",
            ["完成 "] = "Completed ",
            ["节点已完成"] = "Node completed",
            ["结果已返回"] = "Result Returned",
            ["AI 分析阶段已结束。"] = "AI analysis stage finished.",
            ["分析阶段异常结束。"] = "Analysis stage ended abnormally.",
            ["已生成最终回答。"] = "Final answer generated.",
            ["未生成有效回答。"] = "No valid answer generated.",
            ["流程失败"] = "Flow Failed",
            ["请查看 AI 最终回答。"] = "Check the final AI answer.",
            ["已触发 ClearMachineAlarms。"] = "ClearMachineAlarms triggered.",
            ["本次未触发任何 MCP 动作。"] = "No MCP action was triggered this time.",
            ["已执行 1 个 MCP 动作。"] = "Executed 1 MCP action.",
            ["已执行 {0} 个 MCP 动作。"] = "Executed {0} MCP actions.",
            [" 最近："] = " Latest: ",
            ["触发"] = "Trigger",
            ["受理"] = "Accepted",
            ["分析"] = "Analysis",
            ["执行"] = "Execution",
            ["报警处理中"] = "Processing Alarm",
            ["已捕获报警并生成 trace。"] = "Alarm captured and trace generated.",
            ["等待本地服务受理。"] = "Waiting for local service acceptance.",
            ["尚未进入 Dify workflow。"] = "Not yet entered Dify workflow.",
            ["等待 AI 判断是否需要执行动作。"] = "Waiting for AI to decide whether an action is needed.",
            ["等待最终回答。"] = "Waiting for final answer.",
            ["报警触发"] = "Alarm Triggered",
            ["AUTO 事件"] = "AUTO Event",
            ["当前已有 AUTO 任务执行中，本次请求已丢弃。"] = "An AUTO task is already running. This request was dropped.",
            ["Auto URL / AutoKey 尚未配置，本次请求未被受理。"] = "Auto URL / AutoKey is not configured. This request was not accepted.",
            ["Auto URL / AutoVisionKey 尚未配置，本次视觉相关 AUTO 请求未被受理。"] = "Auto URL / AutoVisionKey is not configured. This vision-related AUTO request was not accepted.",
            ["当前已有 AUTO 任务执行中，或 Auto URL / AutoKey / AutoVisionKey 尚未配置。"] = "An AUTO task is already running, or Auto URL / AutoKey / AutoVisionKey is not configured.",
            ["请求未被系统受理。"] = "Request was not accepted by the system.",
            ["MCP 已触发机台清报警"] = "MCP triggered machine alarm clear",
            ["MCP 已执行 IO 动作"] = "MCP executed IO action",
            ["MCP 已读取 IO 状态"] = "MCP read IO status",
            ["MCP 已执行设备复位"] = "MCP reset the machine",
            ["MCP 已执行设备启动"] = "MCP started the machine",
            ["MCP 已执行设备暂停"] = "MCP paused the machine",
            ["MCP 已执行视觉标定"] = "MCP executed vision calibration",
            ["MCP 已执行一键点检"] = "MCP executed quick inspection",
            ["MCP 已调用 "] = "MCP called ",
            ["失败："] = "Failed: ",
            ["已下发机台清报警动作。"] = "Machine alarm clear action has been sent.",
            ["MCP 动作执行结束。"] = "MCP action finished.",
            ["未实现视图：{0}"] = "View not implemented: {0}",
            ["已清空"] = "Cleared",
            ["已复制 {0} 条信息"] = "Copied {0} messages",
            ["请先选择要复制的信息"] = "Select messages to copy first",
            ["无可复制内容"] = "Nothing to copy",
            ["复制失败"] = "Copy failed",
            ["正在初始化报表..."] = "Initializing reports...",
            ["报表初始化已停止，请查看前面的具体原因。"] = "Report initialization stopped. Check the earlier details.",
            ["报表初始化完成。"] = "Report initialization completed.",
            ["报表初始化已取消。"] = "Report initialization canceled.",

            ["规则事件"] = "Rule Events",
            ["AI 分析历史"] = "AI Analysis History",
            ["测试报警"] = "Test Alert",
            ["内存使用"] = "Memory Usage",
            ["历史记录"] = "History",
            ["级别"] = "Level",
            ["摘要"] = "Summary",
            ["磁盘容量"] = "Disk Capacity",
            ["盘符"] = "Drive",
            ["总量(GB)"] = "Total (GB)",
            ["剩余(GB)"] = "Free (GB)",
            ["总内存 {0} MB"] = "Total memory {0} MB",
            ["已使用 {0} MB"] = "Used {0} MB",
            ["可用 {0} MB"] = "Available {0} MB",
            ["暂无 AI 分析结果"] = "No AI analysis yet",
            ["性能 AI 分析超时，已在 2 分钟后中止。"] = "Performance AI analysis timed out and was stopped after 2 minutes.",
            ["调用 AI 分析出错："] = "AI analysis failed: ",
            ["进程"] = "Process",
            ["CPU"] = "CPU",
            ["内存"] = "Memory",
            ["磁盘"] = "Disk",
            ["使用率"] = "Usage",

            ["报表类型"] = "Report Type",
            ["报表列表"] = "Report List",
            ["打开所在文件夹"] = "Open Folder",
            ["重新生成"] = "Regenerate",
            ["导出报表"] = "Export Report",
            ["图文报表包"] = "Rich Report Package",
            ["HTML 文件"] = "HTML File",
            ["PDF 文件"] = "PDF File",
            ["纯文本文件"] = "Plain Text File",
            ["摘要预览"] = "Summary Preview",
            ["当前页仅展示简版信息，完整图文报表请双击左侧列表项打开。"] = "This page shows a short preview only. Double-click an item on the left for the full report.",
            ["双击查看完整内容"] = "Double-click to View Full Content",
            ["双击摘要预览区域打开完整图文详情"] = "Double-click the summary preview area to open full report details",
            ["概览图表"] = "Overview Chart",
            ["使用主图快速查看当前报表的核心趋势。"] = "Use the main chart to quickly inspect key trends.",
            ["摘要速览"] = "Summary Highlights",
            ["提炼 AI 分析正文中的核心结论，完整版本请在详情窗查看。"] = "Key conclusions extracted from the AI analysis. Open details for the full version.",
            ["未选择报表"] = "No Report Selected",
            ["双击当前摘要可查看完整图文内容、AI 正文和明细表。"] = "Double-click the summary to view full content, AI analysis, and detail tables.",
            ["报表详情"] = "Report Details",
            ["报表预览"] = "Report Preview",
            ["请选择左侧报表后预览"] = "Select a report on the left to preview",
            ["明细数据"] = "Detail Data",
            ["重生成超时或已取消。"] = "Regeneration timed out or was canceled.",
            ["重生成报表失败："] = "Report regeneration failed: ",
            ["正在加载报表列表，请稍候..."] = "Loading report list...",
            ["扫描报表目录时出错："] = "Error while scanning report folder: ",
            ["当前类型暂无报表文件。"] = "No report files for the current type.",
            ["请选择左侧报表以查看图文预览。"] = "Select a report on the left to view the rich preview.",
            ["正在加载报表预览，请稍候..."] = "Loading report preview...",
            ["读取报表失败："] = "Failed to read report: ",
            ["当前报表暂无 AI 分析正文。"] = "No AI analysis text for this report.",
            ["暂无摘要内容。"] = "No summary content.",
            ["报表类型："] = "Report type: ",
            ["未选择"] = "Not selected",
            ["更新时间："] = "Updated: ",
            ["未选择报表。"] = "No report selected.",
            ["未指定导出路径。"] = "No export path specified.",
            ["当前报表暂无摘要内容，可双击左侧报表查看完整图文详情。"] = "No summary is available for this report. Double-click the report on the left to view full details.",
            ["请选择有效的报表文件。"] = "Select a valid report file.",
            ["报表文件不存在，可能已被移动或删除。"] = "The report file does not exist. It may have been moved or deleted.",
            ["打开资源管理器失败："] = "Failed to open Explorer: ",
            ["请选择要导出的报表。"] = "Select a report to export.",
            ["报表已导出："] = "Report exported: ",
            ["导出失败："] = "Export failed: ",
            ["开始重新生成报表："] = "Regenerating report: ",
            ["报表重新生成完成："] = "Report regenerated: ",
            ["重新生成失败："] = "Regeneration failed: ",
            ["打开报表详情失败："] = "Failed to open report details: ",
            ["当前报表元数据缺失，无法导出。"] = "Current report metadata is missing. Cannot export.",
            ["单击查看详情"] = "Click to view details",
            ["未找到报表元数据"] = "Report metadata not found",
            ["文件："] = "File: ",
            ["未解析日期"] = "Date not parsed",
            ["日期：{0}"] = "Date: {0}",
            ["更新：{0:yyyy-MM-dd HH:mm}"] = "Updated: {0:yyyy-MM-dd HH:mm}",
            ["大小：{0}"] = "Size: {0}",
            ["文件：{0}"] = "File: {0}",
            ["摘要更新时间：{0:yyyy-MM-dd HH:mm}"] = "Summary updated: {0:yyyy-MM-dd HH:mm}",
            ["关键指标"] = "Key Metrics",
            ["AI 分析正文"] = "AI Analysis Text",
            ["注意事项"] = "Notes",
            ["图表详情"] = "Chart Details",
            ["摘要说明"] = "Summary",
            ["关键数据"] = "Key Data",
            ["明细信息"] = "Details",
            ["暂无图表"] = "No Chart",
            ["暂无数据"] = "No Data",
            ["暂不支持该图表类型"] = "This chart type is not supported",
            ["暂无图表数据"] = "No Chart Data",

            ["新增"] = "Add",
            ["备件列表"] = "Spare Parts",
            ["名称"] = "Name",
            ["单位"] = "Unit",
            ["库位"] = "Location",
            ["规格"] = "Spec",
            ["规格/型号"] = "Spec/Model",
            ["库存"] = "Stock",
            ["安全库存"] = "Safety Stock",
            ["历史进出库记录"] = "Stock History",
            ["备件"] = "Part",
            ["类型"] = "Type",
            ["其他"] = "Other",
            ["变更"] = "Change",
            ["变更后"] = "After",
            ["原因"] = "Reason",
            ["备件信息"] = "Part Info",
            ["计量单位"] = "Unit",
            ["当前库存"] = "Current Stock",
            ["编辑"] = "Edit",
            ["删除"] = "Delete",
            ["入库"] = "Inbound",
            ["出库"] = "Outbound",
            ["调整库存"] = "Adjust Stock",
            ["调整"] = "Adjustment",
            ["编辑备件"] = "Edit Part",
            ["新增备件"] = "Add Part",
            ["数量"] = "Quantity",
            ["调整后库存"] = "Adjusted Stock",
            ["取消"] = "Cancel",
            ["确定"] = "OK",
            ["名称不能为空。"] = "Name is required.",
            ["库存操作"] = "Stock Operation",
            ["变更数量"] = "Change Quantity",
            ["请输入有效数量。"] = "Enter a valid quantity.",
            ["请输入有效的调整后库存。"] = "Enter a valid adjusted stock quantity.",
            ["采购入库"] = "Purchase Inbound",
            ["退料入库"] = "Return Inbound",
            ["维修领料"] = "Maintenance Issue",
            ["生产领料"] = "Production Issue",
            ["报废出库"] = "Scrap Outbound",
            ["库存调整"] = "Stock Adjustment",
            ["刷新库存失败："] = "Failed to refresh inventory: ",
            ["新增备件成功。"] = "Part added successfully.",
            ["新增失败："] = "Add failed: ",
            ["更新成功。"] = "Updated successfully.",
            ["更新失败："] = "Update failed: ",
            ["确定删除该备件吗？"] = "Delete this part?",
            ["确认删除"] = "Confirm Delete",
            ["删除成功。"] = "Deleted successfully.",
            ["删除失败："] = "Delete failed: ",
            ["数量必须大于 0。"] = "Quantity must be greater than 0.",
            ["入库失败："] = "Inbound failed: ",
            ["出库失败："] = "Outbound failed: ",
            ["调整库存失败："] = "Failed to adjust stock: ",
            ["套"] = "set",
            ["箱"] = "box",
            ["卷"] = "roll",
            ["袋"] = "bag",
            ["米"] = "m",
            ["公斤"] = "kg",

            ["已显示 {0} 条预警"] = "Showing {0} warnings",
            [" 共 {0} 条"] = " Total {0}",
            ["共 {0} 条"] = "Total {0}"
        };

        private static readonly Dictionary<string, string> EnglishAlarmTriggerText = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V01_A未测取料真空1报警"] = "V01_A Untested pickup vacuum 1 alarm",
            ["C01_A未测分料气缸原位报警"] = "C01_A Untested material-separation cylinder home-position alarm",
            ["C05_A未测顶升气缸2动位报警"] = "C05_A Untested jacking cylinder 2 actuated-position alarm"
        };

        private static readonly KeyValuePair<string, string>[] EnglishAlarmTriggerPhrases =
        {
            new KeyValuePair<string, string>("未测", " untested "),
            new KeyValuePair<string, string>("取料", " pickup "),
            new KeyValuePair<string, string>("分料", " material-separation "),
            new KeyValuePair<string, string>("顶升", " jacking "),
            new KeyValuePair<string, string>("真空", " vacuum "),
            new KeyValuePair<string, string>("气缸", " cylinder "),
            new KeyValuePair<string, string>("原位", " home-position "),
            new KeyValuePair<string, string>("动位", " actuated-position "),
            new KeyValuePair<string, string>("报警", " alarm")
        };

        public static string Normalize(string language)
        {
            var raw = (language ?? string.Empty).Trim();
            if (raw.Equals(English, StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("english", StringComparison.OrdinalIgnoreCase))
            {
                return English;
            }

            return Chinese;
        }

        public static bool IsEnglish(string language)
        {
            return string.Equals(Normalize(language), English, StringComparison.Ordinal);
        }

        public static bool IsEnglish(AppConfig cfg)
        {
            return IsEnglish(cfg?.UiLanguage);
        }

        public static string Text(string chineseText, AppConfig cfg)
        {
            if (!IsEnglish(cfg))
                return chineseText ?? string.Empty;

            return TranslateToEnglish(chineseText);
        }

        public static string CurrentText(string chineseText)
        {
            return Text(chineseText, ConfigService.Current);
        }

        public static string ApplyDisplayTextReplacements(string text, AppConfig cfg = null)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            if (!IsEnglish(cfg ?? ConfigService.Current))
                return text;

            var exact = TranslateToEnglish(text);
            if (!string.Equals(exact, text, StringComparison.Ordinal))
                return exact;

            var operational = TranslateOperationalDisplayText(text);
            return TranslateAlarmTriggerText(operational);
        }

        private static string TranslateOperationalDisplayText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? string.Empty;

            const string workServerStarted = "WorkHttpServer 已启动：";
            if (text.StartsWith(workServerStarted, StringComparison.Ordinal))
                return "WorkHttpServer started: " + text.Substring(workServerStarted.Length);

            const string workServerError = "WorkHttpServer 异常：";
            if (text.StartsWith(workServerError, StringComparison.Ordinal))
                return "WorkHttpServer error: " + text.Substring(workServerError.Length);

            const string listenFailed = "监听失败：";
            const string urlAclHint = "（可能需要 urlacl）";
            if (text.StartsWith(listenFailed, StringComparison.Ordinal))
            {
                var detail = text.Substring(listenFailed.Length).Replace(urlAclHint, " (urlacl may be required)");
                return "Listener failed: " + detail;
            }

            const string mcpAlreadyRunning = "MCP Server 已在运行，无需重复启动。";
            if (string.Equals(text, mcpAlreadyRunning, StringComparison.Ordinal))
                return "MCP Server is already running. No restart is needed.";

            const string mcpStartedPrefix = "MCP Server 已启动（PID=";
            if (text.StartsWith(mcpStartedPrefix, StringComparison.Ordinal) && text.EndsWith("）。", StringComparison.Ordinal))
            {
                var pid = text.Substring(mcpStartedPrefix.Length, text.Length - mcpStartedPrefix.Length - "）。".Length);
                return "MCP Server started (PID=" + pid + ").";
            }

            const string mcpExitedPrefix = "MCP Server 进程启动后立即退出（ExitCode=";
            if (text.StartsWith(mcpExitedPrefix, StringComparison.Ordinal) && text.EndsWith("）。", StringComparison.Ordinal))
            {
                var exitCode = text.Substring(mcpExitedPrefix.Length, text.Length - mcpExitedPrefix.Length - "）。".Length);
                return "MCP Server exited immediately after startup (ExitCode=" + exitCode + ").";
            }

            const string mcpStartFailedEmpty = "启动 MCP Server 失败（返回空进程）。";
            if (string.Equals(text, mcpStartFailedEmpty, StringComparison.Ordinal))
                return "Failed to start MCP Server (empty process returned).";

            const string mcpStartFailedPrefix = "启动 MCP Server 失败：";
            if (text.StartsWith(mcpStartFailedPrefix, StringComparison.Ordinal))
                return "Failed to start MCP Server: " + text.Substring(mcpStartFailedPrefix.Length);

            const string mcpNotFoundPrefix = "未找到 MCP Server 可执行文件：";
            if (text.StartsWith(mcpNotFoundPrefix, StringComparison.Ordinal))
                return "MCP Server executable not found: " + text.Substring(mcpNotFoundPrefix.Length);

            const string knowledgeLoadedPrefix = "报警知识库已加载，共 ";
            if (text.StartsWith(knowledgeLoadedPrefix, StringComparison.Ordinal) && text.EndsWith(" 条。", StringComparison.Ordinal))
            {
                var count = text.Substring(knowledgeLoadedPrefix.Length, text.Length - knowledgeLoadedPrefix.Length - " 条。".Length);
                return "Alarm knowledge base loaded, " + count + " entries.";
            }

            const string knowledgeMissingPath = "IoMapCsvPath 未配置，未加载报警知识库。";
            if (string.Equals(text, knowledgeMissingPath, StringComparison.Ordinal))
                return "IoMapCsvPath is not configured. Alarm knowledge base was not loaded.";

            const string knowledgeFileMissingPrefix = "未找到报警知识库文件：";
            if (text.StartsWith(knowledgeFileMissingPrefix, StringComparison.Ordinal))
                return "Alarm knowledge base file not found: " + text.Substring(knowledgeFileMissingPrefix.Length);

            const string knowledgeReadFailedPrefix = "读取报警知识库IO失败：";
            if (text.StartsWith(knowledgeReadFailedPrefix, StringComparison.Ordinal))
                return "Failed to read alarm knowledge base IO: " + text.Substring(knowledgeReadFailedPrefix.Length);

            const string reportInitFailedPrefix = "报表初始化失败：";
            if (text.StartsWith(reportInitFailedPrefix, StringComparison.Ordinal))
                return "Report initialization failed: " + text.Substring(reportInitFailedPrefix.Length);

            const string autoReportFailedPrefix = "自动检查报表失败：";
            if (text.StartsWith(autoReportFailedPrefix, StringComparison.Ordinal))
                return "Automatic report check failed: " + text.Substring(autoReportFailedPrefix.Length);

            return text;
        }

        public static string TranslateAlarmTriggerText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            var result = text;
            foreach (var pair in EnglishAlarmTriggerText)
            {
                result = result.Replace(pair.Key, pair.Value);
            }

            if ((ReferenceEquals(result, text) || string.Equals(result, text, StringComparison.Ordinal))
                && IsLikelyAlarmTriggerText(result))
            {
                result = ApplyAlarmPhraseFallback(result);
            }

            return NormalizeAlarmEnglishSpacing(result);
        }

        private static bool IsLikelyAlarmTriggerText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.IndexOf("报警", StringComparison.Ordinal) >= 0
                   && (text.IndexOf("未测", StringComparison.Ordinal) >= 0
                       || text.IndexOf("报警代码", StringComparison.Ordinal) >= 0
                       || text.IndexOf("ErrorCode", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string ApplyAlarmPhraseFallback(string text)
        {
            var result = text;
            foreach (var pair in EnglishAlarmTriggerPhrases)
            {
                result = result.Replace(pair.Key, pair.Value);
            }

            return result;
        }

        private static string NormalizeAlarmEnglishSpacing(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? string.Empty;

            var result = text.Trim();
            while (result.Contains("  "))
            {
                result = result.Replace("  ", " ");
            }

            result = result
                .Replace("_ ", "_")
                .Replace(" _", "_")
                .Replace("： ", ": ")
                .Replace(" 。", ".")
                .Replace("， ", ", ");

            return result;
        }

        public static string TranslateToEnglish(string chineseText)
        {
            if (chineseText == null)
                return string.Empty;

            return EnglishText.TryGetValue(chineseText, out var translated)
                ? translated
                : chineseText;
        }

        public static void ApplyStaticText(DependencyObject root, AppConfig cfg)
        {
            if (!IsEnglish(cfg) || root == null)
                return;

            if (ShouldSkipStaticText(root))
                return;

            if ((bool)root.GetValue(StaticTextAppliedProperty))
                return;

            root.SetValue(StaticTextAppliedProperty, true);
            var visited = new HashSet<DependencyObject>();
            ApplyStaticTextCore(root, visited);
        }

        private static bool ShouldSkipStaticText(DependencyObject root)
        {
            var name = root.GetType().FullName ?? string.Empty;
            return name.EndsWith(".Views.AgentControlView", StringComparison.Ordinal)
                   || name.EndsWith(".Views.UiRuntimeView", StringComparison.Ordinal);
        }

        private static void ApplyStaticTextCore(DependencyObject current, HashSet<DependencyObject> visited)
        {
            if (current == null || !visited.Add(current))
                return;

            ApplyElementText(current);

            try
            {
                foreach (var child in LogicalTreeHelper.GetChildren(current))
                {
                    if (child is DependencyObject childObject)
                        ApplyStaticTextCore(childObject, visited);
                }
            }
            catch
            {
                // 仅做显示文案适配，遍历失败时跳过该分支，避免影响页面运行。
            }
        }

        private static void ApplyElementText(DependencyObject element)
        {
            if (element is TextBlock textBlock &&
                BindingOperations.GetBindingExpression(textBlock, TextBlock.TextProperty) == null)
            {
                textBlock.Text = TranslateToEnglish(textBlock.Text);
            }

            if (element is HeaderedContentControl headeredContent &&
                headeredContent.Header is string headerText &&
                BindingOperations.GetBindingExpression(headeredContent, HeaderedContentControl.HeaderProperty) == null)
            {
                headeredContent.Header = TranslateToEnglish(headerText);
            }

            if (element is HeaderedItemsControl headeredItems &&
                headeredItems.Header is string itemsHeaderText &&
                BindingOperations.GetBindingExpression(headeredItems, HeaderedItemsControl.HeaderProperty) == null)
            {
                headeredItems.Header = TranslateToEnglish(itemsHeaderText);
            }

            if (element is ContentControl contentControl &&
                contentControl.Content is string contentText &&
                BindingOperations.GetBindingExpression(contentControl, ContentControl.ContentProperty) == null)
            {
                contentControl.Content = TranslateToEnglish(contentText);
            }

            if (element is FrameworkElement frameworkElement &&
                frameworkElement.ToolTip is string toolTipText &&
                BindingOperations.GetBindingExpression(frameworkElement, FrameworkElement.ToolTipProperty) == null)
            {
                frameworkElement.ToolTip = TranslateToEnglish(toolTipText);
            }

            if (element is Window window &&
                BindingOperations.GetBindingExpression(window, Window.TitleProperty) == null)
            {
                window.Title = TranslateToEnglish(window.Title);
            }

            if (element is DataGrid dataGrid)
            {
                foreach (var column in dataGrid.Columns)
                {
                    if (column?.Header is string columnHeader)
                        column.Header = TranslateToEnglish(columnHeader);
                }
            }

            if (element is Run run)
            {
                run.Text = TranslateToEnglish(run.Text);
            }
        }
    }

    public sealed class LocalizedFormatConverter : IValueConverter
    {
        private const string ParameterSeparator = "||";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var parameterText = parameter as string ?? string.Empty;
            var fallbackText = string.Empty;
            var separatorIndex = parameterText.IndexOf(ParameterSeparator, StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                fallbackText = parameterText.Substring(separatorIndex + ParameterSeparator.Length);
                parameterText = parameterText.Substring(0, separatorIndex);
            }

            var hasValue = value != null
                           && value != DependencyProperty.UnsetValue
                           && value != Binding.DoNothing
                           && !string.IsNullOrWhiteSpace(value.ToString());

            if (!hasValue)
            {
                if (!string.IsNullOrWhiteSpace(fallbackText))
                {
                    if (parameterText.IndexOf("{0", StringComparison.Ordinal) >= 0)
                    {
                        try
                        {
                            return string.Format(
                                culture ?? CultureInfo.CurrentCulture,
                                UiLanguageService.Text(parameterText, ConfigService.Current),
                                UiLanguageService.Text(fallbackText, ConfigService.Current));
                        }
                        catch
                        {
                            return UiLanguageService.Text(fallbackText, ConfigService.Current);
                        }
                    }

                    return UiLanguageService.Text(fallbackText, ConfigService.Current);
                }

                if (parameterText.IndexOf("{0", StringComparison.Ordinal) < 0)
                {
                    return UiLanguageService.Text(parameterText, ConfigService.Current);
                }
            }

            if (parameterText.IndexOf("{0", StringComparison.Ordinal) < 0)
            {
                return hasValue ? value.ToString() : UiLanguageService.Text(parameterText, ConfigService.Current);
            }

            var format = UiLanguageService.Text(parameterText, ConfigService.Current);
            try
            {
                return string.Format(culture ?? CultureInfo.CurrentCulture, format, hasValue ? value : string.Empty);
            }
            catch
            {
                return hasValue ? value.ToString() : UiLanguageService.Text(fallbackText, ConfigService.Current);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
