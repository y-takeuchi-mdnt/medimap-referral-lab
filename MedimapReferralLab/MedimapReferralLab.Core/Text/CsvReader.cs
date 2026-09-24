using System.Text;

namespace MedimapReferralLab.Core.Text;

/// <summary>
/// カタログCSV（RFC 4180、UTF-8 BOM付き）を読む。NuGet に依存させないための最小実装。
/// </summary>
public static class CsvReader
{
    public static List<Dictionary<string, string>> ReadFile(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return Read(reader.ReadToEnd());
    }

    public static List<Dictionary<string, string>> Read(string text)
    {
        var rows = ParseRows(text);
        if (rows.Count == 0) return [];

        var header = rows[0];
        var result = new List<Dictionary<string, string>>(rows.Count - 1);
        foreach (var row in rows.Skip(1))
        {
            if (row.Count == 1 && row[0].Length == 0) continue; // 空行
            var dict = new Dictionary<string, string>(header.Count, StringComparer.Ordinal);
            for (var i = 0; i < header.Count; i++)
                dict[header[i]] = i < row.Count ? row[i] : "";
            result.Add(dict);
        }
        return result;
    }

    private static List<List<string>> ParseRows(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString()); field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
