// Program.cs — .NET 8 Minimal API
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

// Dev en puerto 5000
if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseUrls("http://localhost:5000");
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRouting(o => o.LowercaseUrls = true);

// Rendimiento
builder.Services.AddResponseCompression(o => { o.EnableForHttps = true; });
builder.Services.AddMemoryCache();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles(); // sirve index.html/dashboard.html primero si existe
app.UseStaticFiles();
app.UseResponseCompression();

// ===== Helpers de archivos =====
static string BaseDir() => AppContext.BaseDirectory;

// En Render, solo /tmp es escribible; si defines UPLOAD_DIR (p.ej. /data/uploads con Disk), se usa eso.
static string UploadsDir()
{
    var d = Environment.GetEnvironmentVariable("UPLOAD_DIR");
    if (string.IsNullOrWhiteSpace(d)) d = "/tmp/uploads";
    Directory.CreateDirectory(d);
    return d;
}

// Datos "semilla" (de solo lectura). No crear en runtime (el FS /app es read-only en Render).
static string DataDir()
{
    var d = Path.Combine(BaseDir(), "data");
    return d;
}

// Usa uploads si ya subiste CSV; de lo contrario usa data
static string ResolveDataDir()
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

// Lee CSV (auto-detecta ; o ,). Headers -> minúsculas
static List<Dictionary<string, object>> ReadCsv(string path)
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
        if (line.Length > 200_000) continue; // descarta líneas absurdamente largas

        var cols = line.Split(sep);
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            dict[headers[i]] = i < cols.Length ? cols[i] : "";
        }
        rows.Add(dict);
    }
    return rows;
}

// Helpers de parseo
static int? TryInt(Dictionary<string, object> r, string key)
{
    if (!r.TryGetValue(key, out var obj)) return null;
    if (int.TryParse(Convert.ToString(obj)?.Trim(), out var v)) return v;
    return null;
}
static string? TryStr(Dictionary<string, object> r, string key)
{
    if (!r.TryGetValue(key, out var obj)) return null;
    var s = Convert.ToString(obj);
    return string.IsNullOrWhiteSpace(s) ? null : s;
}

// ====== Normalización/detección de RENAES SIN REGEX ======

// Normaliza capturando 6 dígitos + 1 letra + 3 dígitos (ej: 150140D101)
static string? TryNormalizeRenaesFast(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return null;

    // Filtra solo [0-9A-Za-z], toUpper y corta a 32
    Span<char> buf = stackalloc char[32];
    int n = 0;
    foreach (var ch in s)
    {
        if (char.IsLetterOrDigit(ch))
        {
            if (n >= buf.Length) break;
            buf[n++] = char.ToUpperInvariant(ch);
        }
    }
    if (n < 10) return null;

    // Ventana deslizante buscando 6D + 1L + 3D
    for (int i = 0; i + 10 <= n; i++)
    {
        bool ok =
            char.IsDigit(buf[i+0]) && char.IsDigit(buf[i+1]) && char.IsDigit(buf[i+2]) &&
            char.IsDigit(buf[i+3]) && char.IsDigit(buf[i+4]) && char.IsDigit(buf[i+5]) &&
            char.IsLetter(buf[i+6]) &&
            char.IsDigit(buf[i+7]) && char.IsDigit(buf[i+8]) && char.IsDigit(buf[i+9]);

        if (ok)
            return new string(new[] {
                buf[i+0], buf[i+1], buf[i+2], buf[i+3], buf[i+4], buf[i+5],
                buf[i+6], buf[i+7], buf[i+8], buf[i+9]
            });
    }
    return null;
}

// Detección robusta del código EESS en FUENTES (iras/edas/febriles) sin regex global
static string? GetEessCode(Dictionary<string, object> r)
{
    string? TryKey(string k)
        => r.TryGetValue(k, out var v) ? TryNormalizeRenaesFast(Convert.ToString(v)) : null;

    // 1) llaves típicas
    foreach (var k in new[] { "renaes","e_salud","e_sal","eess","cod_eess","codigo_eess","codigo","sub_reg_nt" })
    {
        var x = TryKey(k);
        if (x != null) return x;
    }

    // 2) Fallback acotado: primeras 5 celdas "cortas"
    int seen = 0;
    foreach (var kv in r)
    {
        if (++seen > 5) break;
        var val = Convert.ToString(kv.Value) ?? "";
        if (val.Length > 32) continue;
        var n = TryNormalizeRenaesFast(val);
        if (n != null) return n;
    }
    return null;
}

