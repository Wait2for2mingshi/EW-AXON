using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EW_Assistant.Services
{
    internal static class AgentControlValueHelper
    {
        internal static string CreateRouteTraceId()
        {
            return "route-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        internal static string NormalizeDecision(string decision)
        {
            var normalized = (decision ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "reply_direct" => "reply_direct",
                "run_executor" => "run_executor",
                "reject" => "reject",
                _ => "need_human"
            };
        }

        internal static string SafeValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        }

        internal static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
                return string.Empty;

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return string.Empty;
        }

        internal static object ParseJsonLike(object value, object fallback)
        {
            if (value is JObject || value is JArray)
                return value;

            var text = DifyOutputSanitizer.Clean(value?.ToString());
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
                if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    lines.RemoveAt(0);
                if (lines.Count > 0 && lines[lines.Count - 1].Trim() == "```")
                    lines.RemoveAt(lines.Count - 1);
                text = string.Join("\n", lines).Trim();
            }

            var startObj = text.IndexOf('{');
            var endObj = text.LastIndexOf('}');
            var startArr = text.IndexOf('[');
            var endArr = text.LastIndexOf(']');

            try
            {
                if (startArr >= 0 && endArr > startArr && (startObj < 0 || startArr < startObj))
                    return JArray.Parse(text.Substring(startArr, endArr - startArr + 1));
                if (startObj >= 0 && endObj > startObj)
                    return JObject.Parse(text.Substring(startObj, endObj - startObj + 1));
                return JToken.Parse(text);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
