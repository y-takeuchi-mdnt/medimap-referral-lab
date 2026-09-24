namespace MedimapReferralLab.Core.Ai;

/// <summary>
/// Azure OpenAI の接続設定。APIキーはリポジトリに入れない（appsettings.Development.json・環境変数・user-secrets）。
/// </summary>
public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>例: https://xxxx.openai.azure.com</summary>
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";

    /// <summary>デプロイ名。モデルのバージョンはデプロイ側で固定する（08 8章）。</summary>
    public string Deployment { get; set; } = "";

    public string ApiVersion { get; set; } = "2024-10-21";

    /// <summary>
    /// タイムアウト。設計では「AI呼び出しに割く予算で切る」（08 8・9章）。
    /// 試作では実測したいので予算より長めに取り、予算超過は結果に印を付けて出す。
    /// </summary>
    public double TimeoutSeconds { get; set; } = 10;

    /// <summary>AI呼び出しに割く予算（07 10章の配分。3〜4秒）。超えたら結果に印を付ける。</summary>
    public double BudgetSeconds { get; set; } = 4;

    /// <summary>出力は数百トークン。大きくしても遅くなるだけ（08 8章）。</summary>
    public int MaxOutputTokens { get; set; } = 1500;

    /// <summary>
    /// true なら max_completion_tokens を送る（o系・GPT-5系など max_tokens を受け付けないモデル用）。
    /// </summary>
    public bool UseMaxCompletionTokens { get; set; }

    /// <summary>
    /// false なら temperature を送らない（temperature を受け付けないモデル用）。
    /// 送らないと「temperature 0」が守れないので、試験ページに警告を出す。
    /// </summary>
    public bool SendTemperature { get; set; } = true;

    /// <summary>
    /// true なら JSON Schema に pattern / maxItems を入れる。
    /// デプロイによっては strict モードでこれらを受け付けないので切り替えられるようにする。
    /// どちらでもコード側の検証（OutputValidator）で同じ制約をかける。
    /// </summary>
    public bool SchemaUsesPatternAndMaxItems { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Deployment);
}

/// <summary>機能全体の設定。</summary>
public sealed class ReferralSearchOptions
{
    public const string SectionName = "ReferralSearch";

    /// <summary>
    /// false なら外部送信（Azure OpenAI）を止める。法務の判断が出る前・障害時のスイッチ（08 9章）。
    /// </summary>
    public bool AiEnabled { get; set; }

    /// <summary>カタログCSVのあるディレクトリ。</summary>
    public string CatalogDirectory { get; set; } = "data/catalog";
}
