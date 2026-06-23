using System;
using EW_Assistant.Io;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using McpServer;
using EW_Assistant.Diagnostics;

namespace EW_Assistant.McpTools
{
    [McpServerToolType]
    public static class IoMcpTools
    {
        // === 配置：非 3010/3012 直读地址的位语义（true 表示“开”）===
        private const bool DIRECT_CHECK_TRUE_MEANS_OPEN = true;

        private static string Err(string where, string msg) =>
            JsonConvert.SerializeObject(new { type = "error", where, message = msg });

        private static readonly object s_ioLogLock = new object();

        /// <summary>IO 相关的本地日志，便于排查 LLM 调用失败原因。</summary>
        private static void LogIoTrace(object detail)
        {
            try
            {
                var dir = @"D:\Data\AiLog\McpTools";
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                    var baseDir = AppContext.BaseDirectory ?? AppDomain.CurrentDomain.BaseDirectory ?? ".";
                    dir = Path.Combine(baseDir, "AiLog", "McpTools");
                    Directory.CreateDirectory(dir);
                }

                var path = Path.Combine(dir, $"io-command-{DateTime.Now:yyyy-MM-dd}.log");
                var json = JsonConvert.SerializeObject(detail, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {json}{Environment.NewLine}";

                lock (s_ioLogLock)
                {
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }

                LogRetentionPolicy.TryCleanupFiles(
                    dir,
                    "io-command-*.log",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch
            {
                // 日志失败忽略，避免影响主流程
            }
        }

        private static string TrimForLog(string? text, int max = 2000)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (text.Length <= max) return text;
            return text.Substring(0, max) + $"...（后续截断 {text.Length - max} 字符）";
        }

        // 仅用于选择 open/close；不再支持 toggle
        // 返回：ok=true 时 intentOrReason 一定为 "open"/"close"
        //       ok=false 时 intentOrReason 为错误原因说明
        private static (bool ok, string intentOrReason) NormalizeIntent(string raw)
        {
            var s = (raw ?? string.Empty).Trim();

            if (s.Length == 0)
            {
                // 原来这里是默认 open，现在改成直接报错，强制调用方给出明确意图
                return (false, "未提供操作意图，请明确指定：open / close（或中文：打开 / 关闭）。");
            }

            var lower = s.ToLowerInvariant();

            // ===== 英文别名 =====
            if (lower == "open" ||
                lower == "on" ||
                lower == "start" ||
                lower == "enable" ||
                lower == "1" ||
                lower == "true")
            {
                return (true, "open");
            }

            if (lower == "close" ||
                lower == "off" ||
                lower == "stop" ||
                lower == "disable" ||
                lower == "0" ||
                lower == "false")
            {
                return (true, "close");
            }

            // ===== 中文 / 混合短语：看是否包含典型关键词 =====
            // 开 → 打开、开启、上电、通电、吸合、合上、伸出、升起、抬起
            if (Regex.IsMatch(s, "打开|开启|上电|通电|吸合|合上|伸出|升起|抬起"))
            {
                return (true, "open");
            }

            // 关 → 关闭、关掉、关上、停机、停止、下电、断电、断开、释放、回缩、缩回、落下、放下、复位
            if (Regex.IsMatch(s, "关闭|关掉|关上|停机|停止|下电|断电|断开|释放|回缩|缩回|落下|放下|复位"))
            {
                return (true, "close");
            }

            // ===== 明确拒绝 toggle 语义 =====
            if (lower == "toggle" || Regex.IsMatch(s, "切换|翻转|取反"))
            {
                return (false, "当前逻辑不再支持 toggle/切换，请明确指定：打开(open) 或 关闭(close)。");
            }

            // ===== 兜底：无法判断 =====
            return (false, "无法识别是打开还是关闭，请明确指定：open / close（或中文：打开 / 关闭）。");
        }

        private static string ToBusinessActionText(string intent)
        {
            return string.Equals(intent, "open", StringComparison.OrdinalIgnoreCase) ? "打开"
                 : string.Equals(intent, "close", StringComparison.OrdinalIgnoreCase) ? "关闭"
                 : (intent ?? string.Empty).Trim();
        }

        private static string BuildIoCommandVerificationText(string intent, string verdict)
        {
            var actionText = ToBusinessActionText(intent);
            return (verdict ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "ok" => string.IsNullOrWhiteSpace(actionText) ? "动作已到位。" : $"{actionText}动作已到位。",
                "readback_unexpected" => string.IsNullOrWhiteSpace(actionText) ? "执行后状态与预期不一致。" : $"{actionText}后状态与预期不一致。",
                "readback_unavailable" => "未获取到可用读回结果，无法完成状态校验。",
                "timeout" => "执行超时，未完成状态校验。",
                _ => "状态校验结果未知。"
            };
        }

        private static string BuildIoCommandResultText(string ioName, string intent, string verdict)
        {
            var safeName = string.IsNullOrWhiteSpace(ioName) ? "目标IO" : ioName.Trim();
            var actionText = ToBusinessActionText(intent);

            return (verdict ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "ok" => $"已对 {safeName} 执行{actionText}，读回校验通过。",
                "readback_unexpected" => $"已对 {safeName} 执行{actionText}，但读回校验与预期不一致。",
                "readback_unavailable" => $"已对 {safeName} 执行{actionText}，但无法完成读回校验。",
                "timeout" => $"对 {safeName} 执行{actionText}超时，未获取到有效响应。",
                _ => $"已对 {safeName} 执行{actionText}。"
            };
        }

        private static string ToBusinessVerificationStatus(string verdict)
        {
            return (verdict ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "ok" => "passed",
                "readback_unexpected" => "failed",
                "readback_unavailable" => "unavailable",
                "timeout" => "timeout",
                _ => "unknown"
            };
        }

        private static bool IsIoCommandShadowModeEnabled()
        {
            try
            {
                return Base.ReadAppConfig()?.ClearMachineAlarmsShadowMode ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 写 IO 并对“本次写动作的读回位”做一次校验。
        /// 注意：
        /// 1. 该工具不是“写完后再读取当前业务态”的接口。
        /// 2. 对外返回以业务表述为主，不再暴露易误解的校验位 expected/actual。
        /// 3. 工具返回描述的是“本次动作执行与校验结果”，不是设备当前业务态。
        /// </summary>
        [McpServerTool, Description(
     "控制现场设备 IO 点位（如气缸、电磁阀、继电器）的打开/关闭。\n" +
     "当用户说『打开/关闭 某某气缸/电磁阀/IO』时，应优先调用此工具，而不是清除报警。\n" +
     "示例：\n" +
     " - 打开 A未测分料气缸 → IoCommand(ioName=\"A未测分料气缸\", op=\"open\")\n" +
     " - 关闭 A未测分料气缸 → IoCommand(ioName=\"A未测分料气缸\", op=\"close\")\n" +
     " - 关闭 NG抽屉解锁气缸A → IoCommand(ioName=\"NG抽屉解锁气缸A\", op=\"close\")\n" +
     "【硬约束 —— op 只能是打开 / 关闭】\n" +
     " - 参数 op 只能表示两种意图：打开(open) 或 关闭(close)。\n" +
     " - 允许的取值示例：\"open\"、\"close\"，或中文 \"打开\"、\"关闭\"。\n" +
     "\n" +
     "注意：本工具只做 IO 动作，不清除报警或复位机台。"
 )]
        public static async Task<string> IoCommand(
     [Description("目标 IO 名称；与 IO 映射表中的 Name 完全一致，例如：\"A未测分料气缸\"")]
    string ioName = null,

     [Description(
        "操作意图：open=打开 / close=关闭。\n" +
        "【别名归一】\n" +
        " - open：\"open\" / \"打开\" / \"开启\" / \"ON\"\n" +
        " - close：\"close\" / \"关闭\" / \"关掉\" / \"OFF\"\n"
    )]
    string op = null
 )

        {
            var shadowMode = IsIoCommandShadowModeEnabled();
            LogIoTrace(new { stage = "开始", ioName, op, shadowMode, mapCount = IoMapRepository.Count });

            if (IoMapRepository.Count == 0)
            {
                var err = Err("IoCommand", "IO 映射未加载，请先调用 LoadIoMap。");
                LogIoTrace(new { stage = "校验失败", reason = "IO 映射未加载", ioName, op });
                ToolCallLogger.Log(nameof(IoCommand), new { ioName, op, shadowMode }, null, "IO 映射未加载");
                return err;
            }

            if (string.IsNullOrWhiteSpace(ioName))
            {
                var err = Err("IoCommand", "请提供 ioName。");
                LogIoTrace(new { stage = "校验失败", reason = "缺少 ioName", ioName, op });
                ToolCallLogger.Log(nameof(IoCommand), new { ioName, op, shadowMode }, null, "缺少 ioName");
                return err;
            }

            if (!IoMapRepository.TryGetEntry(ioName, out IoEntry entry) || entry == null)
            {
                var err = Err("IoCommand", $"未找到 IO 名称：{ioName}");
                LogIoTrace(new { stage = "校验失败", reason = "映射未找到", ioName, op });
                ToolCallLogger.Log(nameof(IoCommand), new { ioName, op, shadowMode }, null, "映射未找到");
                return err;
            }

            // 判定意图（仅用于选地址）
            var norm = NormalizeIntent(op);
            if (!norm.ok)
            {
                var err = Err("IoCommand", norm.intentOrReason);
                LogIoTrace(new { stage = "校验失败", reason = norm.intentOrReason, ioName = entry.Name, op });
                ToolCallLogger.Log(nameof(IoCommand), new { ioName, op, shadowMode }, null, norm.intentOrReason);
                return err;
            }
            var intent = norm.intentOrReason; // "open" / "close"

            // 下面保持你原来的逻辑不变……
            // 选择目标地址（始终写 on）
            var targetAddress = intent == "open" ? entry.OpenAddress : entry.CloseAddress;
            if (string.IsNullOrWhiteSpace(targetAddress))
            {
                var err = Err("IoCommand", $"映射项缺少{(intent == "open" ? "OpenAddress" : "CloseAddress")}：{entry.Name}");
                LogIoTrace(new { stage = "校验失败", reason = "映射缺少地址", ioName = entry.Name, intent });
                ToolCallLogger.Log(nameof(IoCommand), new { ioName, op, shadowMode }, null, "映射缺少地址");
                return err;
            }

            if (shadowMode)
            {
                var verificationText = BuildIoCommandVerificationText(intent, "ok");
                var resultText = BuildIoCommandResultText(entry.Name, intent, "ok");
                var shadowResult = JsonConvert.SerializeObject(
                    new
                    {
                        type = "io.command",
                        success = true,
                        ioName = entry.Name,
                        actionCode = intent,
                        actionText = ToBusinessActionText(intent),
                        verificationStatus = "passed",
                        verificationText,
                        resultText,
                        address = targetAddress
                    },
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }
                );

                LogIoTrace(new
                {
                    stage = "影子模式短路",
                    ioName = entry.Name,
                    intent,
                    targetAddress,
                    rawOp = op
                });
                ToolCallLogger.Log(
                    nameof(IoCommand),
                    new { ioName = entry.Name, op, address = targetAddress, shadowMode = true },
                    shadowResult);
                return shadowResult;
            }

            var pd = Tool.PD(
                ("ioIndex", entry.Index),
                ("address", targetAddress)
            );

            var checkAddress = entry.CheckAddress;

            // open 指令若落在 3010x 读回域，切到 3012x（1态）做验收。
            if (intent == "open" &&
                !string.IsNullOrWhiteSpace(checkAddress) &&
                checkAddress.StartsWith("3010", StringComparison.OrdinalIgnoreCase))
            {
                checkAddress = "3012" + checkAddress.Substring(4);
            }
            var checkRole = GetAddressRole(checkAddress);

            LogIoTrace(new
            {
                stage = "准备发送",
                ioName = entry.Name,
                intent,
                targetAddress,
                checkAddress,
                checkRole,
                ioIndex = entry.Index,
                checkIndex = entry.CheckIndex,
                rawOp = op
            });

            if (!string.IsNullOrWhiteSpace(checkAddress))
            {
                pd.Add("checkAddress", checkAddress);
            }

            // 下发
            Tool.CommandCallTrace trace = null;
            var writeRaw = await Tool.SendCommand(
                action: "IoWrite",
                actionName: "IO写入",
                args: pd,
                traceSink: t => trace = t
            ).ConfigureAwait(false);
            LogIoTrace(new
            {
                stage = "下发完成",
                ioName = entry.Name,
                intent,
                targetAddress,
                checkAddress,
                checkRole,
                ioIndex = entry.Index,
                checkIndex = entry.CheckIndex,
                httpStatus = trace?.HttpStatus?.ToString(),
                elapsedMs = trace?.ElapsedMs,
                timeout = trace?.Timeout,
                exception = trace?.Exception,
                request = TrimForLog(trace?.RequestJson),
                response = TrimForLog(trace?.ResponseText),
                returned = TrimForLog(trace?.ReturnText ?? writeRaw)
            });

            // 超时/无响应
            if (writeRaw.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
                writeRaw.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                var verificationText = BuildIoCommandVerificationText(intent, "timeout");
                var resultText = BuildIoCommandResultText(entry.Name, intent, "timeout");
                var resTimeout = JsonConvert.SerializeObject(new
                {
                    type = "io.command",
                    success = false,
                    ioName = entry.Name,
                    actionCode = intent,
                    actionText = ToBusinessActionText(intent),
                    verificationStatus = ToBusinessVerificationStatus("timeout"),
                    verificationText,
                    resultText,
                    address = targetAddress,
                    errorText = "request timeout/no response"
                });
                LogIoTrace(new
                {
                    stage = "超时/无响应",
                    ioName = entry.Name,
                    intent,
                    targetAddress,
                    checkAddress,
                    raw = TrimForLog(writeRaw),
                    elapsedMs = trace?.ElapsedMs
                });
                ToolCallLogger.Log(nameof(IoCommand), new { ioName, op, address = targetAddress, shadowMode }, resTimeout, "timeout/no response");
                return resTimeout;
            }

            // —— 读回期望：优先按地址角色判定（3010=close态位，3012=open态位）——
            // 规则：
            // - state0(3010x): bit=1 表示 close
            // - state1(3012x): bit=1 表示 open
            // - direct(其他): 走兼容常量语义
            // 这里得到的 expected/actual 仅用于“本次写动作”的位校验，
            // 不等同于 IoQueryStatus 对外返回的当前业务态 true/false。
            bool? expected = checkRole switch
            {
                "state0" => intent == "close",
                "state1" => intent == "open",
                _ => intent == "open"
                    ? (DIRECT_CHECK_TRUE_MEANS_OPEN ? true : false)
                    : (DIRECT_CHECK_TRUE_MEANS_OPEN ? false : true)
            };

            // 解析服务端返回的状态字（16位）
            var parsed = ParseIoWriteResponse(writeRaw, entry.CheckIndex);
            int? statusWord = parsed.statusWord;
            bool? bitValue = parsed.bitValue; // true=位1, false=位0

            string verdict =
                (statusWord is null || bitValue is null) ? "readback_unavailable" :
                (bitValue == expected) ? "ok" : "readback_unexpected";

            var verificationSummary = BuildIoCommandVerificationText(intent, verdict);
            var resultSummary = BuildIoCommandResultText(entry.Name, intent, verdict);

            var res = JsonConvert.SerializeObject(
                new
                {
                    type = "io.command",
                    success = verdict == "ok",
                    ioName = entry.Name,
                    actionCode = intent,
                    actionText = ToBusinessActionText(intent),
                    verificationStatus = ToBusinessVerificationStatus(verdict),
                    verificationText = verificationSummary,
                    resultText = resultSummary,
                    address = targetAddress
                },
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }
            );
            LogIoTrace(new
            {
                stage = "解析完成",
                ioName = entry.Name,
                intent,
                targetAddress,
                checkAddress,
                checkRole,
                bitIndex = entry.CheckIndex,
                expected,
                statusWord,
                bits = parsed.bits16,
                bitValue,
                actual = bitValue,
                verdict,
                raw = TrimForLog(writeRaw)
            });
            ToolCallLogger.Log(nameof(IoCommand), new { ioName, op, address = targetAddress, shadowMode }, res, verdict == "ok" ? null : $"status={verdict}");
            return res;
        }

        [McpServerTool, Description(
            "查询多个 IO 的当前状态。\n" +
            "输入参数 ioNames 是 IO 名称数组。\n" +
            "该工具只做状态读取，不做任何 IO 写入。"
        )]
        public static async Task<string> IoQueryStatus(
            [Description("要查询的 IO 名称数组，例如：[\"A未测分料气缸\",\"NG抽屉解锁气缸A\"]")]
            string[] ioNames = null
        )
        {
            LogIoTrace(new { stage = "开始查询", ioNames = ioNames ?? Array.Empty<string>(), mapCount = IoMapRepository.Count });

            if (ioNames == null || ioNames.Length == 0)
            {
                var err = Err("IoQueryStatus", "请提供 ioNames。");
                LogIoTrace(new { stage = "校验失败", reason = "缺少 ioNames", ioNames });
                ToolCallLogger.Log(nameof(IoQueryStatus), new { ioNames }, null, "缺少 ioNames");
                return err;
            }

            var trimmedNames = ioNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .ToList();

            if (trimmedNames.Count == 0)
            {
                var err = Err("IoQueryStatus", "ioNames 为空或全为空白。");
                LogIoTrace(new { stage = "校验失败", reason = "ioNames 为空", ioNames });
                ToolCallLogger.Log(nameof(IoQueryStatus), new { ioNames }, null, "ioNames 为空");
                return err;
            }

            var orderedNames = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in trimmedNames)
            {
                if (seenNames.Add(name))
                {
                    orderedNames.Add(name);
                }
            }

            if (IoMapRepository.Count == 0)
            {
                var resNoMap = BuildIoQueryResult(orderedNames, null);
                LogIoTrace(new { stage = "校验失败", reason = "IO 映射未加载", ioNames = orderedNames });
                ToolCallLogger.Log(nameof(IoQueryStatus), new { ioNames }, resNoMap, "IO 映射未加载");
                return resNoMap;
            }

            var missingNames = new List<string>();
            var missingCheckAddress = new List<string>();
            var items = new List<(string name, IoEntry entry, List<string> addresses)>();
            foreach (var name in orderedNames)
            {
                if (!IoMapRepository.TryGetEntry(name, out IoEntry entry) || entry == null)
                {
                    missingNames.Add(name);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.CheckAddress))
                {
                    missingCheckAddress.Add(entry.Name);
                    continue;
                }

                var addresses = ExpandCheckAddresses(entry.CheckAddress);
                if (addresses.Count == 0)
                {
                    missingCheckAddress.Add(entry.Name);
                    continue;
                }

                items.Add((name, entry, addresses));
            }

