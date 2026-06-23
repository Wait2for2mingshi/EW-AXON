using Newtonsoft.Json.Linq;
using EW_Assistant.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EW_Assistant.Views
{
    public partial class MachineControl : UserControl
    {
        public MachineControl()
        {
            InitializeComponent();
        }

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public class CommandTarget
        {
            public string Station { get; set; }  // 如 "T66-01"
            public string Device { get; set; }  // 如 "IO1" / "CamA" / "AxisX"
            public string Axis { get; set; }  // 可选
            public string Fixture { get; set; }  // 可选
        }

        public class CommandRequest
        {
            public string Action { get; set; }                      // 动作名：如 "Machine.Start" / "IO.Switch"
            public CommandTarget Target { get; set; }               // 目标对象（可选）
            public Dictionary<string, object> Params { get; set; }  // 任意参数（可嵌套）
        }

        public class CommandResponse   // 按你的后端返回结构可调整
        {
            public string Status { get; set; }   // "ok"/"error"/"accepted"/...
            public string Message { get; set; }
            public object Data { get; set; }
        }

        // 小工具：快速构建参数字典（忽略 null）
        private static Dictionary<string, object> PD(params (string Key, object Value)[] items)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in items) if (v != null) d[k] = v;
            return d;
        }

        private static void TryPostInfo(string text, string level)
        {
            try { MainWindow.PostProgramInfo(text, level); } catch { }
        }

        private static string ResolveApiBaseUrl()
        {
            var configured = EW_Assistant.Services.ConfigService.Current?.MachineCommandBaseUrl;
            return EW_Assistant.Services.ConfigService.NormalizeMachineCommandBaseUrl(configured);
        }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        // === 通用发送 ===
        private static async Task<string> SendCommand(
            string action,
            string actionName,
            Dictionary<string, object> args = null,
            CommandTarget target = null)
        {
            try
            {
                var req = new CommandRequest
                {
                    Action = action,
                    Target = target,
                    Params = args
                };

                var json = JsonSerializer.Serialize(req, JsonOpt);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, ResolveApiBaseUrl()) { Content = content };

                var resp = await _http.SendAsync(httpReq).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    try
                    {
                        var r = JsonSerializer.Deserialize<CommandResponse>(text, JsonOpt);
                        if (r != null && string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase))
                            return actionName + L("成功: ") + (string.IsNullOrEmpty(r.Message) ? "OK" : r.Message);
                    }
                    catch { /* 非标准JSON则直接透传 */ }

                    return actionName + L("成功: ") + text;
                }
                else
                {
                    return actionName + L("失败, HTTP 状态码: ") + resp.StatusCode + ", " + L("返回: ") + text;
                }
            }
            catch (Exception ex)
            {
                return actionName + L("请求异常: ") + ex.Message;
            }
        }

        // === 新增：统一确认弹框 ===
        private static Task<bool> ConfirmAsync(string title, string body, bool danger = false)
        {
            // 设置默认按钮为“否”，降低误触风险
            var icon = danger ? MessageBoxImage.Warning : MessageBoxImage.Question;
            var result = MessageBox.Show(
                body + "\n\n" + L("是否继续？"),
                title,
                MessageBoxButton.YesNo,
                icon,
                MessageBoxResult.No);
            return Task.FromResult(result == MessageBoxResult.Yes);
        }

        // === 新增：封装“先确认再执行再回写”的通用流程 ===
        private static async Task ConfirmThenSendAsync(
            string title,
            string confirmText,
            string action,
            string actionName,
            bool danger = false,
            Dictionary<string, object> args = null,
            CommandTarget target = null)
        {
            if (!await ConfirmAsync(title, confirmText, danger))
            {
                TryPostInfo(L("已取消：") + actionName, "warn");
                return;
            }

            TryPostInfo(L("执行：") + actionName, "info");
            var res = await SendCommand(action, actionName, args, target);
            var level = res.Contains(L("成功")) || res.IndexOf("succeeded", StringComparison.OrdinalIgnoreCase) >= 0 || res.Contains("OK") ? "ok"
                      : (res.Contains(L("失败")) || res.Contains(L("异常")) || res.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || res.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0) ? "error"
                      : "info";
            TryPostInfo(res, level);
        }

        // === 事件处理（已加确认） ===
        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            await ConfirmThenSendAsync(
                title: L("启动机台确认"),
                confirmText: L("将执行【启动】操作，请确认现场已具备安全条件（防护门/急停/人员离开危险区）。"),
                action: "StartMachine",
                actionName: L("设备启动"),
                danger: false);
        }

        private async void Pause_Click(object sender, RoutedEventArgs e)
        {
            await ConfirmThenSendAsync(
                title: L("暂停机台确认"),
                confirmText: L("将执行【暂停】操作，设备可能进入保持/等待状态。"),
                action: "PauseMachine",
                actionName: L("设备暂停"),
                danger: false);
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            await ConfirmThenSendAsync(
                title: L("复位确认"),
                confirmText: L("将执行【复位】操作，轴/气缸等机构可能移动至初始位。请确认无人员在危险区域。"),
                action: "ResetMachine",
                actionName: L("设备复位"),
                danger: true);
        }

        private async void ClearAlarm_Click(object sender, RoutedEventArgs e)
        {
            await ConfirmThenSendAsync(
                title: L("清除报警确认"),
                confirmText: L("将执行【清除报警】。若根因未排除，设备可能再次报警。是否继续？"),
                action: "ClearMachineAlarms",
                actionName: L("机台报警消除"),
                danger: true);
        }

        private async void Calibrate_Click(object sender, RoutedEventArgs e)
        {
            await ConfirmThenSendAsync(
                title: L("一键视觉标定确认"),
                confirmText: L("将执行【视觉一键标定】。\n注意：机构/光源/镜头可能自动运动与切换，请确认治具与安全防护到位。"),
                action: "VisionCalibrateMachine",
                actionName: L("视觉标定"),
                danger: true,
                args: PD(("mode", "auto"))); // 如需参数可在此填写
        }

        // 一键点检
        private async void Inspect_Click(object sender, RoutedEventArgs e)
        {
            await ConfirmThenSendAsync(
                title: L("一键点检确认"),
                confirmText: L("将执行【一键点检】。\n可能依次触发 IO/气压/真空/相机连通/轴原点等检查，部分检查会短暂动作，请确认现场安全。"),
                action: "QuickInspectionMachine",     // 或按你后端命名： "Maintenance.QuickInspection"
                actionName: L("一键点检"),
                danger: true
            );
        }

    }
}
