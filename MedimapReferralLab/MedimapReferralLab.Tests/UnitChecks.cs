using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MedimapReferralLab.Core;
using MedimapReferralLab.Core.Ai;
using MedimapReferralLab.Core.Cases;
using MedimapReferralLab.Core.Catalog;

namespace MedimapReferralLab.Tests;

/// <summary>Azure OpenAI を使わない検査。</summary>
internal static class UnitChecks
{
    public static int Run(string root)
    {
        var check = new Check();
        var catalog = SearchKeyCatalog.LoadFromDirectory(RepoPaths.CatalogDirectory(root));
        var cities = CityCatalog.LoadFromDirectory(RepoPaths.CatalogDirectory(root));
        var prompts = new PromptBuilder(catalog, cities);

        Catalog(check, catalog, cities);
        Prompt(check, catalog, cities, prompts);
        Schema(check);
        Validator(check, catalog);
        Guard(check);
        ClientAsync(check).GetAwaiter().GetResult();
        ServiceAsync(check, catalog, prompts).GetAwaiter().GetResult();
        Cases(check, root, cities);

        return check.Summarize();
    }

    private static readonly ReferralRequest Request = new()
    {
        CityCode = "13111", AgeBand = "70代", Sex = "男", FreeText = "慢性心不全。在宅酸素療法を導入した。",
    };

    // 件数は 設計/04_検索キーカタログ.md 11章の実測
    private static void Catalog(Check check, SearchKeyCatalog catalog, CityCatalog cities)
    {
        check.Group("カタログの読み込み（04 11章の件数と照合）");
        check.Equal(1833, catalog.MedimapKeys.Count, "メディマップ 1,833件");
        check.Equal(1671, catalog.MedimapKeys.Count(k => k.Searchable), "検索対象 1,671件");
        check.Equal(768, catalog.MedimapKeys.Count(k => k.RequirableAsMust), "必須可 768件");
        check.Equal(556, catalog.CommonSurveyKeys.Count, "共通設問 556件");
        check.Equal(70, catalog.MedimapKeys.Count(k => k.IsDepartment && k.Searchable), "診療科目（検索対象）70件");
        check.That(catalog.CommonSurveyKeys.All(k => !k.RequirableAsMust), "共通設問は必須にできない");
        check.Equal("内科", catalog.Find("M0002")?.DisplayName, "M0002 は内科");
        check.That(catalog.Find("M0001") is { Searchable: false }, "M0001 歯科は検索対象外");
        check.Equal("高血圧（疾患治療）", catalog.All.FirstOrDefault(k => k.DisplayName == "高血圧" && k.Source == "疾患治療")?.LabelForPrompt,
            "同名はソース名を添える（04 8章）");
        check.Equal("東京都大田区", cities.Find("13111")?.FullName, "市区町村 13111 は大田区");
    }

