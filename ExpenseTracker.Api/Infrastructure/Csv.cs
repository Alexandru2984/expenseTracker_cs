namespace ExpenseTracker.Api.Infrastructure;

/// <summary>
/// Minimal RFC-4180-ish CSV reader: handles quoted fields, escaped quotes ("")
/// and commas/newlines inside quotes. Enough to round-trip what we export.
/// </summary>
public static class Csv
{
    public static List<List<string>> Parse(string content)
    {
        var rows = new List<List<string>>();
        var field = new System.Text.StringBuilder();
        var row = new List<string>();
        var inQuotes = false;

        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
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
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        // trailing field/row (no final newline)
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        // drop fully-empty rows
        rows.RemoveAll(r => r.Count == 1 && string.IsNullOrWhiteSpace(r[0]));
        return rows;
    }
}