// ======= Maestro helpers (para eess_maestro.csv) =======
static string? MaestroGetCode(Dictionary<string, object> r)
{
    var c = TryStr(r, "renaes") ?? TryStr(r, "e_salud");
    var n = TryNormalizeRenaesFast(c);
    if (n != null) return n;

    int seen = 0;
    foreach (var kv in r)
    {
        if (++seen > 5) break;
        var s = Convert.ToString(kv.Value);
        if (s != null && s.Length <= 32)
        {
            var nx = TryNormalizeRenaesFast(s);
            if (nx != null) return nx;
        }
    }
    return null;
}

static string MaestroGetNombre(Dictionary<string, object> r)
{
    var s = TryStr(r, "raz_soc")
         ?? TryStr(r, "establecimiento")
         ?? TryStr(r, "nombre")
         ?? TryStr(r, "nom_estab")
         ?? TryStr(r, "nombre_establecimiento");
    s = s?.Trim();
    return string.IsNullOrWhiteSpace(s) ? (MaestroGetCode(r) ?? "") : s!;
}

static string? MaestroGetRIS(Dictionary<string, object> r)
{
    var s = TryStr(r, "RIS") ?? TryStr(r, "ris") ?? TryStr(r, "Ris");
    if (!string.IsNullOrWhiteSpace(s))
    {
        s = s.Trim();
        // quita prefijo numérico (sin Regex)
        int i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return s.Substring(i);
    }
    var sub = TryStr(r, "subregion")?.Trim();
    return string.IsNullOrWhiteSpace(sub) ? null : sub;
}

static int? MaestroGetUbigeo(Dictionary<string, object> r)
{
    var s = (TryStr(r, "ubigeo_rn") ?? TryStr(r, "ubigeo"))?.Trim();
    if (int.TryParse(s, out var v)) return v;

    var code = MaestroGetCode(r);
    if (!string.IsNullOrEmpty(code) && code.Length >= 6 && int.TryParse(code.Substring(0, 6), out var vv))
        return vv;

    return null;
}

// ========= CACHE DE DATOS (iras/edas/feb/maestro) =========
// En top-level no se permiten campos estáticos sueltos. Los metemos en una clase estática.
static class CacheState
{
    public static readonly object Lock = new();

    public static (
        List<Dictionary<string,object>> iras,
        List<Dictionary<string,object>> edas,
        List<Dictionary<string,object>> febs,
        Dictionary<string, Dictionary<string,object>> maestroByCode,
        (DateTime ti,DateTime te,DateTime tf,DateTime tm) mtimes
    )? Snapshot;
}

static (List<Dictionary<string,object>> iras,
        List<Dictionary<string,object>> edas,
        List<Dictionary<string,object>> febs,
        Dictionary<string, Dictionary<string,object>> maestroByCode) GetSnapshot()
{
    string dir = ResolveDataDir();

    string pI = Path.Combine(dir, "iras.csv");
    string pE = Path.Combine(dir, "edas.csv");
    string pF = Path.Combine(dir, "febriles.csv");
    string pM = Path.Combine(dir, "eess_maestro.csv");

    var mI = File.Exists(pI) ? File.GetLastWriteTimeUtc(pI) : DateTime.MinValue;
    var mE = File.Exists(pE) ? File.GetLastWriteTimeUtc(pE) : DateTime.MinValue;
    var mF = File.Exists(pF) ? File.GetLastWriteTimeUtc(pF) : DateTime.MinValue;
    var mM = File.Exists(pM) ? File.GetLastWriteTimeUtc(pM) : DateTime.MinValue;

    lock (CacheState.Lock)
    {
        if (CacheState.Snapshot is null || CacheState.Snapshot.Value.mtimes != (mI,mE,mF,mM))
        {
            var iras = ReadCsv(pI);
            var edas = ReadCsv(pE);
            var febs = ReadCsv(pF);
            var maestroRows = ReadCsv(pM);

            var maestroByCode = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in maestroRows)
            {
                var code = MaestroGetCode(r)?.Trim();
                if (!string.IsNullOrEmpty(code)) maestroByCode[code] = r;
            }

            CacheState.Snapshot = (iras, edas, febs, maestroByCode, (mI,mE,mF,mM));
        }

        return (CacheState.Snapshot.Value.iras,
                CacheState.Snapshot.Value.edas,
                CacheState.Snapshot.Value.febs,
                CacheState.Snapshot.Value.maestroByCode);
    }
}

