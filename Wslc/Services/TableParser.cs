using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WslManager.Wslc.Services;

/// <summary>
/// wslc 命令输出解析。
/// wslc 的 CLI 与 docker 对齐，列表类命令输出「列对齐的纯文本表」；
/// 这里不假设固定列位置，而是「先用表头定位列，再用列起始偏移切分数据行」，
/// 这样列宽变化、列增删都不会让解析崩掉。
/// </summary>
public static class TableParser
{
    /// <summary>按行拆分并清理。</summary>
    public static List<string> Lines(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n')
                   .Split('\n')
                   .Select(l => l.TrimEnd())
                   .Where(l => l.Trim().Length > 0)
                   .ToList();
    }

    /// <summary>
    /// 解析对齐表格。返回 (表头, 数据行)。
    /// 策略：表头按 2+ 空格切出列名与其起始列号；数据行按这些列号切片。
    /// 若表头切不出列，则退化为「数据行按 2+ 空格切」。
    /// </summary>
    public static (string[] Header, List<string[]> Rows) ParseAligned(string text)
    {
        var lines = Lines(text);
        var rows = new List<string[]>();
        if (lines.Count == 0) return (Array.Empty<string>(), rows);

        var headerLine = lines[0];
        var colStarts = ColumnStarts(headerLine);
        var header = new List<string>();
        for (int i = 0; i < colStarts.Count; i++)
        {
            int s = colStarts[i];
            int e = i + 1 < colStarts.Count ? colStarts[i + 1] : headerLine.Length;
            if (s >= headerLine.Length) { header.Add(""); continue; }
            header.Add(headerLine.Substring(s, Math.Min(e, headerLine.Length) - s).Trim());
        }

        var body = lines.Skip(1).ToList();

        if (colStarts.Count <= 1)
        {
            // 退化：按 2+ 空格切
            foreach (var l in body)
                rows.Add(Regex.Split(l.Trim(), @"\s{2,}").Select(x => x.Trim()).ToArray());
            return (header.ToArray(), rows);
        }

        foreach (var l in body)
        {
            if (l.Length <= colStarts[0])
            {
                // 极短行，整体当成第一列
                rows.Add(new[] { l.Trim() });
                continue;
            }
            var cells = new List<string>();
            for (int i = 0; i < colStarts.Count; i++)
            {
                int s = colStarts[i];
                int e = i + 1 < colStarts.Count ? colStarts[i + 1] : l.Length;
                if (s >= l.Length) { cells.Add(""); continue; }
                cells.Add(l.Substring(s, Math.Min(e, l.Length) - s).Trim());
            }
            rows.Add(cells.ToArray());
        }
        return (header.ToArray(), rows);
    }

    /// <summary>找出各列的起始列号：表头中每段非空白块的起点。</summary>
    private static List<int> ColumnStarts(string line)
    {
        var res = new List<int>();
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ') { i++; continue; }
            res.Add(i);
            while (i < line.Length && line[i] != ' ') i++;
            // 列名内部允许单个空格（如 CONTAINER ID）；只要后面还有 2+ 连续空格才算列结束
            while (i < line.Length)
            {
                if (line[i] != ' ') { i++; continue; }
                // 看是否有 2+ 连续空格
                int j = i;
                while (j < line.Length && line[j] == ' ') j++;
                if (j - i >= 2) { i = j; break; }
                i = j;
                while (i < line.Length && line[i] != ' ') i++;
            }
        }
        return res;
    }

    /// <summary>表头归一化：去掉空格/连字符/下划线并转小写，便于按名字取列。</summary>
    public static string Normalize(string h)
        => new string((h ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>从字典里按若干候选键取值。</summary>
    public static string Pick(Dictionary<string, string> d, params string[] keys)
    {
        foreach (var k in keys)
        {
            var n = Normalize(k);
            if (d.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return "";
    }

    /// <summary>
    /// 解析 NDJSON（每行一个 JSON 对象）——wslc 的 `--format json` 就是这个格式，
    /// 例如: {"ID":"abc","Names":"web","State":"running"}
    /// 返回每行对象展开后的 归一化键 → 值 字典；非 JSON 行会被跳过。
    /// </summary>
    public static List<Dictionary<string, string>> ParseJsonLines(string text)
    {
        var res = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(text)) return res;

        // 同时兼容整体是 JSON 数组的情况
        var trimmed = text.Trim();
        if (trimmed.StartsWith("["))
        {
            try
            {
                using var arr = JsonDocument.Parse(trimmed);
                if (arr.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.RootElement.EnumerateArray())
                        res.Add(Flatten(el));
                    return res;
                }
            }
            catch { /* 落到逐行解析 */ }
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    res.Add(Flatten(doc.RootElement));
            }
            catch { /* 忽略非 JSON 行（如提示文本） */ }
        }
        return res;
    }

    private static Dictionary<string, string> Flatten(JsonElement el)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in el.EnumerateObject())
        {
            string v;
            if (p.Value.ValueKind == JsonValueKind.String) v = p.Value.GetString();
            else if (p.Value.ValueKind == JsonValueKind.Object || p.Value.ValueKind == JsonValueKind.Array)
                v = p.Value.GetRawText();
            else v = p.Value.ToString();
            d[Normalize(p.Name)] = v;
        }
        return d;
    }
}
