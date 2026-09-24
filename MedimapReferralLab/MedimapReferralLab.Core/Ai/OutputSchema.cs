using System.Text.Json.Nodes;

namespace MedimapReferralLab.Core.Ai;

/// <summary>
/// Structured Outputs に渡す JSON Schema。設計/08_生成AIの呼び出し.md 7章。
/// <para>
/// スキーマで縛れるのは形だけ。検索キー2,227件は enum に入れられないので、
/// 所属はコード（<see cref="OutputValidator"/>）で検証する。
/// </para>
/// <para>
/// profile の ageBand / sex / cityCode / facilityScope はAIに出させない。セレクタの値をコードで入れる
/// （08 4章。AIが取り違えると結果が全部おかしくなる）。
/// </para>
/// </summary>
public static class OutputSchema
{
    public const string Name = "referral_search_draft";

    public static JsonObject Build(bool usePatternAndMaxItems)
    {
        JsonObject KeyString()
        {
            var o = new JsonObject { ["type"] = "string" };
            if (usePatternAndMaxItems) o["pattern"] = OutputLimits.KeyPattern;
            return o;
        }

        JsonObject Array(JsonNode items, int maxItems)
        {
            var o = new JsonObject { ["type"] = "array", ["items"] = items };
            if (usePatternAndMaxItems) o["maxItems"] = maxItems;
            return o;
        }

        JsonObject Obj(params (string Name, JsonNode Schema)[] props)
        {
            var properties = new JsonObject();
            foreach (var (name, schema) in props) properties[name] = schema;
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(props.Select(p => (JsonNode)JsonValue.Create(p.Name)).ToArray()),
                ["additionalProperties"] = false,
            };
        }

        JsonObject StringArray(int? maxItems = null)
        {
            var o = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
            if (usePatternAndMaxItems && maxItems is int m) o["maxItems"] = m;
            return o;
        }

        return Obj(
            ("profile", Obj(
                ("conditions", StringArray()),
                ("careNeeds", StringArray()),
                ("context", StringArray()))),
            ("departments", Array(Obj(("key", KeyString())), OutputLimits.Departments)),
            ("required", Array(Obj(("key", KeyString()), ("reason", new JsonObject { ["type"] = "string" })), OutputLimits.Required)),
            ("bonus", Array(Obj(("key", KeyString())), OutputLimits.Bonus)),
            ("unmatched", StringArray(OutputLimits.Unmatched)));
    }
}
