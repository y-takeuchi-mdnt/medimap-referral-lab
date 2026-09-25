namespace MedimapReferralLab.Core.Ai;

/// <summary>
/// 出力の件数の上限。設計/08_生成AIの呼び出し.md 7章。
/// maxItems は必須を増やしすぎないための歯止めでもある。スキーマとコードの両方で使う。
/// </summary>
public static class OutputLimits
{
    public const int Departments = 5;
    public const int Required = 6;
    /// <summary>
    /// 20→10（プロンプト第2版。2026-09-25）。第1版は見出しの下をまとめて選び、加点が最大17件・再現性が低かった。
    /// </summary>
    public const int Bonus = 10;
    public const int Unmatched = 3;
    public const string KeyPattern = "^[MC][0-9]{4}$";
}
