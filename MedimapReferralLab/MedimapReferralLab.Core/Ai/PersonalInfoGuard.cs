using System.Text;
using System.Text.RegularExpressions;

namespace MedimapReferralLab.Core.Ai;

public enum GuardSeverity
{
    /// <summary>書き直してもらう。Azure OpenAI へ送らない。</summary>
    Block,
    /// <summary>注意を出すだけ。送ってよい。</summary>
    Warn,
}

public sealed record GuardFinding(GuardSeverity Severity, string Kind, string Matched);

public sealed record GuardResult(IReadOnlyList<GuardFinding> Findings)
{
    public bool IsBlocked => Findings.Any(f => f.Severity == GuardSeverity.Block);
    public IEnumerable<GuardFinding> Blocks => Findings.Where(f => f.Severity == GuardSeverity.Block);
    public IEnumerable<GuardFinding> Warnings => Findings.Where(f => f.Severity == GuardSeverity.Warn);
}

/// <summary>
/// 個人情報のガード。自社サーバ → Azure OpenAI の境界（送る直前）に置く。
/// 設計/08_生成AIの呼び出し.md 5章。
/// <para>
/// <b>補助でしかない。</b>氏名は正規表現で検知できず、すり抜けたものは Azure OpenAI に送られる。
/// いちばん効く防御は、出力に氏名・生年月日の置き場（スロット）を作らないこと（同 4章）。
/// </para>
/// </summary>
public static partial class PersonalInfoGuard
{
    public static GuardResult Check(string freeText)
    {
        // 全角数字・全角記号を半角に寄せてから見る（「０３－１２３４－５６７８」も止める）
        var text = Normalize(freeText);
        var findings = new List<GuardFinding>();

        void Add(GuardSeverity severity, string kind, Regex regex)
        {
            foreach (Match m in regex.Matches(text))
                findings.Add(new GuardFinding(severity, kind, m.Value));
        }

        Add(GuardSeverity.Block, "生年月日", WarekiDate());
        Add(GuardSeverity.Block, "年月日（生年月日の可能性）", SeirekiDate());
        Add(GuardSeverity.Block, "生年月日", BirthdayWord());
        Add(GuardSeverity.Block, "電話番号", PhoneNumber());
        Add(GuardSeverity.Block, "メールアドレス", Email());
        Add(GuardSeverity.Block, "長い数字（患者ID・保険者番号など）", LongDigits());

        foreach (Match m in Honorific().Matches(text))
        {
            var word = m.Groups["word"].Value;
            if (IsCommonWordBeforeHonorific(word)) continue;
            findings.Add(new GuardFinding(GuardSeverity.Warn, "敬称（氏名の可能性）", m.Value));
        }

        // 電話番号と長い数字が同じ箇所で二重に出るのを1件にまとめる
        return new GuardResult(findings
            .GroupBy(f => (f.Severity, f.Matched))
            .Select(g => g.First())
            .ToList());
    }

    public static string Normalize(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC);
        // NFKC はハイフン類（U+2010〜2015・U+2212）を「-」に寄せないので明示する。長音「ー」は触らない
        foreach (var dash in "‐‑‒–—―−")
            s = s.Replace(dash, '-');
        return s;
    }

    /// <summary>「患者さん」「奥様」のような一般語は氏名ではない。</summary>
    private static bool IsCommonWordBeforeHonorific(string word) =>
        word.Length == 0
        || word.All(char.IsDigit)
        || CommonWords.Any(w => word.EndsWith(w, StringComparison.Ordinal));

    private static readonly string[] CommonWords =
    [
        "患者", "患", "奥", "旦那", "娘", "息子", "お子", "子", "親御", "お父", "お母", "父", "母", "兄", "姉", "弟", "妹",
        "孫", "嫁", "婿", "家族", "皆", "みな", "おばあ", "おじい", "ばあ", "じい", "おば", "おじ", "看護師", "医師", "先生", "ケアマネ", "ヘルパー",
        "利用者", "本人", "ご本人", "相手", "お客", "隣", "近所", "お", "ご", "その", "この", "あの",
    ];

    // S22.3.5 / 昭和22年3月5日 / H1.1.8 / 令和2年4月1日
    [GeneratedRegex(@"(明治|大正|昭和|平成|令和|[MTSHR])\s*(\d{1,2}|元)\s*[年./\-]\s*\d{1,2}\s*[月./\-]\s*\d{1,2}\s*日?", RegexOptions.IgnoreCase)]
    private static partial Regex WarekiDate();

    // 1947/03/05 / 1947-3-5 / 1947年3月5日
    [GeneratedRegex(@"(18|19|20)\d{2}\s*[年./\-]\s*\d{1,2}\s*[月./\-]\s*\d{1,2}\s*日?")]
    private static partial Regex SeirekiDate();

    [GeneratedRegex(@"(生年月日|誕生日|生まれ)\s*[:：は]?\s*\S{0,12}\d")]
    private static partial Regex BirthdayWord();

    // 03-1234-5678 / 090-1234-5678 / 0120-123-456 / (03)1234-5678
    [GeneratedRegex(@"\(?0\d{1,4}\)?[\s\-]?\d{1,4}[\s\-]\d{3,4}(?!\d)")]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // 8桁以上の連続する数字（間のハイフン・空白は無視）。患者ID・保険者番号・マイナンバー
    [GeneratedRegex(@"\d(?:[\s\-]?\d){7,}")]
    private static partial Regex LongDigits();

    [GeneratedRegex(@"(?<word>[\p{IsCJKUnifiedIdeographs}\p{IsKatakana}\p{IsHiragana}A-Za-z]{1,8}?)(さん|様|さま|氏|くん|ちゃん)")]
    private static partial Regex Honorific();
}
