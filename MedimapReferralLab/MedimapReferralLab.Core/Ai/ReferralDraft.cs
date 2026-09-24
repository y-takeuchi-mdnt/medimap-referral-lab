using System.Text.Json.Serialization;
using MedimapReferralLab.Core.Catalog;

namespace MedimapReferralLab.Core.Ai;

/// <summary>
/// 構造化プロフィール。④の画面に出し、保存するのはこちら（自由文は保存しない）。
/// ageBand / sex / cityCode / facilityScope はセレクタの値をそのまま入れる。
/// </summary>
public sealed record DraftProfile(
    string AgeBand,
    string Sex,
    string CityCode,
    string FacilityScope,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> CareNeeds,
    IReadOnlyList<string> Context);

/// <summary>検証を通った条件1件。</summary>
public sealed record DraftCondition(
    [property: JsonIgnore] SearchKey Key,
    string? Reason = null,
    string? Note = null)
{
    public string KeyId => Key.Id;
    public string Label => Key.LabelForPrompt;
}

public enum ValidationAction
{
    /// <summary>捨てた</summary>
    Dropped,
    /// <summary>必須から加点に格下げした（段階3）</summary>
    Downgraded,
    /// <summary>別の枠に移した（段階4・5）</summary>
    Moved,
}

/// <summary>
/// 検証で落ちた・動かしたもの。黙って捨てず、④の画面に出してログにも残す（04 9章）。
/// <para>Stage: 1〜5 は 04 9章の5段階。0 は形式（IDの形・重複・件数の上限）。</para>
/// </summary>
public sealed record ValidationIssue(int Stage, string KeyId, string FromList, ValidationAction Action, string Message);

/// <summary>検証済みの下書き。検索に渡してよいのはこれだけ。</summary>
public sealed record ReferralDraft(
    DraftProfile Profile,
    IReadOnlyList<DraftCondition> Departments,
    IReadOnlyList<DraftCondition> Required,
    IReadOnlyList<DraftCondition> Bonus,
    IReadOnlyList<string> Unmatched,
    IReadOnlyList<ValidationIssue> Issues)
{
    public bool HasAnyCondition => Departments.Count + Required.Count + Bonus.Count > 0;

    /// <summary>AIが失敗したときの、条件が空の下書き（手動検索。08 9章）。</summary>
    public static ReferralDraft Empty(ReferralRequest request, IReadOnlyList<ValidationIssue>? issues = null) => new(
        new DraftProfile(request.AgeBand, request.Sex, request.CityCode, request.FacilityScope, [], [], []),
        [], [], [], [], issues ?? []);

    /// <summary>同じ条件が出たかを比べるための署名（順序込み）。</summary>
    public string ConditionSignature =>
        $"D:{string.Join(",", Departments.Select(c => c.KeyId))}|R:{string.Join(",", Required.Select(c => c.KeyId))}|B:{string.Join(",", Bonus.Select(c => c.KeyId))}";
}