static void InvalidateSnapshot()
{
    lock (CacheState.Lock) { CacheState.Snapshot = null; }
}

// ======= Endpoints básicos =======
app.MapGet("/health", () => Results.Ok(new { ok = true }));

// Catálogo de RIS (desde eess_maestro.csv)
app.MapGet("/api/epi/ris-options", () =>
{
    var (_, _, _, maestroByCode) = GetSnapshot();

    var options = maestroByCode.Values
        .Select(r => MaestroGetRIS(r))
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s)
        .ToList();

    return Results.Ok(new { options });
});

// ========================= PIVOT SEMANAL POR VIGILANCIA =========================
app.MapGet("/api/epi/pivot", (
    int ano,
    string indicator,
    string groupBy = "estab",
    int semana_ini = 1,
    int semana_fin = 53,
    string? ris = null
) =>
{
    indicator = indicator.ToUpperInvariant();
    groupBy   = groupBy.ToLowerInvariant();
    semana_ini = Math.Max(1, Math.Min(53, semana_ini));
    semana_fin = Math.Max(semana_ini, Math.Min(53, semana_fin));

    // usar cache
    var (iras, edas, febs, maestroByCode) = GetSnapshot();

    IEnumerable<Dictionary<string, object>> fuente = indicator switch
    {
        "EDA"     => edas,
        "IRA"     => iras,
        "NEU"     => iras,
        "SOBASMA" => iras,
        "FEB"     => febs,
        _         => edas
    };

    double ValorFila(Dictionary<string, object> r)
    {
        return indicator switch
        {
            "EDA"     => SumAny(new[] { r }, "daa_", "eda_acuosa", "eda") + SumAny(new[] { r }, "dis_", "disenterica"),
            "IRA"     => SumAny(new[] { r }, "ira_", "ira"),
            "NEU"     => SumAny(new[] { r }, "neu_", "neumonia", "neumonias"),
            "SOBASMA" => SumAny(new[] { r }, "sob_", "sob_asma", "asma"),
            "FEB"     => (SumAny(new[] { r }, "feb_tot") is var t && t > 0) ? t : SumAny(new[] { r }, "feb_", "feb"),
            _         => 0
        };
    }

    int weeks = semana_fin - semana_ini + 1;
    var series = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
    var labelCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var row in fuente)
    {
        int ra = TryInt(row, "ano") ?? TryInt(row, "anio") ?? TryInt(row, "año") ?? -1;
        if (ra != ano) continue;

        int se = TryInt(row, "semana") ?? TryInt(row, "se") ?? -1;
        if (se < semana_ini || se > semana_fin) continue;

        var code = GetEessCode(row);
        if (string.IsNullOrEmpty(code)) continue;

        maestroByCode.TryGetValue(code!, out var m);
        var ris_m = MaestroGetRIS(m ?? new())?.Trim();
        if (!string.IsNullOrWhiteSpace(ris) && !string.Equals(ris_m, ris.Trim(), StringComparison.OrdinalIgnoreCase))
            continue;

        string key = groupBy == "ris"
            ? (ris_m ?? "SIN RED")
            : (MaestroGetNombre(m ?? new()) ?? code!);

        if (!series.TryGetValue(key, out var vec))
        {
            vec = new double[weeks];
            series[key] = vec;
            if (groupBy == "estab") labelCode[key] = code!;
        }

        vec[se - semana_ini] += ValorFila(row);
    }

    var listado = series
        .Select(kv => new {
            label = kv.Key,
            code  = labelCode.TryGetValue(kv.Key, out var c) ? c : null,
            values = kv.Value,
            total  = (int)Math.Round(kv.Value.Sum())
        })
        .OrderByDescending(x => x.total)
        .ToList();

    var totalesCol = new int[weeks];
    foreach (var it in listado)
        for (int i = 0; i < weeks; i++)
            totalesCol[i] += (int)Math.Round(it.values[i]);

    var payload = new {
        indicator,
        groupBy,
        semanas = Enumerable.Range(semana_ini, weeks).ToArray(),
        rows = listado.Select(x => new {
            label = x.label,
            code  = x.code,
            values = x.values.Select(v => (int)Math.Round(v)).ToArray(),
            total = x.total
        }),
        totalFila = new {
            label = "TOTAL",
            values = totalesCol,
            total  = totalesCol.Sum()
        }
    };

    return Results.Json(payload);
});
// ======================= /PIVOT SEMANAL POR VIGILANCIA =========================


