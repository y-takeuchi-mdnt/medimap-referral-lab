namespace MedimapReferralLab.Core;

/// <summary>
/// 利用者の入力。市区町村・年齢帯・性別・医科／歯科はセレクタの値で、AIに決めさせない
/// （設計/08_生成AIの呼び出し.md 4章）。AIの仕事は自由文だけ。
/// </summary>
public sealed record ReferralRequest
{
    public required string CityCode { get; init; }
    public required string AgeBand { get; init; }
    public required string Sex { get; init; }
    public string FacilityScope { get; init; } = FacilityScopes.Medical;

    /// <summary>症状・状態・ケアの必要。保存しない。</summary>
    public required string FreeText { get; init; }
}

public static class FacilityScopes
{
    public const string Medical = "医科";
    public const string Dental = "歯科";
    public static readonly IReadOnlyList<string> All = [Medical, Dental];
}

/// <summary>年齢帯は10歳刻み（08 4章。「70代で十分」は確定事項）。</summary>
public static class AgeBands
{
    public static readonly IReadOnlyList<string> All =
        ["10歳未満", "10代", "20代", "30代", "40代", "50代", "60代", "70代", "80代", "90代", "100歳以上"];
}

public static class Sexes
{
    public static readonly IReadOnlyList<string> All = ["男", "女", "不明"];
}
