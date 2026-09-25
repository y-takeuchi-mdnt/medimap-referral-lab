namespace MedimapReferralLab.Core.Ai;

public enum AiCallStatus
{
    Success,
    /// <summary>時間内に返らなかった</summary>
    Timeout,
    /// <summary>レート制限（429）</summary>
    RateLimited,
    /// <summary>Azure OpenAI の障害・認証エラーなど</summary>
    ServiceError,
    /// <summary>返ってきたが形が合わない・拒否・途中で切れた</summary>
    SchemaViolation,
}

/// <summary>
/// 1回の呼び出しの記録。手順1で測るもの（所要時間・トークン数・キャッシュ）をここに集める。
/// 自由文は持たない。
/// </summary>
public sealed record AiCallResult
{
    public required AiCallStatus Status { get; init; }
    public required double ElapsedMs { get; init; }

    public int? PromptTokens { get; init; }
    /// <summary>プロンプトキャッシュに当たった入力トークン数（usage.prompt_tokens_details.cached_tokens）。</summary>
    public int? CachedTokens { get; init; }
    public int? CompletionTokens { get; init; }

    /// <summary>応答に書かれたモデル名（バージョン込み）。変わったら精度評価をやり直す（08 8章）。</summary>
    public string? Model { get; init; }
    public string? SystemFingerprint { get; init; }
    public string? FinishReason { get; init; }

    /// <summary>AIが返した JSON 本文。</summary>
    public string? Content { get; init; }

    public int? HttpStatus { get; init; }

    /// <summary>429 のときに Azure が返した「何秒待てばよいか」（retry-after）。</summary>
    public double? RetryAfterSeconds { get; init; }
    public string? Error { get; init; }

    public bool CacheHit => CachedTokens > 0;
}
