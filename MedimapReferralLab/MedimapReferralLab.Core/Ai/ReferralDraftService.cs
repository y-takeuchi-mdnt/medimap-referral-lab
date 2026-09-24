namespace MedimapReferralLab.Core.Ai;

public enum DraftStatus
{
    /// <summary>条件が出た</summary>
    Success,
    /// <summary>個人情報のガードで止めた。書き直してもらう（送っていない）</summary>
    BlockedByGuard,
    /// <summary>AIが使えない・失敗した。条件が空の④を出す（手動検索。08 9章）</summary>
    FallbackToManual,
}

public sealed record DraftResult
{
    public required DraftStatus Status { get; init; }
    public required ReferralDraft Draft { get; init; }
    public required GuardResult Guard { get; init; }

    /// <summary>AIを呼んだときだけ入る。</summary>
    public AiCallResult? Call { get; init; }

    /// <summary>手動へ落とした理由（画面にそのまま出す文言）。</summary>
    public string? FallbackMessage { get; init; }

    /// <summary>静的プロンプトのハッシュ。変わったらキャッシュが効かない。</summary>
    public required string StaticPromptHash { get; init; }

    /// <summary>予算（AzureOpenAiOptions.BudgetSeconds）を超えたか。</summary>
    public bool OverBudget { get; init; }
}

/// <summary>
/// 自由文 → ガード → AI呼び出し（1回だけ） → 検証 → 下書き。
/// 失敗はすべて手動検索へ落とす。再試行しない（設計/08_生成AIの呼び出し.md 9章）。
/// </summary>
public sealed class ReferralDraftService(
    PromptBuilder prompts,
    OutputValidator validator,
    ReferralAiClient client,
    ReferralSearchOptions searchOptions)
{
    public string StaticPromptHash => prompts.StaticPromptHash;

    public bool AiAvailable => searchOptions.AiEnabled && client.Options.IsConfigured;

    public async Task<DraftResult> CreateDraftAsync(ReferralRequest request, CancellationToken cancellationToken = default)
    {
        var guard = PersonalInfoGuard.Check(request.FreeText);

        DraftResult Fallback(string message, AiCallResult? call = null, ReferralDraft? draft = null) => new()
        {
            Status = DraftStatus.FallbackToManual,
            Draft = draft ?? ReferralDraft.Empty(request),
            Guard = guard,
            Call = call,
            FallbackMessage = message,
            StaticPromptHash = prompts.StaticPromptHash,
            OverBudget = call is not null && call.ElapsedMs > client.Options.BudgetSeconds * 1000,
        };

        if (!searchOptions.AiEnabled)
            return Fallback("AIは止めてあります（ReferralSearch:AiEnabled=false）。条件を手で組んでください。");
        if (!client.Options.IsConfigured)
            return Fallback("Azure OpenAI の接続設定がありません。条件を手で組んでください。");

        if (guard.IsBlocked)
        {
            return new DraftResult
            {
                Status = DraftStatus.BlockedByGuard,
                Draft = ReferralDraft.Empty(request),
                Guard = guard,
                StaticPromptHash = prompts.StaticPromptHash,
            };
        }
        if (string.IsNullOrWhiteSpace(request.FreeText))
            return Fallback("自由文が空です。条件を手で組んでください。");

        var call = await client.CallAsync(prompts.StaticPrompt, prompts.BuildUserMessage(request), cancellationToken);

        switch (call.Status)
        {
            case AiCallStatus.Timeout:
                return Fallback("時間内に条件を作れませんでした。条件を手で組んでください。", call);
            case AiCallStatus.RateLimited:
                return Fallback("AIが混み合っています（レート制限）。条件を手で組んでください。", call);
            case AiCallStatus.ServiceError:
                return Fallback("AIを呼び出せませんでした。条件を手で組んでください。", call);
            case AiCallStatus.SchemaViolation:
                return Fallback("AIの出力の形が正しくありませんでした。条件を手で組んでください。", call);
        }

        var raw = RawDraft.TryParse(call.Content!);
        if (raw is null)
            return Fallback("AIの出力の形が正しくありませんでした。条件を手で組んでください。", call with { Status = AiCallStatus.SchemaViolation });

        var draft = validator.Validate(raw, request);
        if (!draft.HasAnyCondition)
        {
            var dropped = draft.Issues.Count(i => i.Action == ValidationAction.Dropped);
            return Fallback(dropped > 0
                    ? $"AIが挙げた条件が検証ですべて落ちました（{dropped}件）。条件を手で組んでください。"
                    : "入力から条件を作れませんでした。条件を手で組んでください。",
                call, draft);
        }

        return new DraftResult
        {
            Status = DraftStatus.Success,
            Draft = draft,
            Guard = guard,
            Call = call,
            StaticPromptHash = prompts.StaticPromptHash,
            OverBudget = call.ElapsedMs > client.Options.BudgetSeconds * 1000,
        };
    }
}