            var checkAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                foreach (var address in item.addresses)
                {
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        checkAddresses.Add(address.Trim());
                    }
                }
            }

            if (checkAddresses.Count == 0)
            {
                var resNoAddr = BuildIoQueryResult(orderedNames, null);
                LogIoTrace(new { stage = "校验失败", reason = "读回地址为空", missingNames, missingCheckAddress, ioNames = orderedNames });
                ToolCallLogger.Log(nameof(IoQueryStatus), new { ioNames }, resNoAddr, "读回地址为空");
                return resNoAddr;
            }

            var pd = Tool.PD(
                ("checkAddresses", checkAddresses.ToArray()),
                ("checkAddress", checkAddresses.Count == 1 ? checkAddresses.First() : null)
            );

            LogIoTrace(new
            {
                stage = "准备发送",
                ioNames = orderedNames,
                checkAddresses = checkAddresses.ToArray(),
                missingNames,
                missingCheckAddress
            });

            Tool.CommandCallTrace trace = null;
            var readRaw = await Tool.SendCommand(
                action: "IoRead",
                actionName: "IO查询",
                args: pd,
                traceSink: t => trace = t
            ).ConfigureAwait(false);

            LogIoTrace(new
            {
                stage = "下发完成",
                ioNames = orderedNames,
                checkAddresses = checkAddresses.ToArray(),
                httpStatus = trace?.HttpStatus?.ToString(),
                elapsedMs = trace?.ElapsedMs,
                timeout = trace?.Timeout,
                exception = trace?.Exception,
                request = TrimForLog(trace?.RequestJson),
                response = TrimForLog(trace?.ResponseText),
                returned = TrimForLog(trace?.ReturnText ?? readRaw)
            });

            if (readRaw.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
                readRaw.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                var resTimeout = BuildIoQueryResult(orderedNames, null);
                LogIoTrace(new
                {
                    stage = "超时/无响应",
                    ioNames = orderedNames,
                    checkAddresses = checkAddresses.ToArray(),
                    raw = TrimForLog(readRaw),
                    elapsedMs = trace?.ElapsedMs
                });
                ToolCallLogger.Log(nameof(IoQueryStatus), new { ioNames }, resTimeout, "timeout/no response");
                return resTimeout;
            }

            var statusByAddress = ParseIoReadResponse(readRaw);
            var stateByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var bitDetails = new List<object>();

            foreach (var item in items)
            {
                int hit = 0;
                bool? state0Bit = null;
                bool? state1Bit = null;
                bool? directBit = null;
                var hasState0Addr = item.addresses.Any(a => a.StartsWith("3010", StringComparison.OrdinalIgnoreCase));
                var hasState1Addr = item.addresses.Any(a => a.StartsWith("3012", StringComparison.OrdinalIgnoreCase));
                var useDual = hasState0Addr && hasState1Addr;
                var bitRows = new List<object>();
                foreach (var addr in item.addresses)
                {
                    var role = GetAddressRole(addr);
                    if (statusByAddress.TryGetValue(addr, out var word))
                    {
                        bool bitValue = GetBit((ushort)(word & 0xFFFF), item.entry.CheckIndex);
                        bool? bitValueBoxed = bitValue;
                        int? wordBoxed = word;
                        if (role == "state0") state0Bit = bitValue;
                        else if (role == "state1") state1Bit = bitValue;
                        else directBit = bitValue;
                        bitRows.Add(new
                        {
                            address = addr,
                            role,
                            bitIndex = item.entry.CheckIndex,
                            bitValue = bitValueBoxed,
                            statusWord = wordBoxed
                        });
                        hit++;
                    }
                    else
                    {
                        bitRows.Add(new
                        {
                            address = addr,
                            role,
                            bitIndex = item.entry.CheckIndex,
                            bitValue = (bool?)null,
                            statusWord = (int?)null
                        });
                    }
                }

                // 3010=0态、3012=1态，需要综合判断；3014 类地址直接取值
                string ioValue = "error";
                if (useDual)
                {
                    if (state0Bit.HasValue && state1Bit.HasValue)
                    {
                        if (state0Bit.Value != state1Bit.Value)
                        {
                            ioValue = state1Bit.Value ? "true" : "false";
                        }
                    }
                }
                else
                {
                    if (directBit.HasValue)
                    {
                        ioValue = directBit.Value ? "true" : "false";
                    }
                }

                if (hit == 0) ioValue = "error";
                stateByName[item.name] = ioValue;
                bitDetails.Add(new
                {
                    name = item.name,
                    bitIndex = item.entry.CheckIndex,
                    ioValue,
                    bits = bitRows
                });
            }

            var res = BuildIoQueryResult(orderedNames, stateByName);

            LogIoTrace(new
            {
                stage = "解析完成",
                ioNames = orderedNames,
                results = stateByName,
                bitDetails,
                missing = new
                {
                    names = missingNames,
                    checkAddress = missingCheckAddress
                },
                parsedCount = statusByAddress.Count,
            });
            ToolCallLogger.Log(nameof(IoQueryStatus), new { ioNames }, res, null);
            return res;
        }

        private static string BuildIoQueryResult(IEnumerable<string> orderedNames, IDictionary<string, string> stateByName)
        {
            var results = new JObject();
            if (orderedNames != null)
            {
                foreach (var name in orderedNames)
                {
                    var state = "error";
                    if (stateByName != null &&
                        stateByName.TryGetValue(name, out var value) &&
                        !string.IsNullOrWhiteSpace(value))
                    {
                        state = value;
                    }

                    results[name] = state;
                }
            }

            var root = new JObject { ["results"] = results };
            return root.ToString(Formatting.None);
        }

        // 3010/3012 双读回地址扩展（仅在前缀匹配时才扩展）
        private static List<string> ExpandCheckAddresses(string? checkAddress)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(checkAddress)) return list;

            var addr = checkAddress.Trim();
            list.Add(addr);

            if (addr.StartsWith("3010", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("3012" + addr.Substring(4));
            }
            else if (addr.StartsWith("3012", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("3010" + addr.Substring(4));
            }

            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ===== 解析 IoRead 返回的状态字（仅支持 {地址:十进制} 形式）=====
        private static Dictionary<string, int> ParseIoReadResponse(string? raw)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            try
            {
                var json = TryExtractJsonObject(raw);
                if (string.IsNullOrWhiteSpace(json)) return result;

                var settings = new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    CommentHandling = CommentHandling.Load
                };
                var jo = JObject.Parse(json, settings);
                if (jo.DescendantsAndSelf().Any(t => t.Type == JTokenType.Comment))
                {
                    return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
                foreach (var prop in jo.Properties())
                {
                    if (!IsLikelyAddress(prop.Name)) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (!TryParseStatusWord(prop.Value, out var word)) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    result[prop.Name] = word;
                }
            }
            catch
            {
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        private static bool TryParseStatusWord(JToken token, out int word)
        {
            word = 0;
            if (token == null) return false;

            if (token.Type == JTokenType.Integer)
            {
                var value = token.Value<long>();
                if (value < 0 || value > 65535) return false;
                word = (int)value;
                return true;
            }

            var s = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString();

            if (string.IsNullOrWhiteSpace(s)) return false;

            if (!long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return false;
            if (parsed < 0 || parsed > 65535) return false;
            word = (int)parsed;
            return true;

            return false;
        }

        private static bool IsLikelyAddress(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            for (int i = 0; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i])) return false;
            }
            return true;
        }

        private static string GetAddressRole(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "direct";
            if (address.StartsWith("3010", StringComparison.OrdinalIgnoreCase)) return "state0";
            if (address.StartsWith("3012", StringComparison.OrdinalIgnoreCase)) return "state1";
            return "direct";
        }

        // ===== 解析 IoWrite 返回的 16位状态字（从 "status" 字段）=====
        private static (int? statusWord, string? bits16, bool? bitValue)
        ParseIoWriteResponse(string? raw, int checkIndex)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, null, null);

            try
            {
                // 剥掉前缀，保留第一个 JSON 对象
                var json = TryExtractJsonObject(raw);
                if (json == null) return (null, null, null);

                var jo = JObject.Parse(json);
                var statusToken = jo["status"];
                if (statusToken == null) return (null, null, null);

                var statusStr = statusToken.Type == JTokenType.String
                    ? (string)statusToken
                    : statusToken.ToString();

                if (!int.TryParse(statusStr?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var word))
                    return (null, null, null);

                word &= 0xFFFF;
                var bits16 = Convert.ToString(word, 2).PadLeft(16, '0');

                // LSB 编号（1..16）：1=最低位（右一位），16=最高位（左一位）
                bool bitValue = GetBit((ushort)(word & 0xFFFF), checkIndex);

                return (word, bits16, bitValue);
            }
            catch
            {
                return (null, null, null);
            }
        }

        private static bool GetBit(ushort word, int checkIndex)
        {
            if (checkIndex < 1 || checkIndex > 16) throw new ArgumentOutOfRangeException(nameof(checkIndex));
            int bit0 = checkIndex - 1;
            return ((word >> bit0) & 1) != 0;
        }

        // 提取第一个完整 JSON（考虑字符串转义）
        private static string? TryExtractJsonObject(string s)
        {
            int start = s.IndexOf('{');
            if (start < 0) return null;

            int depth = 0;
            bool inStr = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];

                if (c == '"' && (i == start || s[i - 1] != '\\'))
                    inStr = !inStr;

                if (!inStr)
                {
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                            return s.Substring(start, i - start + 1);
                    }
                }
            }
            return null;
        }
    }
}
