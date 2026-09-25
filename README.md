# medimap-referral-lab

逆紹介検索の試作（テスト版）。仕様は MedimapAgent の
`docs/逆紹介検索/12_テスト版の仕様.md`。設計資料とカタログCSVの正本は MedimapAgent 側にあり、
**試作で分かったことは MedimapAgent の設計資料に書き戻す。**

> **入力は架空の症例だけ。** 法務の確認（`運用/11_プライバシーと法務.md`）が済むまで、実在の患者の情報は送らない。
> 架空の症例だけで試すことを法務に一言確認してから、Azure OpenAI に流す。

## 構成

```
MedimapReferralLab/
├ MedimapReferralLab/        Blazor Server（仕様の Lab.Web）。ログインした人だけが使える画面
│                            /lab/ai … AI呼び出しの試験（手順1）
├ MedimapReferralLab.Core/   本実装に移す部分（仕様の Lab.Core）。画面にも NuGet にも依存しない
│   ├ Catalog/               カタログCSV・市区町村の読み込み
│   ├ Ai/                    プロンプト・呼び出し・出力スキーマ・5段階の検証・個人情報のガード
│   └ Cases/                 架空の症例の読み込み
├ MedimapReferralLab.Tests/  回帰テスト（仕様の Lab.Tests）。コンソールアプリ
└ data/
    ├ catalog/               MedimapAgent の docs/逆紹介検索/カタログ/ からコピー
    └ cases/架空症例.json     架空の症例 26件
```

## 進み具合

| 手順 | 状態 |
|---|---|
| 1 AIの呼び出し | **実装済み。実測はまだ**（Azure OpenAI のデプロイ待ち） |
| 2 検索用データ | 未着手 |
| 3 検索と一覧の画面 | 未着手 |

## 動かし方

### 設定

`MedimapReferralLab/appsettings.Development.template.json` を
`appsettings.Development.json` にコピーして値を入れる（`.gitignore` 済み。APIキーをコミットしない）。

| 設定 | |
|---|---|
| `ReferralSearch:AiEnabled` | `true` で Azure OpenAI に送る。**既定は `false`**（外部送信を止めるスイッチ。設計/08 9章） |
| `AzureOpenAI:Endpoint` / `ApiKey` / `Deployment` / `ApiVersion` | デプロイの情報 |
| `AzureOpenAI:DeploymentType` | デプロイの種類（`Standard` / `DataZoneStandard` / `GlobalStandard`）。記録と表示のためだけ。`GlobalStandard` なら結果に「参考値」と出る |
| `AzureOpenAI:TimeoutSeconds` | 打ち切る時間。試作では実測したいので既定10秒。予算（`BudgetSeconds` 既定4秒）を超えたら印を付ける |
| `AzureOpenAI:UseMaxCompletionTokens` | `max_tokens` を受け付けないモデル（o系・GPT-5系など）なら `true` |
| `AzureOpenAI:SendTemperature` | `temperature` を受け付けないモデルなら `false`（**再現性が保証されなくなる**ので画面に警告が出る） |
| `AzureOpenAI:SchemaUsesPatternAndMaxItems` | デプロイが strict スキーマの `pattern` / `maxItems` を拒否したら `false`。件数と形はコードでも検証している |
| `LabAuth:Users` | 試作を使える人（利用者名とパスワード）。空ならだれもログインできない |

### 画面

```
cd MedimapReferralLab
dotnet run --project MedimapReferralLab
```

`/lab/ai` で、架空の症例を選ぶか自由文を入れて「条件を作る」。
所要時間・入力／キャッシュ／出力のトークン数・モデル名・検証で落ちたIDが出る。
同じ症例を2回流すと、下の表に「前回と同じ／違う」が出る。**入力も結果も保存しない。**

### 回帰テスト

```
cd MedimapReferralLab
dotnet run --project MedimapReferralLab.Tests                       # オフラインの検査（Azure OpenAI 不要）
dotnet run --project MedimapReferralLab.Tests -- live               # 架空の症例 26件 × 2回を流して測る
dotnet run --project MedimapReferralLab.Tests -- live --cases K01,K02 --runs 3
dotnet run --project MedimapReferralLab.Tests -- live --interval 15       # 呼び出しの間を15秒空ける
```

`--interval` は、1分あたりのトークン数の枠でレート制限（429）に当たらないようにするため。
1回の入力が約4.5万トークンなので、枠が 200,000 なら1分に4回まで。`--interval 15` で52回を約15分で流せる。

`live` は `MedimapReferralLab/appsettings.Development.json` と環境変数の設定を使い、
`results/<日時>/` に次を出す（`results/` はコミットしない）。

| ファイル | 中身 | 手順1の終わりの条件 |
|---|---|---|
| `summary.md` | 時間（中央値・p90）・トークン数・キャッシュが効いた回数・再現性・必須の件数の分布・検証で落ちた件数 | 時間・トークン・キャッシュ／2回流して同じか |
| `calls.csv` | 呼び出しごとの記録 | 同上 |
| `review.csv` | 症例ごとの条件（表示名付き）。**確認役の人が「使える／足りない／余計」を書き込む列**がある。Excel で開ける | 確認役の判定 |
| `raw/` | 呼び出しごとの結果（JSON） | |

1周目を全件流してから2周目を流すので、2周目はキャッシュが温まった状態の数字になる。

## 設計から決めたこと（MedimapAgent に書き戻す候補）

| | |
|---|---|
| `profile` の `ageBand` / `sex` / `cityCode` / `facilityScope` はAIに出させない | 出力スキーマから外し、セレクタの値をコードで入れる。設計/08 4章の「AIに決めさせない」をスキーマで守る |
| 検証に「段階0」（IDの形・重複・件数の上限）を足した | スキーマの `pattern` / `maxItems` を受け付けないデプロイでも同じ制約をかけるため |
| 必須から格下げしたキーは加点の**先頭**に置く。理由も残す | AIが必須と見た＝加点の中では優先度が高い |
| 必須条件と同じキーが加点にもあれば、加点から落とす | |
| AI呼び出しは SDK ではなく REST | usage（キャッシュに当たったトークン数）と `system_fingerprint` をそのまま記録するため。NuGet 依存も増やさない |
| 静的プロンプトは system、可変部分は user | 静的部分はカタログ読み込み時に1回だけ作り、ハッシュを画面とログに出す（変わるとキャッシュが効かない） |
| 画面は Radzen ではなく Bootstrap | 手順1は試験ページだけなので。手順3の④と一覧で Radzen を入れる |
| 手順1の実測はグローバル標準（2026-09-25） | Visual Studio サブスクリプション（MSDN）には Japan East の標準・データ ゾーン標準の枠が無いため。**架空の症例だけ・法務に一言確認したうえで使う。**時間とキャッシュは参考値で、本実装（Japan East・標準）で測り直す |
| プロンプト第2版（2026-09-25） | 第1版の回帰テスト（26件×2回）で、加点を見出しの下ごとまとめて選ぶ・加点の再現性が低い・訪問診療が要る症例で必須が出ない、が分かったため。加点は自由文に根拠のあるものだけ・上限10件、在宅療養を支える体制のキー（M2259 など）を必須に検討させる |
| 歯科を選んだらAIを呼ばない | 歯科の検索キーが未整備で、AIが医科のキーで条件を作ってしまうため（第1版の K26） |
| ログインは設定ファイルの利用者 | 試作は AgentDB を使わないため。本実装では Agent の管理者ログインに載せる |
