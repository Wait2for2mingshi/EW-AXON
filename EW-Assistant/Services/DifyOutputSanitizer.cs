using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EW_Assistant.Services
{
    /// <summary>
    /// Dify 返回文本的客户端兜底清洗，避免模型推理块进入 UI、日志摘要或 JSON 解析。
    /// </summary>
    internal static class DifyOutputSanitizer
    {
        private static readonly string[] ReasoningTags = { "think", "thinking", "reasoning" };
        private static readonly HashSet<string> ReasoningFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "think",
            "thinking",
            "thought",
            "thoughts",
            "reasoning",
            "reasoning_content",
            "reasoningContent",
            "chain_of_thought",
            "chainOfThought"
        };
        private static readonly string[] DefaultVisibleTextKeys =
        {
            "text",
            "answer",
            "result",
            "output",
            "message",
            "output_text",
            "content"
        };

        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            var cleaned = StripReasoningBlocks(text).Trim();
            cleaned = StripReasoningFieldsFromJsonLike(cleaned);
            return StripReasoningBlocks(cleaned).Trim();
        }

        public static bool IsReasoningFieldName(string propertyName)
        {
            return !string.IsNullOrWhiteSpace(propertyName) &&
                   ReasoningFieldNames.Contains(propertyName.Trim());
        }

        public static JToken StripReasoningFields(JToken token)
        {
            if (token == null)
                return null;

            if (token.Type == JTokenType.Object)
            {
                var result = new JObject();
                foreach (var property in ((JObject)token).Properties())
                {
                    if (IsReasoningFieldName(property.Name))
                        continue;

                    result[property.Name] = StripReasoningFields(property.Value);
                }

                return result;
            }

            if (token.Type == JTokenType.Array)
            {
                var result = new JArray();
                foreach (var item in (JArray)token)
                    result.Add(StripReasoningFields(item));
                return result;
            }

            if (token.Type == JTokenType.String)
            {
                var value = token.Value<string>();
                if (TryParseJsonLike(value, out var parsed))
                    return StripReasoningFields(parsed);

                return new JValue(StripReasoningBlocks(value));
            }

            return token.DeepClone();
        }

        public static string CleanToken(JToken token, Formatting formatting = Formatting.None)
        {
            if (token == null || token.Type == JTokenType.Null)
                return string.Empty;

            var cleaned = StripReasoningFields(token);
            if (cleaned == null || cleaned.Type == JTokenType.Null)
                return string.Empty;

            return StripReasoningBlocks(cleaned.Type == JTokenType.String
                ? cleaned.Value<string>()
                : cleaned.ToString(formatting)).Trim();
        }

        public static string ExtractVisibleText(JToken token, params string[] preferredKeys)
        {
            var text = TryExtractVisibleText(token, NormalizeVisibleTextKeys(preferredKeys));
            if (!string.IsNullOrWhiteSpace(text))
                return Clean(text);

            return CleanToken(token);
        }

        public static string StripReasoningBlocks(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            var cleaned = text;
            foreach (var tag in ReasoningTags)
            {
                cleaned = Regex.Replace(
                    cleaned,
                    "<\\s*" + tag + "\\b[^>]*>.*?</\\s*" + tag + "\\s*>",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            cleaned = StripUnclosedReasoningBlocks(cleaned);
            return Regex.Replace(
                cleaned,
                "</\\s*(think|thinking|reasoning)\\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string StripUnclosedReasoningBlocks(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            var cleaned = text;
            while (true)
            {
                var match = Regex.Match(
                    cleaned,
                    "<\\s*(think|thinking|reasoning)\\b[^>]*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!match.Success)
                    return cleaned;

                var resumeIndex = FindResumeIndex(cleaned, match.Index + match.Length);
                cleaned = resumeIndex >= 0
                    ? cleaned.Substring(0, match.Index) + cleaned.Substring(resumeIndex)
                    : cleaned.Substring(0, match.Index);
            }
        }

        private static int FindResumeIndex(string text, int startIndex)
        {
            if (string.IsNullOrEmpty(text) || startIndex >= text.Length)
                return -1;

            var tail = text.Substring(startIndex);
            var markdownHeading = Regex.Match(tail, @"(^|[\r\n]+)[ \t]*(?=#{1,6}[ \t]+)");
            if (markdownHeading.Success)
                return startIndex + markdownHeading.Index + markdownHeading.Length;

            var jsonStart = Regex.Match(tail, @"(^|[\r\n]+)[ \t]*(?=[\{\[])");
            if (jsonStart.Success)
                return startIndex + jsonStart.Index + jsonStart.Length;

            return -1;
        }

        private static string StripReasoningFieldsFromJsonLike(string text)
        {
            if (!TryParseJsonLike(text, out var token))
                return text ?? string.Empty;

            return CleanToken(token);
        }

        private static bool TryParseJsonLike(string text, out JToken token)
        {
            token = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var trimmed = text.Trim();
            if (!((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)) ||
                  (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))))
            {
                return false;
            }

            try
            {
                token = JToken.Parse(trimmed);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string[] NormalizeVisibleTextKeys(string[] preferredKeys)
        {
            if (preferredKeys == null || preferredKeys.Length == 0)
                return DefaultVisibleTextKeys;

            var keys = new List<string>();
            foreach (var key in preferredKeys)
            {
                if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key))
                    keys.Add(key);
            }

            foreach (var key in DefaultVisibleTextKeys)
            {
                if (!keys.Contains(key))
                    keys.Add(key);
            }

            return keys.ToArray();
        }

        private static string TryExtractVisibleText(JToken token, string[] preferredKeys)
        {
            if (token == null || token.Type == JTokenType.Null)
                return string.Empty;

            if (token.Type == JTokenType.String)
            {
                var value = token.Value<string>();
                if (TryParseJsonLike(value, out var parsed))
                    return TryExtractVisibleText(parsed, preferredKeys);

                return StripReasoningBlocks(value);
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                foreach (var key in preferredKeys)
                {
                    if (string.IsNullOrWhiteSpace(key) || IsReasoningFieldName(key))
                        continue;

                    var value = obj[key];
                    var text = TryExtractVisibleText(value, preferredKeys);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }

                foreach (var property in obj.Properties())
                {
                    if (IsReasoningFieldName(property.Name))
                        continue;

                    var text = TryExtractVisibleText(property.Value, preferredKeys);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }

                return string.Empty;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in (JArray)token)
                {
                    var text = TryExtractVisibleText(item, preferredKeys);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }

                return string.Empty;
            }

            return token.Type == JTokenType.Integer ||
                   token.Type == JTokenType.Float ||
                   token.Type == JTokenType.Boolean
                ? string.Empty
                : token.ToString(Formatting.None);
        }
    }
}
