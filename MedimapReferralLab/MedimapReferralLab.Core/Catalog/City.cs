using MedimapReferralLab.Core.Text;

namespace MedimapReferralLab.Core.Catalog;

public sealed record City(string Code, string Name, string PrefectureCode, string PrefectureName)
{
    public string FullName => $"{PrefectureName}{Name}";
}

/// <summary>
/// 市区町村の一覧。手順1ではDBを使わないので `市区町村_一覧.csv` から読む。
/// 手順2以降は ReferralCities に置き換える。
/// </summary>
public sealed class CityCatalog
{
    public const string FileName = "市区町村_一覧.csv";

    private readonly Dictionary<string, City> _byCode;

    public IReadOnlyList<City> Cities { get; }

    public CityCatalog(IEnumerable<City> cities)
    {
        Cities = cities.ToList();
        _byCode = Cities.ToDictionary(c => c.Code, StringComparer.Ordinal);
    }

    public City? Find(string code) => _byCode.GetValueOrDefault(code);

    public IEnumerable<(string Code, string Name)> Prefectures =>
        Cities.Select(c => (c.PrefectureCode, c.PrefectureName)).Distinct().OrderBy(p => p.PrefectureCode);

    public static CityCatalog LoadFromDirectory(string directory) =>
        new(CsvReader.ReadFile(Path.Combine(directory, FileName))
            .Select(r => new City(r["市区町村コード"], r["市区町村名"], r["都道府県コード"], r["都道府県名"])));
}
