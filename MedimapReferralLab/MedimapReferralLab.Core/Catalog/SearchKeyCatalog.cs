using System.Collections.Frozen;
using MedimapReferralLab.Core.Text;

namespace MedimapReferralLab.Core.Catalog;

/// <summary>
/// 検索キーカタログ（メディマップ＋共通設問）。設計/04_検索キーカタログ.md。
/// </summary>
public sealed class SearchKeyCatalog
{
    public const string MedimapFileName = "検索キーカタログ_メディマップ.csv";
    public const string CommonSurveyFileName = "検索キーカタログ_共通設問.csv";
    public const string DepartmentSource = "診療科目";

    private readonly FrozenDictionary<string, SearchKey> _byId;

    public IReadOnlyList<SearchKey> MedimapKeys { get; }
    public IReadOnlyList<SearchKey> CommonSurveyKeys { get; }

    public SearchKeyCatalog(IEnumerable<SearchKey> medimapKeys, IEnumerable<SearchKey> commonSurveyKeys)
    {
        MedimapKeys = medimapKeys.ToList();
        CommonSurveyKeys = commonSurveyKeys.ToList();
        _byId = MedimapKeys.Concat(CommonSurveyKeys).ToFrozenDictionary(k => k.Id, StringComparer.Ordinal);
    }

    public SearchKey? Find(string id) => _byId.GetValueOrDefault(id);

    public IEnumerable<SearchKey> All => MedimapKeys.Concat(CommonSurveyKeys);

    public static SearchKeyCatalog LoadFromDirectory(string directory) =>
        new(LoadMedimap(Path.Combine(directory, MedimapFileName)),
            LoadCommonSurvey(Path.Combine(directory, CommonSurveyFileName)));

    public static IEnumerable<SearchKey> LoadMedimap(string path) =>
        CsvReader.ReadFile(path).Select(r => new SearchKey
        {
            Id = r["検索キーID"],
            DisplayName = r["表示名"],
            Origin = SearchKeyOrigin.Medimap,
            Source = r["ソース"],
            Category = r["分類"],
            // 状態=廃止 は検索対象=0 になっている前提だが、念のため両方見る（04 5章）
            Searchable = r["検索対象"] == "1" && r["状態"] != "廃止",
            RequirableAsMust = r["必須可"] == "1" && r["検索対象"] == "1" && r["状態"] != "廃止",
            HasSameNameInOtherSource = !string.IsNullOrEmpty(r["同名"]),
        });

    public static IEnumerable<SearchKey> LoadCommonSurvey(string path) =>
        CsvReader.ReadFile(path).Select(r => new SearchKey
        {
            Id = r["検索キーID"],
            DisplayName = r["表示名"],
            Origin = SearchKeyOrigin.CommonSurvey,
            Source = r["大区分"],
            Category = r["中区分"],
            Searchable = r["検索対象"] == "1",
            RequirableAsMust = false,
        });
}
