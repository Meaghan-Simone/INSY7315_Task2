using System.Text;

namespace UncoveringGreatnessCRM.Helpers;

public static class CsvHelper
{
    /// <summary>Escapes a cell for CSV and neutralises spreadsheet formula injection (=, +, -, @ prefixes).</summary>
    public static string Cell(object? value)
    {
        var s = value switch
        {
            null => "",
            DateTime d => d.ToString("yyyy-MM-dd HH:mm"),
            decimal m => m.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public static byte[] Build(IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', headers.Select(h => Cell(h))));
        foreach (var row in rows) sb.AppendLine(string.Join(',', row.Select(Cell)));
        var preamble = new UTF8Encoding(true).GetPreamble(); // BOM so Excel opens UTF-8 correctly
        return preamble.Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    /// <summary>Minimal RFC 4180 parser (quoted fields, escaped quotes, embedded newlines).</summary>
    public static List<string[]> Parse(TextReader reader, int maxRows = 5001)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"' when field.Length == 0: inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
                    row.Clear();
                    if (rows.Count >= maxRows) return rows;
                    break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
        }
        return rows;
    }
}