    private static void Prompt(Check check, SearchKeyCatalog catalog, CityCatalog cities, PromptBuilder prompts)
    {
        check.Group("プロンプトの組み立て（08 6章）");
        var s = prompts.StaticPrompt;
        var catalogPart = s[s.IndexOf("# 3. メディマップ検索キー", StringComparison.Ordinal)..];
        var ids = Regex.Matches(catalogPart, @"(?<![A-Za-z0-9])[MC]\d{4}(?= )").Select(m => m.Value).ToList();
        check.Equal(1671 + 556, ids.Distinct().Count(), "カタログは全量（2,227件）");
        check.Equal(ids.Count, ids.Distinct().Count(), "同じIDが2回出ない");
        check.That(!ids.Contains("M0001"), "検索対象外（M0001 歯科）は入らない");

        // 見出しの語は指示文にも出てくるので、行頭の見出しとして探す
        var must = s.IndexOf("\n## 必須条件にも加点にも使える\n", StringComparison.Ordinal);
        var bonusOnly = s.IndexOf("\n## 加点にのみ使える\n", StringComparison.Ordinal);
        var common = s.IndexOf("\n# 4. 共通設問検索キー\n", StringComparison.Ordinal);
        check.That(must > 0 && bonusOnly > must && common > bonusOnly, "見出しの順: 必須可 → 加点のみ → 共通設問");
        var mustIds = Regex.Matches(s[must..bonusOnly], @"M\d{4}(?= )").Select(m => m.Value).Distinct().ToList();
        check.Equal(768, mustIds.Count, "「必須条件にも加点にも使える」は768件");
        check.That(mustIds.All(id => catalog.Find(id)!.RequirableAsMust), "そのブロックは全部 必須可=1");
        check.That(s.Contains("### 診療科目／診療科目"), "ソース／分類の見出し");
        check.That(s.Contains("副甲状腺疾患 ／ 甲状腺疾患"), "判定の注意（別概念の実例）");
        check.That(s.Contains("まとめて選ばないでください") && s.Contains("M2259 在宅療養支援診療所"), "第2版の指示（加点を絞る・在宅の体制）");
        check.That(catalog.Find("M2259") is { RequirableAsMust: true }, "例に出した M2259 は必須可");
        check.That(s.IndexOf("# 5. 判定の注意", StringComparison.Ordinal) > common, "判定の注意は最後");

        var again = new PromptBuilder(catalog, cities);
        check.Equal(prompts.StaticPromptHash, again.StaticPromptHash, "静的部分は毎回同じ（キャッシュのため）");

        var user = prompts.BuildUserMessage(Request);
        check.That(user.Contains("東京都大田区（13111）") && user.Contains("70代") && user.Contains(Request.FreeText), "可変部分にセレクタの値と自由文");
        check.That(!s.Contains(Request.FreeText), "自由文は静的部分に入らない");
        Console.WriteLine($"  静的部分 {s.Length:N0}字 / hash {prompts.StaticPromptHash}");
    }

