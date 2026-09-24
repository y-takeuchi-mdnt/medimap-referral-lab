using System.Text.Json;
using System.Text.Json.Serialization;

namespace MedimapReferralLab.Core.Cases;

/// <summary>
/// 架空の症例（data/cases/架空症例.json）。回帰テストと試験ページで使う。
/// 法務の確認が済むまで、実在の患者の情報は入れない（運用/11_プライバシーと法務.md）。
/// </summary>
public sealed record FictionalCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("cityCode")] string CityCode,
    [property: JsonPropertyName("ageBand")] string AgeBand,
    [property: JsonPropertyName("sex")] string Sex,
    [property: JsonPropertyName("facilityScope")] string FacilityScope,
    [property: JsonPropertyName("freeText")] string FreeText,
    [property: JsonPropertyName("観点")] string Viewpoint)
{
    public ReferralRequest ToRequest() => new()
    {
        CityCode = CityCode,
        AgeBand = AgeBand,
        Sex = Sex,
        FacilityScope = FacilityScope,
        FreeText = FreeText,
    };

    public const string DefaultFileName = "架空症例.json";

    public static IReadOnlyList<FictionalCase> Load(string path)
    {
        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<CaseFile>(stream) ?? throw new InvalidDataException(path);
        return file.Cases;
    }

    private sealed record CaseFile([property: JsonPropertyName("cases")] List<FictionalCase> Cases);
}