// ========= ENDPOINTS =========
// 1) Subida segura de CSVs a /uploads
// Acepta: iras.csv, edas.csv, febriles.csv, individual.csv, eess_maestro.csv
app.MapPost("/api/upload", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var files = form.Files;
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "iras.csv","edas.csv","febriles.csv","individual.csv","eess_maestro.csv" };

    var root = UploadsDir();
    var saved = new List<object>();

    foreach (var file in files)
    {
        if (file.Length == 0) continue;

        // Límite defensivo (20 MB)
        if (file.Length > 20 * 1024 * 1024)
            return Results.BadRequest("Archivo supera 20 MB.");

        var safeName = Path.GetFileName(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(safeName))
            return Results.BadRequest($"Nombre no permitido: {safeName}");

        var tmp = Path.Combine(root, $"{Guid.NewGuid()}_{safeName}");
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(fs);
        }

        var dest = Path.Combine(root, safeName);
        try { File.Move(tmp, dest, overwrite: true); }
        catch
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* ignore */ }
            File.Move(tmp, dest);
        }

        saved.Add(new { file = safeName, path = dest });
    }

    // fuerza recarga del snapshot en la próxima consulta
    InvalidateSnapshot();

    return Results.Ok(new { ok = true, saved, count = saved.Count });
});

// 2) Listar archivos guardados en /uploads
app.MapGet("/api/files", () =>
{
    var root = UploadsDir();
    var files = Directory.EnumerateFiles(root, "*.csv")
        .Select(p => new { name = Path.GetFileName(p), size = new FileInfo(p).Length })
        .OrderBy(x => x.name);
    return Results.Ok(files);
});

