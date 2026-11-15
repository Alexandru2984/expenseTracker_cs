namespace ExpenseTracker.Api.Infrastructure;

/// <summary>
/// Minimal RFC-4180-ish CSV reader: handles quoted fields, escaped quotes ("")
/// and commas/newlines inside quotes. Enough to round-trip what we export.
/// </summary>
public static class Csv
{
    /// <summary>
    /// Quotes a CSV field and neutralizes spreadsheet formula injection
    /// (values starting with = + - @ or control chars are prefixed with ').
    /// </summary>
    public static string Field(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && (char.IsControl(value[0]) || value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static List<List<string>> Parse(string content, int maxRows = 5001, int maxColumns = 32)
    {
        var rows = new List<List<string>>();
        var field = new System.Text.StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var closedQuote = false;

        void AddField()
        {
            row.Add(field.ToString());
            field.Clear();
            closedQuote = false;
            if (row.Count > maxColumns) throw new FormatException($"Maximum {maxColumns} coloane CSV.");
        }
        void AddRow()
        {
            AddField();
            if (row.Count > 1 || !string.IsNullOrWhiteSpace(row[0])) rows.Add(row);
            if (rows.Count > maxRows) throw new FormatException($"Maximum {maxRows - 1} rânduri de date CSV.");
            row = new List<string>();
        }

        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else { inQuotes = false; closedQuote = true; }
                }
                else field.Append(c);
                continue;
            }

            if (closedQuote && c is not (',' or '\r' or '\n'))
                throw new FormatException("Caractere neașteptate după un câmp CSV între ghilimele.");

            switch (c)
            {
                case '"':
                    if (field.Length != 0) throw new FormatException("Ghilimele CSV într-un câmp neîncadrat.");
                    inQuotes = true;
                    break;
                case ',':
                    AddField();
                    break;
                case '\r':
                    if (i + 1 < content.Length && content[i + 1] == '\n') i++;
                    AddRow();
                    break;
                case '\n':
                    AddRow();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        // trailing field/row (no final newline)
        if (inQuotes) throw new FormatException("Câmp CSV cu ghilimele neînchise.");
        if (field.Length > 0 || row.Count > 0 || closedQuote) AddRow();

        // drop fully-empty rows
        rows.RemoveAll(r => r.Count == 1 && string.IsNullOrWhiteSpace(r[0]));
        return rows;
    }
}
