using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MedimapReferralLab.Core.Ai;

/// <summary>
/// Azure OpenAI の呼び出し（Chat Completions の REST を直接叩く）。設計/08_生成AIの呼び出し.md 8章。
/// <list type="bullet">
/// <item>temperature 0</item>
/// <item>Structured Outputs（JSON Schema・strict）</item>
/// <item>ストリーミングなし</item>
/// <item>タイムアウトで切る。再試行しない（9章）</item>
/// </list>
/// SDK ではなく REST にしているのは、usage（キャッシュに当たったトークン数）と
/// system_fingerprint をそのまま記録したいのと、NuGet への依存を増やさないため。
/// </summary>
public sealed class ReferralAiClient(HttpClient http, AzureOpenAiOptions options)
{
    public AzureOpenAiOptions Options => options;

    public async Task<AiCallResult> CallAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        var url = $"{options.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(options.Deployment)}/chat/completions?api-version={Uri.EscapeDataString(options.ApiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // 先に文字列にして Content-Length を付ける（チャンク転送にしない）
            Content = new StringContent(BuildBody(systemPrompt, userMessage).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("api-key", options.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new AiCallResult
                {
                    Status = response.StatusCode == HttpStatusCode.TooManyRequests ? AiCallStatus.RateLimited : AiCallStatus.ServiceError,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    HttpStatus = (int)response.StatusCode,
                    Error = Truncate(body, 500),
                };
            }
            return ParseResponse(body, sw.Elapsed.TotalMilliseconds, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiCallResult
            {
                Status = AiCallStatus.Timeout,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Error = $"{options.TimeoutSeconds}秒で打ち切った",
            };
        }
        catch (HttpRequestException ex)
        {
            return new AiCallResult
            {
                Status = AiCallStatus.ServiceError,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Error = ex.Message,
            };
        }
    }

    internal JsonObject BuildBody(string systemPrompt, string userMessage)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userMessage }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = OutputSchema.Name,
                    ["strict"] = true,
                    ["schema"] = OutputSchema.Build(options.SchemaUsesPatternAndMaxItems),
                },
            },
            ["stream"] = false,
        };
        if (options.SendTemperature) body["temperature"] = 0;
        body[options.UseMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = options.MaxOutputTokens;
        return body;
    }

    internal static AiCallResult ParseResponse(string body, double elapsedMs, int httpStatus)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            int? GetInt(JsonElement e, params string[] path)
            {
                foreach (var p in path)
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
                return e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;
            }
            string? GetString(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var usage = root.TryGetProperty("usage", out var u) ? u : default;
            var choice = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 ? choices[0] : default;
            var message = choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("message", out var m) ? m : default;
            var finishReason = GetString(choice, "finish_reason");
            var content = GetString(message, "content");
            var refusal = GetString(message, "refusal");

            var ok = finishReason == "stop" && !string.IsNullOrEmpty(content) && refusal is null;
            return new AiCallResult
            {
                Status = ok ? AiCallStatus.Success : AiCallStatus.SchemaViolation,
                ElapsedMs = elapsedMs,
                HttpStatus = httpStatus,
                PromptTokens = GetInt(usage, "prompt_tokens"),
                CachedTokens = GetInt(usage, "prompt_tokens_details", "cached_tokens"),
                CompletionTokens = GetInt(usage, "completion_tokens"),
                Model = GetString(root, "model"),
                SystemFingerprint = GetString(root, "system_fingerprint"),
                FinishReason = finishReason,
                Content = content,
                Error = ok ? null : refusal is not null ? $"拒否: {refusal}" : $"finish_reason={finishReason}",
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new AiCallResult
            {
                Status = AiCallStatus.SchemaViolation,
                ElapsedMs = elapsedMs,
                HttpStatus = httpStatus,
                Error = $"応答を読めない: {ex.Message}",
            };
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
