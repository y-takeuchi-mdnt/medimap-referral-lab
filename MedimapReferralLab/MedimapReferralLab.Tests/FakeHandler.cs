using System.Net;

namespace MedimapReferralLab.Tests;

/// <summary>Azure OpenAI の代わりに決まった応答を返す。</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await respond(request, cancellationToken);
    }

    public static FakeHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    /// <summary>Chat Completions の応答の形で content を包む。</summary>
    public static string Completion(string content, string finishReason = "stop", int cached = 0) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            model = "gpt-test-2026-01-01",
            system_fingerprint = "fp_test",
            choices = new[] { new { index = 0, finish_reason = finishReason, message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = 40000, completion_tokens = 300, total_tokens = 40300, prompt_tokens_details = new { cached_tokens = cached } },
        });
}
