using System.Text.Json;
using System.Text.Json.Serialization;

namespace MedimapReferralLab.Core.Ai;

/// <summary>AIが返したままの形（<see cref="OutputSchema"/>）。検証前なので信用しない。</summary>
public sealed record RawDraft(
    [property: JsonPropertyName("profile")] RawProfile? Profile,
    [property: JsonPropertyName("departments")] List<RawKey>? Departments,
    [property: JsonPropertyName("required")] List<RawKey>? Required,
    [property: JsonPropertyName("bonus")] List<RawKey>? Bonus,
    [property: JsonPropertyName("unmatched")] List<string>? Unmatched)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    /// <summary>形が合わなければ null（スキーマ違反。08 9章で手動へ落とす）。</summary>
    public static RawDraft? TryParse(string json)
    {
        try
        {
            var draft = JsonSerializer.Deserialize<RawDraft>(json, Options);
            if (draft?.Profile is null || draft.Departments is null || draft.Required is null
                || draft.Bonus is null || draft.Unmatched is null)
                return null;
            return draft;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record RawProfile(
    [property: JsonPropertyName("conditions")] List<string>? Conditions,
    [property: JsonPropertyName("careNeeds")] List<string>? CareNeeds,
    [property: JsonPropertyName("context")] List<string>? Context);

public sealed record RawKey(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("reason")] string? Reason = null);
