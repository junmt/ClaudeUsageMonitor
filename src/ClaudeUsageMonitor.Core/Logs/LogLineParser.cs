using System.Text.Json;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Logs;

/// <summary>
/// Claude Code のセッションログ（jsonl）1行から usage を取り出す。
/// 会話本文は読み取らず、集計に必要なメタデータのみを使う。
/// </summary>
public static class LogLineParser
{
    public static bool TryParse(string line, out UsageRecord record)
    {
        record = null!;
        if (!line.Contains("\"usage\"", StringComparison.Ordinal))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
                return false;

            var messageId = GetString(message, "id");
            if (string.IsNullOrEmpty(messageId)
                || !root.TryGetProperty("timestamp", out var tsElement)
                || !tsElement.TryGetDateTimeOffset(out var timestamp))
                return false;

            var model = GetString(message, "model");
            if (model == "<synthetic>")
                return false;

            var cacheCreation = GetLong(usage, "cache_creation_input_tokens");
            long cache5m = cacheCreation, cache1h = 0;
            if (usage.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
            {
                cache5m = GetLong(cc, "ephemeral_5m_input_tokens");
                cache1h = GetLong(cc, "ephemeral_1h_input_tokens");
            }

            var webSearch = usage.TryGetProperty("server_tool_use", out var stu) && stu.ValueKind == JsonValueKind.Object
                ? GetLong(stu, "web_search_requests")
                : 0;

            record = new UsageRecord(
                MessageId: messageId,
                RequestId: GetString(root, "requestId") ?? "",
                Timestamp: timestamp,
                SessionId: GetString(root, "sessionId"),
                ProjectPath: GetString(root, "cwd"),
                Model: model,
                IsSidechain: root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True,
                InputTokens: GetLong(usage, "input_tokens"),
                OutputTokens: GetLong(usage, "output_tokens"),
                CacheWrite5mTokens: cache5m,
                CacheWrite1hTokens: cache1h,
                CacheReadTokens: GetLong(usage, "cache_read_input_tokens"),
                WebSearchRequests: webSearch);

            return record.TotalTokens > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;
}