    private static void Schema(Check check)
    {
        check.Group("出力スキーマ（08 7章）");
        var schema = OutputSchema.Build(usePatternAndMaxItems: true);

        // strict モードの約束: すべての object で全プロパティを required にし、additionalProperties=false
        var objects = new List<JsonObject>();
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if ((string?)o["type"] == "object") objects.Add(o);
                foreach (var (_, v) in o) Walk(v);
            }
            else if (n is JsonArray a) foreach (var v in a) Walk(v);
        }
        Walk(schema);
        check.That(objects.All(o => o["additionalProperties"]?.GetValue<bool>() == false), "すべての object が additionalProperties=false");
        check.That(objects.All(o => ((JsonObject)o["properties"]!).Count == ((JsonArray)o["required"]!).Count), "すべてのプロパティが required");

        var props = (JsonObject)schema["properties"]!;
        check.That(new[] { "profile", "departments", "required", "bonus", "unmatched" }.All(props.ContainsKey), "profile / departments / required / bonus / unmatched");
        var profileProps = (JsonObject)props["profile"]!["properties"]!;
        check.That(!profileProps.ContainsKey("cityCode") && !profileProps.ContainsKey("ageBand"), "市区町村・年齢帯はAIに出させない");
        check.That(!profileProps.ContainsKey("name") && !profileProps.ContainsKey("birthDate"), "氏名・生年月日のスロットが無い");
        check.Equal(6, props["required"]!["maxItems"]!.GetValue<int>(), "required maxItems 6");
        check.Equal(10, props["bonus"]!["maxItems"]!.GetValue<int>(), "bonus maxItems 10（第2版）");
        check.That(props["required"]!["items"]!["properties"]!["reason"] is not null, "必須条件にだけ reason");
        check.That(props["bonus"]!["items"]!["properties"]!["reason"] is null, "加点には reason が無い");

        var loose = OutputSchema.Build(usePatternAndMaxItems: false).ToJsonString();
        check.That(!loose.Contains("maxItems") && !loose.Contains("pattern"), "pattern / maxItems を外せる");
    }

    private static void Validator(Check check, SearchKeyCatalog catalog)
    {
        check.Group("出力の検証（04 9章の5段階）");
        var validator = new OutputValidator(catalog);
        var raw = new RawDraft(
            new RawProfile(["慢性心不全"], ["在宅酸素療法"], ["独居"]),
            Departments: [new("M0002"), new("M0102"), new("M0006")],
            Required: [new("M9999", "無い"), new("M0102", "循環器"), new("M1800", "連携"), new("M0006", "消化器"), new("C0003", "心不全"), new("X12", "形が変")],
            Bonus: [new("M0001"), new("M1801"), new("M0102"), new("M1801")],
            Unmatched: ["往診の範囲", "a", "b", "c"]);
        var d = validator.Validate(raw, Request);

        bool Has(int stage, string id, ValidationAction action) =>
            d.Issues.Any(i => i.Stage == stage && i.KeyId == id && i.Action == action);

        check.That(Has(1, "M9999", ValidationAction.Dropped), "1: カタログに無いIDは捨てる");
        check.That(Has(0, "X12", ValidationAction.Dropped), "0: IDの形が違うものは捨てる");
        check.That(Has(2, "M0001", ValidationAction.Dropped), "2: 検索対象外（歯科）は捨てる");
        check.That(Has(3, "M1800", ValidationAction.Downgraded) && d.Bonus.Any(c => c.KeyId == "M1800"), "3: 必須可=0 は加点に格下げ");
        check.That(Has(3, "C0003", ValidationAction.Downgraded) && d.Bonus.Any(c => c.KeyId == "C0003"), "3: 共通設問を必須に挙げたら加点に格下げ");
        check.Equal("M1800", d.Bonus.FirstOrDefault()?.KeyId, "3: 格下げしたものは加点の先頭（AIの意図を残す）");
        check.Equal("連携", d.Bonus.First(c => c.KeyId == "M1800").Reason, "3: 格下げしても理由は残す");
        check.That(Has(4, "M0102", ValidationAction.Moved), "4: 診療科目でないIDは departments から外す");
        check.That(Has(5, "M0006", ValidationAction.Moved), "5: 診療科目のキーは required から departments へ");
        check.Equal("M0002,M0006", string.Join(",", d.Departments.Select(c => c.KeyId)), "departments は診療科目だけ・重複なし");
        check.Equal("M0102", string.Join(",", d.Required.Select(c => c.KeyId)), "required は必須可=1 だけ");
        check.That(!d.Bonus.Any(c => c.KeyId == "M0102"), "必須条件と重複する加点は落とす");
        check.Equal(1, d.Bonus.Count(c => c.KeyId == "M1801"), "加点の重複は1件にする");
        check.Equal(3, d.Unmatched.Count, "unmatched は3件まで");
        check.Equal("13111", d.Profile.CityCode, "市区町村はセレクタの値");
        check.Equal("医科", d.Profile.FacilityScope, "医科／歯科はセレクタの値");

        var many = new RawDraft(new RawProfile([], [], []), [],
            catalog.MedimapKeys.Where(k => k.RequirableAsMust && !k.IsDepartment).Take(8).Select(k => new RawKey(k.Id, "r")).ToList(),
            [], []);
        var limited = validator.Validate(many, Request);
        check.Equal(OutputLimits.Required, limited.Required.Count, "必須は6件で切る");
        check.Equal(2, limited.Issues.Count(i => i.Stage == 0 && i.FromList == "required"), "切ったものは記録する");

        var dental = validator.Validate(new RawDraft(new RawProfile([], [], []), [new("M0001")], [], [new("M0003")], []), Request);
        check.That(!dental.HasAnyCondition && dental.Issues.Count == 2, "全部落ちたら条件が空");
    }

    private static void Guard(Check check)
    {
        check.Group("個人情報のガード（08 5章）");
        void Blocked(string text, string kind) =>
            check.That(PersonalInfoGuard.Check(text).Blocks.Any(f => f.Kind.Contains(kind)), $"ブロック: {text}");
        void Passed(string text) =>
            check.That(!PersonalInfoGuard.Check(text).IsBlocked, $"通す: {text}", string.Join(" / ", PersonalInfoGuard.Check(text).Blocks.Select(f => f.Matched)));
        void Warned(string text) =>
            check.That(PersonalInfoGuard.Check(text) is { IsBlocked: false } r && r.Warnings.Any(), $"警告: {text}");
        void Silent(string text) =>
            check.That(!PersonalInfoGuard.Check(text).Findings.Any(), $"何も出さない: {text}", string.Join(" / ", PersonalInfoGuard.Check(text).Findings.Select(f => f.Matched)));

        Blocked("S22.3.5生まれ", "生年月日");
        Blocked("昭和22年3月5日生", "生年月日");
        Blocked("生年月日 1947/03/05", "生年月日");
        Blocked("連絡先は03-1234-5678", "電話番号");
        Blocked("携帯 090-1234-5678", "電話番号");
        Blocked("連絡先は０３－１２３４－５６７８", "電話番号");
        Blocked("患者ID 12345678", "長い数字");
        Blocked("保険者番号 0613 1234", ""); // 電話番号の形にも当たる。どちらで止まってもよい
        Blocked("mail: foo.bar@example.jp", "メール");

        Passed("HbA1c 8.5%、血圧 150/90、BNP 450");
        Passed("3月5日に退院予定。週3回の透析");
        Passed("心不全で2回入院。EF 35%");
        Warned("山田さんから紹介された");
        Warned("田中様の件");
        Silent("患者さんは独居。娘さんが週1回来る");
        Silent("奥様と二人暮らし。ケアマネさんと相談済み");
    }

    private static readonly AzureOpenAiOptions TestOptions = new()
    {
        Endpoint = "https://example.openai.azure.com", ApiKey = "k", Deployment = "d", TimeoutSeconds = 5,
    };

    private static async Task ClientAsync(Check check)
    {
        check.Group("AI呼び出し（08 8章）");
        var ok = FakeHandler.Json(FakeHandler.Completion("""{"a":1}""", cached: 38000));
        var client = new ReferralAiClient(new HttpClient(ok), TestOptions);
        var r = await client.CallAsync("sys", "user");
        check.Equal(AiCallStatus.Success, r.Status, "成功");
        check.Equal(40000, r.PromptTokens, "入力トークン");
        check.Equal(38000, r.CachedTokens, "キャッシュに当たったトークン");
        check.Equal(300, r.CompletionTokens, "出力トークン");
        check.Equal("gpt-test-2026-01-01", r.Model, "モデル名（バージョン込み）");

        var body = JsonNode.Parse(ok.LastBody!)!;
        check.Equal(0, body["temperature"]!.GetValue<int>(), "temperature 0");
        check.That(body["stream"]!.GetValue<bool>() == false, "ストリーミングなし");
        check.That(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>(), "Structured Outputs（strict）");
        check.Equal("sys", (string?)body["messages"]![0]!["content"], "静的部分が先頭（system）");
        check.That(body["max_tokens"] is not null, "max_tokens");

        var r429 = await new ReferralAiClient(new HttpClient(FakeHandler.Json("{}", HttpStatusCode.TooManyRequests)), TestOptions).CallAsync("s", "u");
        check.Equal(AiCallStatus.RateLimited, r429.Status, "429 はレート制限として区別");
        var r500 = await new ReferralAiClient(new HttpClient(FakeHandler.Json("{}", HttpStatusCode.InternalServerError)), TestOptions).CallAsync("s", "u");
        check.Equal(AiCallStatus.ServiceError, r500.Status, "500 は障害");

        var slow = new FakeHandler(async (_, ct) => { await Task.Delay(5000, ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var rt = await new ReferralAiClient(new HttpClient(slow), new AzureOpenAiOptions
        {
            Endpoint = "https://e", ApiKey = "k", Deployment = "d", TimeoutSeconds = 0.2,
        }).CallAsync("s", "u");
        check.Equal(AiCallStatus.Timeout, rt.Status, "タイムアウトで切る");

        var cut = ReferralAiClient.ParseResponse(FakeHandler.Completion("{\"a\":", "length"), 1, 200);
        check.Equal(AiCallStatus.SchemaViolation, cut.Status, "途中で切れた（finish_reason=length）はスキーマ違反");
    }

    private static async Task ServiceAsync(Check check, SearchKeyCatalog catalog, PromptBuilder prompts)
    {
        check.Group("失敗したときのふるまい（08 9章）");

        (ReferralDraftService Service, FakeHandler Handler) Make(string content, bool aiEnabled = true)
        {
            var handler = FakeHandler.Json(FakeHandler.Completion(content));
            var service = new ReferralDraftService(prompts, new OutputValidator(catalog),
                new ReferralAiClient(new HttpClient(handler), TestOptions), new ReferralSearchOptions { AiEnabled = aiEnabled });
            return (service, handler);
        }

        const string good = """
            {"profile":{"conditions":["慢性心不全"],"careNeeds":["在宅酸素療法"],"context":["独居"]},
             "departments":[{"key":"M0002"}],"required":[{"key":"M0102","reason":"心不全の継続管理"}],
             "bonus":[{"key":"M1800"}],"unmatched":[]}
            """;

        var (s1, h1) = Make(good);
        var ok = await s1.CreateDraftAsync(Request);
        check.Equal(DraftStatus.Success, ok.Status, "条件が出る");
        check.Equal("D:M0002|R:M0102|B:M1800", ok.Draft.ConditionSignature, "検証済みの条件");
        check.Equal(1, h1.Calls, "呼び出しは1回だけ");

        var (s2, h2) = Make(good, aiEnabled: false);
        var off = await s2.CreateDraftAsync(Request);
        check.That(off.Status == DraftStatus.FallbackToManual && h2.Calls == 0, "AiEnabled=false なら送らず手動へ");

        var (s3, h3) = Make(good);
        var blocked = await s3.CreateDraftAsync(Request with { FreeText = "S22.3.5生 心不全" });
        check.That(blocked.Status == DraftStatus.BlockedByGuard && h3.Calls == 0, "ブロックしたら送らない");

        var (s6, h6) = Make(good);
        var dental = await s6.CreateDraftAsync(Request with { FacilityScope = FacilityScopes.Dental });
        check.That(dental.Status == DraftStatus.FallbackToManual && h6.Calls == 0, "歯科はAIを呼ばず手動へ（歯科のカタログが未整備）");

        var (s4, _) = Make("""{"profile":{}}""");
        var broken = await s4.CreateDraftAsync(Request);
        check.That(broken.Status == DraftStatus.FallbackToManual && !broken.Draft.HasAnyCondition, "形が違えば手動へ（再試行しない）");

        var (s5, _) = Make("""{"profile":{"conditions":[],"careNeeds":[],"context":[]},"departments":[{"key":"M0001"}],"required":[],"bonus":[{"key":"M9999"}],"unmatched":[]}""");
        var dropped = await s5.CreateDraftAsync(Request);
        check.That(dropped.Status == DraftStatus.FallbackToManual && dropped.FallbackMessage!.Contains("2件"), "全IDが検証で落ちたら手動へ・件数を出す");
        check.Equal(2, dropped.Draft.Issues.Count, "落ちた理由は残す");
    }

    private static void Cases(Check check, string root, CityCatalog cities)
    {
        check.Group("架空の症例");
        var cases = FictionalCase.Load(RepoPaths.CasesFile(root));
        check.That(cases.Count is >= 20 and <= 30, $"20〜30件（{cases.Count}件）");
        check.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count(), "IDが重ならない");
        check.That(cases.All(c => cities.Find(c.CityCode) is not null), "市区町村コードが一覧にある");
        check.That(cases.All(c => AgeBands.All.Contains(c.AgeBand) && Sexes.All.Contains(c.Sex) && FacilityScopes.All.Contains(c.FacilityScope)), "セレクタの値が正しい");
        var blocked = cases.Where(c => PersonalInfoGuard.Check(c.FreeText).Findings.Any()).Select(c => c.Id).ToList();
        check.That(blocked.Count == 0, "個人情報のガードに引っかからない", string.Join(",", blocked));
    }
}
