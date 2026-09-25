using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using MedimapReferralLab.Core.Ai;
using MedimapReferralLab.Core.Cases;
using MedimapReferralLab.Core.Catalog;
using Microsoft.Extensions.Configuration;

namespace MedimapReferralLab.Tests;

/// <summary>
/// 架空の症例を Azure OpenAI に流して測る（12_テスト版の仕様.md 手順1の終わりの条件）。
/// <list type="bullet">
/// <item>呼び出し時間・入力と出力のトークン数・キャッシュが効いたか → calls.csv / summary.md</item>
/// <item>同じ症例を2回流して、同じ条件が出るか → 全件を1周してから2周目を流す（2周目はキャッシュが温まった状態）</item>
/// <item>確認役の人に「使える」「足りない」「余計」を記録してもらう → review.csv（Excel で開ける）</item>
/// </list>
/// 設定は Web プロジェクトの appsettings.json / appsettings.Development.json と環境変数から読む。
/// </summary>
internal static class LiveRegression
{
    public static async Task<int> RunAsync(string root, string[] args)
    {
        var runs = 2;
        var interval = TimeSpan.Zero;
        string[]? only = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--runs" && i + 1 < args.Length) runs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--cases" && i + 1 < args.Length) only = args[++i].Split(',', StringSplitOptions.TrimEntries);
            // 1分あたりのトークン数の枠でレート制限（429）に当たらないように、呼び出しの間を空ける（秒）
            else if (args[i] == "--interval" && i + 1 < args.Length) interval = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture));
        }

        var webDir = Path.Combine(root, "MedimapReferralLab");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(webDir, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(webDir, "appsettings.Development.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
        var aiOptions = config.GetSection(AzureOpenAiOptions.SectionName).Get<AzureOpenAiOptions>() ?? new();
        var searchOptions = config.GetSection(ReferralSearchOptions.SectionName).Get<ReferralSearchOptions>() ?? new();

        if (!searchOptions.AiEnabled || !aiOptions.IsConfigured)
        {
            Console.Error.WriteLine("""
                Azure OpenAI に送れません。次を設定してください（appsettings.Development.json か環境変数）。
                  ReferralSearch:AiEnabled = true      （ReferralSearch__AiEnabled）
                  AzureOpenAI:Endpoint / ApiKey / Deployment
                架空の症例だけで試すことを、法務に確認してから流してください（12_テスト版の仕様.md 2章）。
                """);
            return 2;
        }

        var catalog = SearchKeyCatalog.LoadFromDirectory(RepoPaths.CatalogDirectory(root));
        var cities = CityCatalog.LoadFromDirectory(RepoPaths.CatalogDirectory(root));
        var prompts = new PromptBuilder(catalog, cities);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var service = new ReferralDraftService(prompts, new OutputValidator(catalog), new ReferralAiClient(http, aiOptions), searchOptions);

        var cases = FictionalCase.Load(RepoPaths.CasesFile(root))
            .Where(c => only is null || only.Contains(c.Id))
            .ToList();

        var outDir = Path.Combine(root, "results", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(outDir, "raw"));
        Console.WriteLine($"{cases.Count}件 × {runs}回 / デプロイ {aiOptions.Deployment}（{DeploymentTypeLabel(aiOptions)}） / 間隔 {interval.TotalSeconds}秒 / 静的プロンプト {prompts.StaticPrompt.Length:N0}字 hash {prompts.StaticPromptHash}");
        Console.WriteLine($"出力先 {outDir}\n");

        var results = new List<(FictionalCase Case, int Run, DraftResult Result)>();
        var rateLimitRetries = 0;
        for (var run = 1; run <= runs; run++)
        {
            foreach (var c in cases)
            {
                if (results.Count > 0 && interval > TimeSpan.Zero) await Task.Delay(interval);
                // 回帰テストだけは、レート制限（429）に当たったら待って同じ症例を呼び直す。
                // 本番（ReferralDraftService）は設計どおり再試行しない。ここは測るための再試行
                var r = await service.CreateDraftAsync(c.ToRequest());
                for (var retry = 1; retry <= MaxRateLimitRetries && r.Call?.Status == AiCallStatus.RateLimited; retry++)
                {
                    var wait = TimeSpan.FromSeconds(Math.Max(r.Call.RetryAfterSeconds ?? 0, MinRetryWaitSeconds));
                    Console.WriteLine($"         {c.Id} レート制限。{wait.TotalSeconds:F0}秒待って呼び直す（{retry}/{MaxRateLimitRetries}）");
                    rateLimitRetries++;
                    await Task.Delay(wait);
                    r = await service.CreateDraftAsync(c.ToRequest());
                }
                results.Add((c, run, r));
                var call = r.Call;
                Console.WriteLine($"{run}回目 {c.Id} {r.Status,-16} {call?.ElapsedMs,7:F0}ms in {call?.PromptTokens} (cache {call?.CachedTokens}) out {call?.CompletionTokens}  {r.Draft.ConditionSignature}");
                await File.WriteAllTextAsync(Path.Combine(outDir, "raw", $"{c.Id}-{run}.json"), ToJson(r));
            }
        }

        await WriteCallsCsv(Path.Combine(outDir, "calls.csv"), results);
        await WriteReviewCsv(Path.Combine(outDir, "review.csv"), results, catalog);
        var summary = BuildSummary(results, aiOptions, prompts, runs)
            + $"\n## レート制限\n- 429 で待って呼び直した回数: {rateLimitRetries}回（間隔 {interval.TotalSeconds}秒）\n";
        await File.WriteAllTextAsync(Path.Combine(outDir, "summary.md"), summary);
        Console.WriteLine("\n" + summary);
        return 0;
    }

    private const int MaxRateLimitRetries = 5;
    private static readonly double MinRetryWaitSeconds = double.TryParse(Environment.GetEnvironmentVariable("LAB_MIN_RETRY_WAIT"), out var w) ? w : 20;

    private static string DeploymentTypeLabel(AzureOpenAiOptions o) =>
        string.IsNullOrWhiteSpace(o.DeploymentType) ? "種類の記載なし" : o.DeploymentType;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string ToJson(DraftResult r) => JsonSerializer.Serialize(r, JsonOptions);

    private static async Task WriteCallsCsv(string path, List<(FictionalCase Case, int Run, DraftResult Result)> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("症例,回,状態,呼び出し状態,所要ms,予算超過,入力トークン,キャッシュトークン,出力トークン,モデル,fingerprint,診療科,必須,加点,unmatched,検証で落ちた,検証で動かした,条件の署名");
        foreach (var (c, run, r) in results)
        {
            var call = r.Call;
            sb.AppendLine(Csv(c.Id, run, r.Status, call?.Status, call?.ElapsedMs.ToString("F0", CultureInfo.InvariantCulture), r.OverBudget ? 1 : 0,
                call?.PromptTokens, call?.CachedTokens, call?.CompletionTokens, call?.Model, call?.SystemFingerprint,
                r.Draft.Departments.Count, r.Draft.Required.Count, r.Draft.Bonus.Count, r.Draft.Unmatched.Count,
                r.Draft.Issues.Count(i => i.Action == ValidationAction.Dropped),
                r.Draft.Issues.Count(i => i.Action != ValidationAction.Dropped),
                r.Draft.ConditionSignature));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>確認役の人が埋める表。1回目の結果を出し、2回目と違えば印を付ける。</summary>
    private static async Task WriteReviewCsv(string path, List<(FictionalCase Case, int Run, DraftResult Result)> results, SearchKeyCatalog catalog)
    {
        static string Names(IEnumerable<DraftCondition> items, bool withReason = false) => string.Join("\n",
            items.Select(c => $"{c.KeyId} {c.Label}" + (withReason && c.Reason is not null ? $"：{c.Reason}" : "") + (c.Note is not null ? $"（{c.Note}）" : "")));

        var sb = new StringBuilder();
        sb.AppendLine("症例,題,市区町村,年齢帯,性別,医科／歯科,自由文,観点,状態,構造化プロフィール,診療科の候補,必須条件,加点条件,拾えなかったこと,検証で落ちた・動かした,2回目と同じか,判定（使える／足りない／余計）,足りない条件,余計な条件,コメント");
        foreach (var group in results.GroupBy(r => r.Case.Id))
        {
            var first = group.First();
            var (c, _, r) = first;
            var d = r.Draft;
            var same = group.Count() < 2 ? "" :
                group.All(x => x.Result.Draft.ConditionSignature == d.ConditionSignature) ? "同じ" : "違う";
            var profile = $"疾患・病態: {string.Join("、", d.Profile.Conditions)}\nケア: {string.Join("、", d.Profile.CareNeeds)}\n生活: {string.Join("、", d.Profile.Context)}";
            var issues = string.Join("\n", d.Issues.Select(i => $"[{i.Stage}] {i.FromList} {i.KeyId} {i.Message}"));
            var status = r.Status == DraftStatus.Success ? "条件あり" : $"{r.Status}: {r.FallbackMessage}";
            sb.AppendLine(Csv(c.Id, c.Title, c.CityCode, c.AgeBand, c.Sex, c.FacilityScope, c.FreeText, c.Viewpoint, status, profile,
                Names(d.Departments), Names(d.Required, withReason: true), Names(d.Bonus, withReason: true),
                string.Join("\n", d.Unmatched), issues, same, "", "", "", ""));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string BuildSummary(List<(FictionalCase Case, int Run, DraftResult Result)> results, AzureOpenAiOptions options, PromptBuilder prompts, int runs)
    {
        var calls = results.Where(r => r.Result.Call is not null).Select(r => (r.Run, Call: r.Result.Call!)).ToList();
        var ms = calls.Select(c => c.Call.ElapsedMs).OrderBy(x => x).ToList();
        double Pct(double p) => ms.Count == 0 ? 0 : ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(p * ms.Count) - 1)];

        var sb = new StringBuilder();
        sb.AppendLine($"# 手順1 実測 {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"- デプロイ: {options.Deployment}（{DeploymentTypeLabel(options)}） / API {options.ApiVersion} / モデル: {string.Join(", ", calls.Select(c => c.Call.Model).Distinct())}");
        sb.AppendLine($"- fingerprint: {string.Join(", ", calls.Select(c => c.Call.SystemFingerprint).Distinct())}");
        sb.AppendLine($"- プロンプト: 第{PromptBuilder.Version}版 / 静的部分 {prompts.StaticPrompt.Length:N0}字（hash {prompts.StaticPromptHash}）");
        if (options.IsGlobalDeployment)
            sb.AppendLine("- **グローバル標準で測った値。**処理の場所とレート制限が本実装（Japan East・標準）と違うので、時間とキャッシュは参考値");
        sb.AppendLine($"- 症例 {results.Select(r => r.Case.Id).Distinct().Count()}件 × {runs}回、呼び出し {calls.Count}回");
        sb.AppendLine();
        sb.AppendLine("## 呼び出し時間");
        sb.AppendLine($"| 中央値 | p90 | 最大 | 予算（{options.BudgetSeconds}秒）超過 |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| {Pct(0.5):F0}ms | {Pct(0.9):F0}ms | {(ms.Count > 0 ? ms[^1] : 0):F0}ms | {results.Count(r => r.Result.OverBudget)}回 |");
        sb.AppendLine();
        sb.AppendLine("## 回ごと");
        sb.AppendLine("| 回 | 時間の中央値 | 入力トークンの平均 | キャッシュが効いた回数 | キャッシュトークンの平均 | 出力トークンの平均 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var g in calls.GroupBy(c => c.Run))
        {
            var sorted = g.Select(c => c.Call.ElapsedMs).OrderBy(x => x).ToList();
            sb.AppendLine($"| {g.Key} | {sorted[sorted.Count / 2]:F0}ms | {g.Average(c => c.Call.PromptTokens ?? 0):F0} | {g.Count(c => c.Call.CacheHit)}/{g.Count()} | {g.Average(c => c.Call.CachedTokens ?? 0):F0} | {g.Average(c => c.Call.CompletionTokens ?? 0):F0} |");
        }
        sb.AppendLine();
        sb.AppendLine("## 状態");
        foreach (var g in results.GroupBy(r => (Status: r.Result.Status, CallStatus: r.Result.Call?.Status)))
            sb.AppendLine($"- {g.Key.Status} / {g.Key.CallStatus?.ToString() ?? "呼び出しなし"}: {g.Count()}回");
        sb.AppendLine();

        if (runs >= 2)
        {
            var byCase = results.GroupBy(r => r.Case.Id).ToList();
            var same = byCase.Count(g => g.Select(x => x.Result.Draft.ConditionSignature).Distinct().Count() == 1);
            // 検索結果に効くのは主に診療科（OR）と必須（AND）。加点は点数だけ。枠ごとに分けて数える
            static string Keys(IEnumerable<DraftCondition> items) => string.Join(",", items.Select(c => c.KeyId).Order(StringComparer.Ordinal));
            int SameBy(Func<ReferralDraft, string> key) =>
                byCase.Count(g => g.Select(x => key(x.Result.Draft)).Distinct().Count() == 1);
            static double Jaccard(IReadOnlyList<DraftCondition> a, IReadOnlyList<DraftCondition> b)
            {
                var sa = a.Select(c => c.KeyId).ToHashSet();
                var sb2 = b.Select(c => c.KeyId).ToHashSet();
                var union = sa.Union(sb2).Count();
                return union == 0 ? 1 : (double)sa.Intersect(sb2).Count() / union;
            }
            var bonusOverlap = byCase
                .Select(g => g.OrderBy(x => x.Run).Select(x => x.Result.Draft).ToList())
                .Where(d => d.Count >= 2)
                .Select(d => Jaccard(d[0].Bonus, d[1].Bonus))
                .DefaultIfEmpty(1)
                .Average();

            sb.AppendLine("## 再現性（同じ症例で同じ条件が出たか）");
            sb.AppendLine("| | 同じだった症例 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| 診療科の候補（順序を問わず） | {SameBy(d => Keys(d.Departments))}/{byCase.Count} |");
            sb.AppendLine($"| 必須条件（順序を問わず） | {SameBy(d => Keys(d.Required))}/{byCase.Count} |");
            sb.AppendLine($"| 診療科と必須の両方 | {SameBy(d => Keys(d.Departments) + "|" + Keys(d.Required))}/{byCase.Count} |");
            sb.AppendLine($"| 加点条件（順序を問わず） | {SameBy(d => Keys(d.Bonus))}/{byCase.Count}（1回目と2回目の重なり 平均 {bonusOverlap * 100:F0}%） |");
            sb.AppendLine($"| 全部（順序込み） | {same}/{byCase.Count} |");
            sb.AppendLine();
            foreach (var g in byCase.Where(g => g.Select(x => x.Result.Draft.ConditionSignature).Distinct().Count() > 1))
            {
                sb.AppendLine($"- {g.Key} が違う:");
                foreach (var x in g) sb.AppendLine($"  - {x.Run}回目 `{x.Result.Draft.ConditionSignature}`");
            }
            sb.AppendLine();
        }

        var firstRun = results.Where(r => r.Run == 1 && r.Result.Status == DraftStatus.Success).Select(r => r.Result.Draft).ToList();
        if (firstRun.Count > 0)
        {
            sb.AppendLine("## 条件の件数（1回目）");
            sb.AppendLine("| | 平均 | 最大 |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| 診療科 | {firstRun.Average(d => d.Departments.Count):F1} | {firstRun.Max(d => d.Departments.Count)} |");
            sb.AppendLine($"| 必須 | {firstRun.Average(d => d.Required.Count):F1} | {firstRun.Max(d => d.Required.Count)} |");
            sb.AppendLine($"| 加点 | {firstRun.Average(d => d.Bonus.Count):F1} | {firstRun.Max(d => d.Bonus.Count)} |");
            sb.AppendLine($"| unmatched | {firstRun.Average(d => d.Unmatched.Count):F1} | {firstRun.Max(d => d.Unmatched.Count)} |");
            sb.AppendLine();
            sb.AppendLine("必須の件数の分布: " + string.Join(" / ", firstRun.GroupBy(d => d.Required.Count).OrderBy(g => g.Key).Select(g => $"{g.Key}件: {g.Count()}")));
            sb.AppendLine();
            var issues = results.Where(r => r.Run == 1).SelectMany(r => r.Result.Draft.Issues).ToList();
            sb.AppendLine("## 検証（1回目）");
            foreach (var g in issues.GroupBy(i => i.Stage).OrderBy(g => g.Key))
                sb.AppendLine($"- 段階{g.Key}: {g.Count()}件（{string.Join(", ", g.Select(i => i.KeyId).Distinct().Take(10))}）");
            if (issues.Count == 0) sb.AppendLine("- 落ちた・動かしたものなし");
        }
        return sb.ToString();
    }

    private static string Csv(params object?[] values) => string.Join(",", values.Select(v =>
    {
        var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }));
}
