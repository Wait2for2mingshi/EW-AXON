using System.Diagnostics;
using System.Text;
using EW_Assistant.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace McpServer
{
    /// <summary>
    /// 记录 MCP HTTP 入口原始请求，便于排查模型生成的 tools/call 是否符合协议。
    /// </summary>
    internal static class McpHttpRequestLogger
    {
        private const int MaxBodyChars = 20000;
        private const string PrimaryLogRoot = @"D:\Data\AiLog\McpServer\Http";
        private static readonly object _lock = new object();

        public static void Use(WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                if (!ShouldLog(context.Request))
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                var startedAt = DateTime.Now;
                var sw = Stopwatch.StartNew();
                var traceId = Guid.NewGuid().ToString("N");
                var requestBody = await ReadRequestBodyAsync(context.Request).ConfigureAwait(false);
                var originalResponseBody = context.Response.Body;
                var captureResponse = ShouldCaptureResponse(context.Request);
                MemoryStream? responseBuffer = null;
                Exception? caught = null;

                if (captureResponse)
                {
                    responseBuffer = new MemoryStream();
                    context.Response.Body = responseBuffer;
                }

                try
                {
                    await next().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    caught = ex;
                    throw;
                }
                finally
                {
                    sw.Stop();

                    var responseBody = string.Empty;
                    if (responseBuffer != null)
                    {
                        context.Response.Body = originalResponseBody;
                        responseBuffer.Position = 0;
                        responseBody = await ReadStreamAsync(responseBuffer).ConfigureAwait(false);
                        responseBuffer.Position = 0;
                        await responseBuffer.CopyToAsync(originalResponseBody).ConfigureAwait(false);
                        responseBuffer.Dispose();
                    }

                    WriteLog(
                        traceId,
                        startedAt,
                        sw.Elapsed,
                        context,
                        requestBody,
                        responseBody,
                        caught);
                }
            });
        }

        private static bool ShouldLog(HttpRequest request)
        {
            var path = request.Path.Value ?? string.Empty;
            return path.StartsWith("/sse", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/message", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldCaptureResponse(HttpRequest request)
        {
            return HttpMethods.IsPost(request.Method);
        }

        private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
        {
            if (!HttpMethods.IsPost(request.Method)
                && !HttpMethods.IsPut(request.Method)
                && !HttpMethods.IsPatch(request.Method))
                return string.Empty;

            if (request.ContentLength == 0)
                return string.Empty;

            request.EnableBuffering();
            if (request.Body.CanSeek)
                request.Body.Position = 0;

            using var reader = new StreamReader(
                request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 8192,
                leaveOpen: true);

            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (request.Body.CanSeek)
                request.Body.Position = 0;

            return TrimForLog(text);
        }

        private static async Task<string> ReadStreamAsync(Stream stream)
        {
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 8192,
                leaveOpen: true);

            return TrimForLog(await reader.ReadToEndAsync().ConfigureAwait(false));
        }

        private static void WriteLog(
            string traceId,
            DateTime startedAt,
            TimeSpan elapsed,
            HttpContext context,
            string requestBody,
            string responseBody,
            Exception? exception)
        {
            try
            {
                var dir = ResolveLogRoot();
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

                var request = context.Request;
                var response = context.Response;
                var sb = new StringBuilder();
                sb.AppendLine($"[{startedAt:yyyy-MM-dd HH:mm:ss.fff}] traceId={traceId}");
                sb.AppendLine($"remote={context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}");
                sb.AppendLine($"request={request.Method} {request.PathBase}{request.Path}{request.QueryString}");
                sb.AppendLine($"status={response.StatusCode} elapsedMs={(long)elapsed.TotalMilliseconds}");
                sb.AppendLine($"contentType={request.ContentType ?? string.Empty}");
                sb.AppendLine($"userAgent={request.Headers.UserAgent.ToString()}");
                if (!string.IsNullOrWhiteSpace(requestBody))
                {
                    sb.AppendLine("requestBody:");
                    sb.AppendLine(requestBody);
                }
                if (!string.IsNullOrWhiteSpace(responseBody))
                {
                    sb.AppendLine("responseBody:");
                    sb.AppendLine(responseBody);
                }
                if (exception != null)
                {
                    sb.AppendLine("exception:");
                    sb.AppendLine(exception.ToString());
                }
                sb.AppendLine(new string('-', 72));

                lock (_lock)
                {
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }

                LogRetentionPolicy.TryCleanupFiles(
                    dir,
                    "*.log",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(14));
            }
            catch
            {
                // 原始请求日志失败不能影响 MCP 调用。
            }
        }

        private static string ResolveLogRoot()
        {
            try
            {
                Directory.CreateDirectory(PrimaryLogRoot);
                return PrimaryLogRoot;
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "AiLog", "McpServer", "Http");
            }
        }

        private static string TrimForLog(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxBodyChars)
                return text ?? string.Empty;

            return text.Substring(0, MaxBodyChars) + Environment.NewLine + "...<truncated>";
        }
    }
}
