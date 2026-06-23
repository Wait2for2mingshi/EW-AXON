using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EW_Assistant.Diagnostics;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 适配 Dify 工作流编排【对话型应用】API（SSE 流式）。
    /// 负责：发送消息、处理 SSE 事件、会话续聊、停止响应、文件上传，并写入本地对话日志。
    /// </summary>
    public sealed class DifyChatAdapter
    {
        public const long MaxImageAttachmentBytes = 15L * 1024L * 1024L;
        private static readonly HashSet<string> s_supportedImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".gif",
            ".webp"
        };

        private readonly HttpClient _http; // 复用外部HttpClient
        private readonly string _fallbackUser;

        /// <summary>上次对话的 conversation_id（用于续聊）</summary>
        public string ConversationId { get; private set; }

        /// <summary>最近一次流的 task_id（用于 /stop）</summary>
        public string LastTaskId { get; private set; }

        public DifyChatAdapter(HttpClient http, string baseUrl, string apiKey, string user, string conversationId = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _fallbackUser = user ?? string.Empty;
            ConversationId = conversationId;
        }

        /// <summary>
        /// 发送一条消息并以 SSE 流式读取结果，支持 token 增量、整段替换与事件回调。
        /// </summary>
        public async Task SendStreamingAsync(
            string query,
            CancellationToken ct,
            Action<string> onToken,              // 分片文本
            Action<string> onReplaceAll = null,  // 审查触发时替换整个回复
            Action onComplete = null,            // 收到 message_end
            IDictionary<string, object> inputs = null,
            bool? autoGenerateName = null,
            IEnumerable<object> files = null,    // 见：BuildFileObjects(...) 用法
            Action<string, JObject> onEvent = null,
            string fileLogSummary = null)
        {
            // === 追加：日志累积与包裹回调 ===
            var answerSb = new StringBuilder();
            var startAt = DateTime.Now;
            var hasCompleted = false;
            var questionLog = BuildQuestionLog(query, fileLogSummary);

            // 包裹 token 回调：既推 UI，又累计文本
            void TokenSink(string t)
            {
                if (!string.IsNullOrEmpty(t)) answerSb.Append(t);
                onToken?.Invoke(t);
            }

            // 包裹 replace 回调：替换 UI 的同时，重置累计文本
            void ReplaceSink(string full)
            {
                var cleaned = full == null ? null : DifyOutputSanitizer.Clean(full);
                answerSb.Clear();
                if (cleaned != null) answerSb.Append(cleaned);
                onReplaceAll?.Invoke(cleaned);
            }

            // 包裹 complete 回调：写一次完整 Q/A 日志
            void CompleteSink()
            {
                if (!hasCompleted)
                {
                    hasCompleted = true;
                    var elapsed = (DateTime.Now - startAt).TotalSeconds;
                    WriteLog($"Q: {questionLog}{Environment.NewLine}A: {DifyOutputSanitizer.Clean(answerSb.ToString())}{Environment.NewLine}[耗时]{elapsed:F2}s");
                }
                onComplete?.Invoke();
            }

            // === 原有发起请求逻辑 ===
            var payload = new Dictionary<string, object>
            {
                ["inputs"] = inputs ?? new Dictionary<string, object>(),
                ["query"] = query ?? string.Empty,
                ["response_mode"] = "streaming",
                ["conversation_id"] = ConversationId ?? string.Empty,
                ["user"] = ResolveUser()
            };
            if (autoGenerateName.HasValue) payload["auto_generate_name"] = autoGenerateName.Value;
            if (files != null) payload["files"] = files;

            using var req = new HttpRequestMessage(HttpMethod.Post, ConfigService.Current.URL+ "/chat-messages");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ConfigService.Current.ChatKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var cancelReg = ct.Register(() =>
            {
                try { stream.Dispose(); } catch { }
                try { resp.Dispose(); } catch { }
            });

            using var reader = new StreamReader(stream, Encoding.UTF8);

            var buffer = new StringBuilder();
            try
            {
                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;

                    // 空行 => 一个 SSE 块结束
                    if (line.Length == 0)
                    {
                        if (buffer.Length > 0)
                        {
                            var json = buffer.ToString();
                            buffer.Clear();
                            // 用包裹后的回调
                            HandleSseChunk(json, TokenSink, ReplaceSink, CompleteSink, onEvent);
                        }
                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var part = line.Substring(5).TrimStart();
                        buffer.Append(part);
                    }
                }

                // 末尾残留
                if (buffer.Length > 0)
                {
                    HandleSseChunk(buffer.ToString(), TokenSink, ReplaceSink, CompleteSink, onEvent);
                    buffer.Clear();
                }
            }
            catch (Exception ex)
            {
                // 异常时也把当前已累积答案写入日志（不覆盖正常 complete 的日志）
                if (!hasCompleted)
                {
                    var tail = IsExpectedStreamStop(ex, ct)
                        ? "[已取消] 聊天流已在本地停止读取。"
                        : "[异常] " + ex.Message;

                    WriteLog(
                        $"Q: {questionLog}" + Environment.NewLine +
                        $"A(partial): {DifyOutputSanitizer.Clean(answerSb.ToString())}" + Environment.NewLine +
                        tail
                    );
                    hasCompleted = true;
                }
                throw;
            }
            finally
            {
                // 有些极端情况下未触发 message_end，这里兜底一次
                if (!hasCompleted)
                {
                    var elapsed = (DateTime.Now - startAt).TotalSeconds;
                    WriteLog($"Q: {questionLog}{Environment.NewLine}A(partial): {DifyOutputSanitizer.Clean(answerSb.ToString())}{Environment.NewLine}[耗时]{elapsed:F2}s");
                }
            }
        }

        public async Task<string> UploadImageAsync(string imagePath, CancellationToken ct)
        {
            ValidateImageFileForUpload(imagePath);

            var cfg = ConfigService.Current ?? throw new InvalidOperationException("配置尚未加载，无法上传图片。");
            if (string.IsNullOrWhiteSpace(cfg.URL))
                throw new InvalidOperationException("URL 未配置，无法上传图片。");
            if (string.IsNullOrWhiteSpace(cfg.ChatKey))
                throw new InvalidOperationException("ChatKey 未配置，无法上传图片。");

            var fileInfo = new FileInfo(imagePath);
            using var content = new MultipartFormDataContent();
            using var fileStream = OpenImageReadStream(fileInfo.FullName);
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessImageMimeType(fileInfo.Extension));
            content.Add(fileContent, "file", fileInfo.Name);
            content.Add(new StringContent(ResolveUser(), Encoding.UTF8), "user");

            var url = cfg.URL.TrimEnd('/') + "/files/upload";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ChatKey);
            req.Content = content;

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.Created && !resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"上传图片失败，HTTP {(int)resp.StatusCode}: {body}");

            var json = JObject.Parse(body);
            var id = json.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("上传图片成功但未返回文件 id：" + body);

            return id;
        }

        public static object BuildLocalImageFileObject(string uploadFileId)
        {
            if (string.IsNullOrWhiteSpace(uploadFileId))
                throw new ArgumentNullException(nameof(uploadFileId));

            return new Dictionary<string, object>
            {
                ["type"] = "image",
                ["transfer_method"] = "local_file",
                ["upload_file_id"] = uploadFileId
            };
        }

        public static void ValidateImageFileForUpload(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new InvalidOperationException("请先选择图片。");

            if (!File.Exists(filePath))
                throw new InvalidOperationException("图片文件不存在。");

            var ext = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(ext) || !s_supportedImageExtensions.Contains(ext))
                throw new InvalidOperationException("仅支持 PNG、JPG、JPEG、BMP、GIF、WEBP 图片。");

            var info = new FileInfo(filePath);
            if (info.Length > MaxImageAttachmentBytes)
                throw new InvalidOperationException("图片大小超过 15MB，请压缩后再上传。");
        }

        private static string GuessImageMimeType(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".png":
                    return "image/png";
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".bmp":
                    return "image/bmp";
                case ".gif":
                    return "image/gif";
                case ".webp":
                    return "image/webp";
                default:
                    return "application/octet-stream";
            }
        }

        private static FileStream OpenImageReadStream(string filePath)
        {
            try
            {
                return File.OpenRead(filePath);
            }
            catch (FileNotFoundException)
            {
                throw new InvalidOperationException("图片文件不存在。");
            }
            catch (DirectoryNotFoundException)
            {
                throw new InvalidOperationException("图片文件不存在。");
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("图片文件无法读取，请检查权限。");
            }
            catch (IOException)
            {
                throw new InvalidOperationException("图片文件当前无法读取，请稍后重试。");
            }
        }

        private static string BuildQuestionLog(string query, string fileLogSummary)
        {
            if (string.IsNullOrWhiteSpace(fileLogSummary))
                return query ?? string.Empty;

            return (query ?? string.Empty) + Environment.NewLine + "[附件] " + fileLogSummary;
        }

        private static readonly object s_logLock = new object();
        /// <summary>将问答过程写入本地聊天日志，失败不抛出异常。</summary>
        public static void WriteLog(string str)
        {
            try
            {
                Directory.CreateDirectory(@"D:\Data\AiLog\Chat");
                var path = Path.Combine(@"D:\Data\AiLog\Chat", DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
                // 简单直写：在每条 Q/A 前增加显著分隔，保持原始内容
                var normalized = (str ?? string.Empty).Replace("\r\n", "\n");
                var sb = new StringBuilder();
                sb.AppendLine(new string('=', 72));
                sb.AppendFormat("[{0:yyyy-MM-dd HH:mm:ss}] ", DateTime.Now);
                sb.AppendLine();
                sb.AppendLine(normalized);
                var line = sb.ToString();

                lock (s_logLock)
                {
                    using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                    {
                        if (fs.Length == 0)
                        {
                            var bom = Encoding.UTF8.GetPreamble(); // EF BB BF
                            if (bom.Length > 0) fs.Write(bom, 0, bom.Length);
                        }

                        fs.Seek(0, SeekOrigin.End);
                        using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                        {
                            sw.WriteLine(line);
                            sw.Flush();
                        }
                    }
                }

                LogRetentionPolicy.TryCleanupFiles(
                    @"D:\Data\AiLog\Chat",
                    "*.txt",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WriteLog failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析单个 SSE JSON 事件并调用对应回调，兼容 message/message_replace/message_file 等事件类型。
        /// </summary>
        private void HandleSseChunk(string jsonLine,
            Action<string> onToken,
            Action<string> onReplaceAll,
            Action onComplete,
           Action<string, JObject> onEvent)
        {
            // Dify 的 SSE 每块就是一条 JSON
            if (string.IsNullOrWhiteSpace(jsonLine)) return;

            JObject obj;
            try { obj = JObject.Parse(jsonLine); }
            catch
            {
                // 非 JSON（偶发脏行/心跳），忽略
                return;
            }

            var evt = (string)obj["event"]; // e.g. "message" / "message_end" / "message_replace" / "error" / ping / workflow/node...
            onEvent?.Invoke(evt, obj);  // ⬅️ 抛给上层做“进度”可视化
            switch (evt)
            {
                case "message":
                    LastTaskId = (string)(obj["task_id"] ?? LastTaskId);
                    ConversationId = (string)(obj["conversation_id"] ?? ConversationId);
                    var delta = (string)obj["answer"];
                    if (!string.IsNullOrEmpty(delta))
                        onToken?.Invoke(delta);
                    break;

                case "message_replace":
                    LastTaskId = (string)(obj["task_id"] ?? LastTaskId);
                    ConversationId = (string)(obj["conversation_id"] ?? ConversationId);
                    var replaced = (string)obj["answer"];
                    if (replaced != null)
                        onReplaceAll?.Invoke(replaced);
                    break;

                case "message_file":
                    // Dify: 图片文件事件（来自 assistant）
                    // 你可以按需把图片以 Markdown 嵌入（MdXaml 支持）
                    var url = (string)obj["url"];
                    if (!string.IsNullOrEmpty(url))
                        onToken?.Invoke($"\n\n![]({url})\n\n");
                    break;

                case "message_end":
                    LastTaskId = (string)(obj["task_id"] ?? LastTaskId);
                    ConversationId = (string)(obj["conversation_id"] ?? ConversationId);
                    onComplete?.Invoke();
                    break;

                case "tts_message":
                case "tts_message_end":
                case "workflow_started":
                case "workflow_finished":
                case "node_started":
                case "node_finished":
                case "ping":
                    // 可按需调试/忽略
                    break;

                case "error":
                    var code = (string)obj["code"];
                    var msg = (string)obj["message"];
                    onToken?.Invoke($"\n\n> ⚠️ **Dify错误** {code}: {msg}\n\n");
                    onComplete?.Invoke();
                    break;

                default:
                    // 未知事件，忽略
                    break;
            }
        }

        /// <summary>
        /// 停止当前流（仅流式模式有效）。要求 body 传 user，且 task_id 为最近一次消息的 task。
        /// </summary>
        public async Task<bool> TryStopAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(LastTaskId)) return false;

            var url = $"{ConfigService.Current.URL + "/chat-messages"}/{LastTaskId}/stop";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ConfigService.Current.ChatKey);
            req.Content = new StringContent(JsonConvert.SerializeObject(new { user = ResolveUser() }), Encoding.UTF8, "application/json");

            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static bool IsExpectedStreamStop(Exception ex, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return true;

            while (ex != null)
            {
                if (ex is OperationCanceledException)
                    return true;

                ex = ex.InnerException;
            }

            return false;
        }

        private string ResolveUser()
        {
            var configuredUser = ConfigService.Current?.User;
            if (!string.IsNullOrWhiteSpace(configuredUser))
                return configuredUser.Trim();

            if (!string.IsNullOrWhiteSpace(_fallbackUser))
                return _fallbackUser.Trim();

            return Environment.MachineName;
        }
    }
}
