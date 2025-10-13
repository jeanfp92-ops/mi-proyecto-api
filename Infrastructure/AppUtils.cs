// Infrastructure/AppUtils.cs
namespace SuperMegaSistemaEpi;

using System.Text;
using System.Text.RegularExpressions;

internal static class AppUtils
{
    // ===== Helpers de archivos =====
    internal static string BaseDir() => AppContext.BaseDirectory;

    internal static string UploadsDir()
    {
        // En Render escribe en /tmp o en un Disk, nunca en /app
        var env = Environment.GetEnvironmentVariable("UPLOAD_DIR");
        var d = string.IsNullOrWhiteSpace(env)
            ? "/tmp/uploads"
            : env;

        Directory.CreateDirectory(d);
        return d;
    }

    internal static string DataDir()
    {
        // Data “semilla” para leer (OK que esté en /app, solo-lectura)
        var d = Path.Combine(BaseDir(), "data");
        Directory.CreateDirectory(d); // no falla si ya existe
        return d;
    }

    // Usa uploads si ya subiste CSV; de lo contrario usa data
    internal static string ResolveDataDir()
    {
        bool HasCsv(string dir) =>
            File.Exists(Path.Combine(dir, "iras.csv")) ||
            File.Exists(Path.Combine(dir, "edas.csv")) ||
            File.Exists(Path.Combine(dir, "febriles.csv")) ||
            File.Exists(Path.Combine(dir, "individual.csv")) ||
            File.Exists(Path.Combine(dir, "eess_maestro.csv"));

        var up = UploadsDir();
        var da = DataDir();
        return HasCsv(up) ? up : da;
    }

    // ========= CSV utils =========
    // Lee CSV (auto ; o ,). Headers -> minúsculas
    internal static List<Dictionary<string, object>> ReadCsv(string path)
    {
        var rows = new List<Dictionary<string, object>>();
        if (!File.Exists(path)) return rows;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headerLine = sr.ReadLine();
        if (headerLine is null) return rows;

        char sep = headerLine.Contains(';') ? ';' : ',';
        var headers = headerLine.Split(sep).Select(h => h.Trim().ToLowerInvariant()).ToArray();

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(sep);
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
                dict[headers[i]] = i < cols.Length ? cols[i] : "";
            rows.Add(dict);
        }
        return rows;
    }

    // Helpers de parseo
    internal static int? TryInt(Dictionary<string, object> r, string key)
    {
        if (!r.TryGetValue(key, out var obj)) return null;
        return int.TryParse(Convert.ToString(obj)?.Trim(), out var v) ? v : null;
    }

    internal static string? TryStr(Dictionary<string, object> r, string key)
    {
        if (!r.TryGetValue(key, out var obj)) return null;
        var s = Convert.ToString(obj);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // ====== Normalización y detección de RENAES ======
    internal static string? NormalizeRenaes(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim(), @"^\s*(\d{6})([A-Za-z])(\d{3})\s*$");
        if (!m.Success) return null;
        return $"{m.Groups[1].Value}{m.Groups[2].Value.ToUpper()}{m.Groups[3].Value}";
    }

    internal static string? GetEessCode(Dictionary<string, object> r)
    {
        foreach (var k in new[] { "renaes","e_salud","e_sal","esalud","eess","cod_eess","codigo_eess","codigo","establecimiento","sub_reg_nt" })
            if (r.TryGetValue(k, out var v))
            {
                var n = NormalizeRenaes(Convert.ToString(v));
                if (n != null) return n;
            }

        foreach (var kv in r)
        {
            var n = NormalizeRenaes(Convert.ToString(kv.Value));
            if (n != null) return n;
        }
        return null;
    }

    internal static int? GetUbigeoOrFromRenaes(Dictionary<string, object> r)
    {
        var u = TryInt(r, "ubigeo");
        if (u.HasValue) return u.Value;

        var code = GetEessCode(r);
        if (code != null && code.Length >= 6 && int.TryParse(code.Substring(0, 6), out var uu))
            return uu;

        return null;
    }

    internal static double SumAny(IEnumerable<Dictionary<string, object>> rows, params string[] keysOrPrefixes)
    {
        double s = 0;
        foreach (var r in rows)
            foreach (var kv in r)
            {
                var k = kv.Key.ToLowerInvariant();
                foreach (var p in keysOrPrefixes)
                {
                    var pp = p.ToLowerInvariant();
                    if (k == pp || k.StartsWith(pp))
                    {
                        if (double.TryParse(Convert.ToString(kv.Value)?.Trim(), out var v))
                            s += v;
                        break;
                    }
                }
            }
        return s;
    }

    // ======= Maestro helpers =======
    internal static string? MaestroGetCode(Dictionary<string, object> r)
    {
        var c = TryStr(r, "renaes") ?? TryStr(r, "e_salud");
        var n = NormalizeRenaes(c);
        if (n != null) return n;

        foreach (var kv in r)
        {
            var nx = NormalizeRenaes(Convert.ToString(kv.Value));
            if (nx != null) return nx;
        }
        return null;
    }

    internal static string MaestroGetNombre(Dictionary<string, object> r)
    {
        var s = TryStr(r, "raz_soc")
             ?? TryStr(r, "establecimiento")
             ?? TryStr(r, "nombre")
             ?? TryStr(r, "nom_estab")
             ?? TryStr(r, "nombre_establecimiento");
        s = s?.Trim();
        return string.IsNullOrWhiteSpace(s) ? (MaestroGetCode(r) ?? "") : s!;
    }

    internal static string? MaestroGetRIS(Dictionary<string, object> r)
    {
        var s = TryStr(r, "RIS") ?? TryStr(r, "ris") ?? TryStr(r, "Ris");
        if (!string.IsNullOrWhiteSpace(s))
        {
            s = Regex.Replace(s.Trim(), @"^\s*\d+\s*", "");
            return s;
        }
        var sub = TryStr(r, "subregion")?.Trim();
        return string.IsNullOrWhiteSpace(sub) ? null : sub;
    }

    internal static int? MaestroGetUbigeo(Dictionary<string, object> r)
    {
        var s = (TryStr(r, "ubigeo_rn") ?? TryStr(r, "ubigeo"))?.Trim();
        if (int.TryParse(s, out var v)) return v;

        var code = MaestroGetCode(r);
        if (!string.IsNullOrEmpty(code) && code.Length >= 6 && int.TryParse(code.Substring(0, 6), out var vv))
            return vv;

        return null;
    }
}