// 3) Resumen por EESS (con maestro y filtros) — SIN UBIGEO
app.MapGet("/api/epi/summary", (
    int ano,
    int semana,
    string? ris,           // ej: "RIS VMT"
    bool includeAll = true // incluir EESS del maestro aunque no notifiquen
) =>
{
    try
    {
        // usar cache
        var (iras, edas, febs, maestroByCode) = GetSnapshot();

        bool PassRis(Dictionary<string, object>? m)
        {
            if (string.IsNullOrWhiteSpace(ris)) return true;
            var r = MaestroGetRIS(m ?? new());
            return string.Equals(r?.Trim(), ris.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // EESS que notifican en la SE (por RENAES detectado)
        HashSet<string> eessNotificadores = new(StringComparer.OrdinalIgnoreCase);
        foreach (var src in new[] { iras, edas, febs })
        foreach (var row in src)
        {
            int ra = TryInt(row, "ano") ?? TryInt(row, "anio") ?? TryInt(row, "año") ?? -1;
            int rs = TryInt(row, "semana") ?? TryInt(row, "se") ?? -1;
            string? es = GetEessCode(row);
            if (ra == ano && rs == semana && !string.IsNullOrWhiteSpace(es))
                eessNotificadores.Add(es!);
        }

        // Universo final (respeta filtro RIS si tenemos maestro)
        HashSet<string> universe = new(StringComparer.OrdinalIgnoreCase);

        if (includeAll)
        {
            foreach (var kv in maestroByCode)
                if (PassRis(kv.Value)) universe.Add(kv.Key);
        }

        foreach (var es in eessNotificadores)
        {
            if (maestroByCode.TryGetValue(es, out var m))
            {
                if (PassRis(m)) universe.Add(es);
            }
            else if (string.IsNullOrWhiteSpace(ris))
            {
                universe.Add(es);
            }
        }

        // Filtro por fuente/EESS
        IEnumerable<Dictionary<string, object>> FilterSrc(IEnumerable<Dictionary<string, object>> rows, string es) =>
            rows.Where(r =>
            {
                int ra = TryInt(r, "ano") ?? TryInt(r, "anio") ?? TryInt(r, "año") ?? -1;
                int rs = TryInt(r, "semana") ?? TryInt(r, "se") ?? -1;
                string? e = GetEessCode(r);
                return ra == ano && rs == semana &&
                       !string.IsNullOrWhiteSpace(e) &&
                       string.Equals(e, es, StringComparison.OrdinalIgnoreCase);
            });

        var filas = new List<object>();
        int notifYes = 0, notifNo = 0;
        double totIra=0, totNeu=0, totSob=0, totDaa=0, totDis=0, totFeb=0;

        foreach (var es in universe.OrderBy(x =>
        {
            maestroByCode.TryGetValue(x, out var m);
            var r = MaestroGetRIS(m ?? new()) ?? "";
            var n = MaestroGetNombre(m ?? new());
            return $"{r}__{n}";
        }))
        {
            // sumas
            double ira = 0, neu = 0, sob = 0;
            foreach (var r in FilterSrc(iras, es))
            {
                ira += SumAny(new[] { r }, "ira_", "ira");
                neu += SumAny(new[] { r }, "neu_", "neumonia", "neumonias");
                sob += SumAny(new[] { r }, "sob_", "sob_asma", "asma");
            }

            double daa = 0, dis = 0;
            foreach (var r in FilterSrc(edas, es))
            {
                daa += SumAny(new[] { r }, "daa_", "eda_acuosa", "eda");
                dis += SumAny(new[] { r }, "dis_", "disenterica");
            }

            double febTot = 0;
            foreach (var r in FilterSrc(febs, es))
            {
                var ft = SumAny(new[] { r }, "feb_tot");
                febTot += ft > 0 ? ft : SumAny(new[] { r }, "feb_", "feb");
            }

            var totalAll = ira + neu + sob + daa + dis + febTot;
            bool notificado = totalAll > 0;

            if (notificado) notifYes++; else notifNo++;

            totIra += ira; totNeu += neu; totSob += sob; totDaa += daa; totDis += dis; totFeb += febTot;

            maestroByCode.TryGetValue(es, out var mrow);
            var nombre = MaestroGetNombre(mrow ?? new());
            var risTxt = MaestroGetRIS(mrow ?? new()) ?? "";

            filas.Add(new {
                renaes = es,
                establecimiento = string.IsNullOrWhiteSpace(nombre) ? es : nombre,
                ris = risTxt,
                ubigeo = MaestroGetUbigeo(mrow ?? new()),
                ira = (int)ira,
                neumonias = (int)neu,
                sob_asma = (int)sob,
                eda_acuosa = (int)daa,
                disenterica = (int)dis,
                feb = (int)febTot,
                notificado = notificado
            });
        }

        var result = new {
            ano, semana,
            ubigeo = (int?)null, // compatibilidad
            ris, includeAll,
            conteo_estab_notificados = notifYes,
            conteo_estab_no_notificados = notifNo,
            totales = new {
                ira = (int)totIra, neumonias = (int)totNeu, sob_asma = (int)totSob,
                eda_acuosa = (int)totDaa, disenterica = (int)totDis, feb = (int)totFeb
            },
            filas
        };
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Summary error", detail: ex.ToString(), statusCode: 500);
    }
});

// 4) Diagnóstico de maestro: vacíos y duplicados
app.MapGet("/api/diag/maestro-issues", () =>
{
    var (_, _, _, maestroByCode) = GetSnapshot();

    var duplicados = maestroByCode
        .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => new { renaes = g.Key, count = g.Count() })
        .OrderByDescending(x => x.count)
        .ThenBy(x => x.renaes)
        .ToList();

    return Results.Ok(new { vacios = 0, duplicados });
});

// 5) ===== Reporte “Tabla notificante por establecimiento” =====
static string UltimoReporteCsvPath()
{
    // Guardar SIEMPRE en carpeta escribible
    return Path.Combine(UploadsDir(), "tablas_notificante.csv");
}

static void GuardarCsv(RespuestaReporte rep)
{
    var path = UltimoReporteCsvPath();
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var sw = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    sw.WriteLine($"SEMANA EPIDEMIOLÓGICA {rep.semana};AÑO {rep.anio};UBIGEO {rep.ubigeo};RIS {rep.ris}");
    sw.WriteLine($"ESTABLECIMIENTOS NOTIFICADOS;{rep.establecimientos_notificados}");
    sw.WriteLine($"ESTABLECIMIENTOS NO NOTIFICADOS;{rep.establecimientos_no_notificados}");
    sw.WriteLine();
    sw.WriteLine("RIS;ESTABLECIMIENTO;RENAES;IRA;NEUMONIAS;SOB.ASMA;EDA ACUOSA;DISENTERICA;FEB;OBSERVACIONES");

    foreach (var f in rep.filas)
    {
        sw.WriteLine(string.Join(';', new[]{
            f.ris, f.establecimiento, f.renaes,
            f.ira.ToString(CultureInfo.InvariantCulture),
            f.neumonias.ToString(CultureInfo.InvariantCulture),
            f.sob_asma.ToString(CultureInfo.InvariantCulture),
            f.eda_acuosa.ToString(CultureInfo.InvariantCulture),
            f.disenterica.ToString(CultureInfo.InvariantCulture),
            f.feb.ToString(CultureInfo.InvariantCulture),
            f.observaciones
        }));
    }
}

app.MapPost("/api/reporte/notificacion-semanal", (PayloadReporte payload) =>
{
    var (_, _, _, maestroByCode) = GetSnapshot();

    var maestro = maestroByCode.Values
        .Select(r => new {
            renaes = MaestroGetCode(r)?.Trim() ?? "",
            nombre = MaestroGetNombre(r),
            ris = MaestroGetRIS(r) ?? "",
            ubigeo = MaestroGetUbigeo(r)
        })
        .Where(x => !string.IsNullOrEmpty(x.renaes))
        .ToList();

    var mFiltrado = maestro.Where(m =>
        (string.IsNullOrWhiteSpace(payload.ubigeo) || (m.ubigeo.HasValue && m.ubigeo.Value.ToString().StartsWith(payload.ubigeo))) &&
        (string.IsNullOrWhiteSpace(payload.ris) || string.Equals(m.ris, payload.ris, StringComparison.OrdinalIgnoreCase))
    ).OrderBy(m => m.ris).ThenBy(m => m.nombre).ToList();

    var dic = payload.filas
        .GroupBy(x => TryNormalizeRenaesFast(x.renaes.Trim()) ?? x.renaes.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => new {
            ira = g.Sum(z => z.ira),
            neumonias = g.Sum(z => z.neumonias),
            sob_asma = g.Sum(z => z.sob_asma),
            eda_acuosa = g.Sum(z => z.eda_acuosa),
            disenterica = g.Sum(z => z.disenterica),
            feb = g.Sum(z => z.feb)
        }, StringComparer.OrdinalIgnoreCase);

    var filas = new List<FilaSalida>();
    int notificados = 0, noNotificados = 0;

    foreach (var e in mFiltrado)
    {
        dic.TryGetValue(e.renaes, out var v);
        int ira = v?.ira ?? 0;
        int neu = v?.neumonias ?? 0;
        int sob = v?.sob_asma ?? 0;
        int daa = v?.eda_acuosa ?? 0;
        int dis = v?.disenterica ?? 0;
        int feb = v?.feb ?? 0;

        bool notificado = (ira + neu + sob + daa + dis + feb) > 0;
        if (notificado) notificados++; else noNotificados++;

        filas.Add(new FilaSalida(
            ris: e.ris,
            establecimiento: e.nombre,
            renaes: e.renaes,
            ira, neu, sob, daa, dis, feb,
            observaciones: ""
        ));
    }

    var resp = new RespuestaReporte(
        anio: payload.anio,
        semana: payload.semana,
        ubigeo: payload.ubigeo,
        ris: payload.ris,
        total_establecimientos: filas.Count,
        establecimientos_notificados: notificados,
        establecimientos_no_notificados: noNotificados,
        filas: filas
    );

    GuardarCsv(resp);
    return Results.Json(resp);
});

app.MapGet("/api/reporte/notificacion-semanal.csv", () =>
{
    var path = UltimoReporteCsvPath();
    if (!File.Exists(path))
        return Results.NotFound("Aún no has generado el reporte. Llama primero a POST /api/reporte/notificacion-semanal");

    var bytes = File.ReadAllBytes(path);
    return Results.File(bytes, "text/csv; charset=utf-8", "tablas_notificante.csv");
});

app.Run();


// ===================== Tipos usados por el endpoint de reporte =====================
record FilaConsolidado(string renaes, int ira, int neumonias, int sob_asma, int eda_acuosa, int disenterica, int feb);
record PayloadReporte(int anio, int semana, string ubigeo, string? ris, List<FilaConsolidado> filas);
record FilaSalida(string ris, string establecimiento, string renaes,
                  int ira, int neumonias, int sob_asma, int eda_acuosa, int disenterica, int feb,
                  string observaciones);
record RespuestaReporte(int anio, int semana, string ubigeo, string? ris,
                        int total_establecimientos, int establecimientos_notificados, int establecimientos_no_notificados,
                        List<FilaSalida> filas);
