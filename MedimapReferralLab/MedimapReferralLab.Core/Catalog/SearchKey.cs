namespace MedimapReferralLab.Core.Catalog;

public enum SearchKeyOrigin
{
    /// <summary>メディマップ（M）</summary>
    Medimap,
    /// <summary>共通設問（C）</summary>
    CommonSurvey,
}

/// <summary>
/// 検索キー1件。設計/04_検索キーカタログ.md の列をそのまま持つ。
/// </summary>
public sealed record SearchKey
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required SearchKeyOrigin Origin { get; init; }

    /// <summary>メディマップは「ソース」、共通設問は「大区分」。</summary>
    public required string Source { get; init; }

    /// <summary>メディマップは「分類」、共通設問は「中区分」。</summary>
    public required string Category { get; init; }

    /// <summary>検索対象=1。0 は歯科・ノイズ・廃止。</summary>
    public required bool Searchable { get; init; }

    /// <summary>必須可=1。共通設問は常に false（回答が0件のため。02_機能概要.md 7章）。</summary>
    public required bool RequirableAsMust { get; init; }

    /// <summary>同名の語彙が別ソースにあるか（メディマップのみ）。あればプロンプトでソース名を添える。</summary>
    public bool HasSameNameInOtherSource { get; init; }

    public bool IsDepartment => Origin == SearchKeyOrigin.Medimap && Source == SearchKeyCatalog.DepartmentSource;

    /// <summary>プロンプトと画面に出す表示名。同名があるときはソース名を添える（04 8章）。</summary>
    public string LabelForPrompt => HasSameNameInOtherSource ? $"{DisplayName}（{Source}）" : DisplayName;
}
