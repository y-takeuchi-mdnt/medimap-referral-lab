using System.Text.RegularExpressions;
using MedimapReferralLab.Core.Catalog;

namespace MedimapReferralLab.Core.Ai;

/// <summary>
/// AIの出力の検証（5段階）。すべて検索の前に通す。AIを信用しない。
/// 設計/04_検索キーカタログ.md 9章・設計/08_生成AIの呼び出し.md 7章。
/// <code>
/// 1 カタログに存在するIDか                 → 捨てる
/// 2 検索対象=1 か                          → 捨てる（歯科・ノイズ・廃止）
/// 3 必須に挙げたIDが 必須可=1 か           → 加点に格下げ
/// 4 departments のIDが診療科目のキーか     → 加点に回す
/// 5 診療科目のキーが required / bonus にないか → departments に移す
/// </code>
/// 加えて、IDの形・重複・件数の上限（スキーマで縛れなかったときのため）をコードで見る（段階0）。
/// </summary>
public sealed partial class OutputValidator(SearchKeyCatalog catalog)
{
    public const string ListDepartments = "departments";
    public const string ListRequired = "required";
    public const string ListBonus = "bonus";
    public const string ListUnmatched = "unmatched";

    public ReferralDraft Validate(RawDraft raw, ReferralRequest request)
    {
        var issues = new List<ValidationIssue>();
        var departments = new List<DraftCondition>();
        var required = new List<DraftCondition>();
        var downgraded = new List<DraftCondition>();
        var bonus = new List<DraftCondition>();
        var fromDepartments = new List<DraftCondition>();

        // 段階1・2（と形式）を通ったキーを返す。落ちたら issue を積んで null
        SearchKey? Resolve(RawKey item, string list)
        {
            var id = item.Key?.Trim() ?? "";
            if (!KeyPattern().IsMatch(id))
            {
                issues.Add(new(0, id, list, ValidationAction.Dropped, "IDの形が正しくない"));
                return null;
            }
            var key = catalog.Find(id);
            if (key is null)
            {
                issues.Add(new(1, id, list, ValidationAction.Dropped, "AIが存在しない項目を挙げた"));
                return null;
            }
            if (!key.Searchable)
            {
                issues.Add(new(2, id, list, ValidationAction.Dropped, $"検索対象外（{key.LabelForPrompt}。歯科・ノイズ・廃止）"));
                return null;
            }
            return key;
        }

        foreach (var item in raw.Departments ?? [])
        {
            if (Resolve(item, ListDepartments) is not { } key) continue;
            if (!key.IsDepartment)
            {
                issues.Add(new(4, key.Id, ListDepartments, ValidationAction.Moved, $"診療科目ではない（{key.LabelForPrompt}）ので加点に回した"));
                fromDepartments.Add(new(key, Note: "診療科の候補から移した"));
                continue;
            }
            departments.Add(new(key));
        }

        foreach (var item in raw.Required ?? [])
        {
            if (Resolve(item, ListRequired) is not { } key) continue;
            if (key.IsDepartment)
            {
                issues.Add(new(5, key.Id, ListRequired, ValidationAction.Moved, $"診療科目（{key.LabelForPrompt}）なので診療科の候補に移した"));
                departments.Add(new(key, Note: "必須条件から移した"));
                continue;
            }
            if (!key.RequirableAsMust)
            {
                issues.Add(new(3, key.Id, ListRequired, ValidationAction.Downgraded, $"必須にできない（{key.LabelForPrompt}）ので加点に格下げした"));
                downgraded.Add(new(key, item.Reason, Note: "必須から格下げ"));
                continue;
            }
            required.Add(new(key, item.Reason));
        }

        foreach (var item in raw.Bonus ?? [])
        {
            if (Resolve(item, ListBonus) is not { } key) continue;
            if (key.IsDepartment)
            {
                issues.Add(new(5, key.Id, ListBonus, ValidationAction.Moved, $"診療科目（{key.LabelForPrompt}）なので診療科の候補に移した"));
                departments.Add(new(key, Note: "加点条件から移した"));
                continue;
            }
            bonus.Add(new(key));
        }

        // 格下げしたものはAIが必須と見た＝加点の中では優先度が高いので先頭に置く
        var finalDepartments = Distinct(departments, ListDepartments, [], issues);
        var finalRequired = Distinct(required, ListRequired, [], issues);
        var finalBonus = Distinct([.. downgraded, .. bonus, .. fromDepartments], ListBonus,
            finalRequired.Select(c => c.KeyId).ToHashSet(), issues);

        var unmatched = (raw.Unmatched ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

        return new ReferralDraft(
            new DraftProfile(request.AgeBand, request.Sex, request.CityCode, request.FacilityScope,
                Clean(raw.Profile?.Conditions), Clean(raw.Profile?.CareNeeds), Clean(raw.Profile?.Context)),
            Limit(finalDepartments, OutputLimits.Departments, ListDepartments, issues),
            Limit(finalRequired, OutputLimits.Required, ListRequired, issues),
            Limit(finalBonus, OutputLimits.Bonus, ListBonus, issues),
            LimitStrings(unmatched, OutputLimits.Unmatched, issues),
            issues);
    }

    private static List<DraftCondition> Distinct(IEnumerable<DraftCondition> items, string list, HashSet<string> alreadyUsed, List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(alreadyUsed);
        var result = new List<DraftCondition>();
        foreach (var c in items)
        {
            if (seen.Add(c.KeyId)) { result.Add(c); continue; }
            issues.Add(new(0, c.KeyId, list, ValidationAction.Dropped,
                alreadyUsed.Contains(c.KeyId) ? "必須条件と重複" : "同じ枠で重複"));
        }
        return result;
    }

    private static List<DraftCondition> Limit(List<DraftCondition> items, int max, string list, List<ValidationIssue> issues)
    {
        foreach (var c in items.Skip(max))
            issues.Add(new(0, c.KeyId, list, ValidationAction.Dropped, $"件数の上限（{max}件）を超えた"));
        return items.Take(max).ToList();
    }

    private static List<string> LimitStrings(List<string> items, int max, List<ValidationIssue> issues)
    {
        foreach (var s in items.Skip(max))
            issues.Add(new(0, "", ListUnmatched, ValidationAction.Dropped, $"件数の上限（{max}件）を超えた: {s}"));
        return items.Take(max).ToList();
    }

    private static List<string> Clean(List<string>? items) =>
        (items ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

    [GeneratedRegex(OutputLimits.KeyPattern)]
    private static partial Regex KeyPattern();
}
