using System.Text;
namespace SuperMegaSistema_Epi.Utils;

public static class CsvUtils
{
    public static string Normalize(string s)
    {
        s ??= string.Empty;
        s = s.Trim().ToLowerInvariant();
        var map = new Dictionary<char,char>{
            ['á']='a', ['é']='e', ['í']='i', ['ó']='o', ['ú']='u',
            ['ä']='a', ['ë']='e', ['ï']='i', ['ö']='o', ['ü']='u',
            ['ñ']='n'
        };
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(map.GetValueOrDefault(ch, ch));
            else if (char.IsWhiteSpace(ch)) sb.Append('_');
            else if (ch=='.' || ch=='-' || ch=='/' ) sb.Append('_');
        }
        return sb.ToString().Replace("__","_");
    }

    // Tolerant CSV Reader (comma or semicolon)
    public static List<Dictionary<string,string>> ReadCsv(Stream stream)
    {
        using var sr = new StreamReader(stream, Encoding.UTF8, true);
        var all = new List<Dictionary<string,string>>();
        string? headerLine = sr.ReadLine();
        if (headerLine == null) return all;
        char sep = headerLine.Contains(';') && !headerLine.Contains(',') ? ';' : ',';
        var headers = headerLine.Split(sep).Select(h => Normalize(h)).ToArray();
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsv(line, sep, headers.Length);
            var row = new Dictionary<string,string>();
            for (int i=0;i<headers.Length;i++)
            {
                row[headers[i]] = i < parts.Count ? parts[i].Trim() : "";
            }
            all.Add(row);
        }
        return all;
    }

    private static List<string> SplitCsv(string line, char sep, int expectedCols)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i=0;i<line.Length;i++)
        {
            char c = line[i];
            if (c=='"') { inQuotes = !inQuotes; continue; }
            if (c==sep && !inQuotes) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        // pad
        while (res.Count < expectedCols) res.Add("");
        return res;
    }

    public static double ToDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s!.Replace(",",".");
        return double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
