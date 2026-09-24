namespace MedimapReferralLab;

/// <summary>
/// 試作を使える人。appsettings.Development.json（リポジトリに入れない）に書く。
/// 顧客や一般の社員の目に触れさせない（12_テスト版の仕様.md 2章・5章）。
/// </summary>
public sealed class LabAuthOptions
{
    public const string SectionName = "LabAuth";

    public List<LabUser> Users { get; set; } = [];
}

public sealed class LabUser
{
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
}
