using SuperMegaSistema_Epi.Utils;
using SuperMegaSistema_Epi.Dtos;

namespace SuperMegaSistema_Epi.Services;

public class Aggregator
{
    private readonly string _uploadDir;
    public Aggregator(string uploadDir) { _uploadDir = uploadDir; }

    private static string Pick(params string[] keys)
    {
        // returns normalized key priority list
        return keys.First();
    }

    private static string Norm(string s)=>CsvUtils.Normalize(s);

    public BoardPayload BuildBoard()
    {
        var rows = new Dictionary<string, BoardRow>(StringComparer.OrdinalIgnoreCase);

        // Helper to ensure row exists
        BoardRow Ensure(string est)
        {
            if (!rows.TryGetValue(est, out var r))
            {
                r = new BoardRow(est, 0,0,0,0,0,0);
                rows[est] = r;
            }
            return r;
        }
        BoardRow Set(BoardRow r) { rows[r.Establecimiento] = r; return r; }

        // iras.csv
        var irasPath = Path.Combine(_uploadDir, "iras.csv");
        if (File.Exists(irasPath))
        {
            using var fs = File.OpenRead(irasPath);
            var data = CsvUtils.ReadCsv(fs);
            foreach (var row in data)
            {
                var est = row.ContainsKey("establecimiento") ? row["establecimiento"] :
                          row.ContainsKey("eess") ? row["eess"] :
                          row.ContainsKey("ipress") ? row["ipress"] : "";
                if (string.IsNullOrWhiteSpace(est)) continue;
                var ira = row.TryGetValue("ira", out var iraS) ? CsvUtils.ToDouble(iraS) : 0;
                // posibles variantes
                var neum = row.TryGetValue("neumonias", out var n1) ? CsvUtils.ToDouble(n1) :
                           row.TryGetValue("neumonia", out var n2) ? CsvUtils.ToDouble(n2) : 0;
                var sob = row.TryGetValue("sob_asma", out var s1) ? CsvUtils.ToDouble(s1) :
                          row.TryGetValue("sobasma", out var s2) ? CsvUtils.ToDouble(s2) :
                          row.TryGetValue("sob_asma_sospecha", out var s3) ? CsvUtils.ToDouble(s3) : 0;
                var r = Ensure(est);
                r = r with { IRA = r.IRA + (int)ira, Neumonias = r.Neumonias + (int)neum, SobAsma = r.SobAsma + (int)sob };
                Set(r);
            }
        }

        // edas.csv
        var edasPath = Path.Combine(_uploadDir, "edas.csv");
        if (File.Exists(edasPath))
        {
            using var fs = File.OpenRead(edasPath);
            var data = CsvUtils.ReadCsv(fs);
            foreach (var row in data)
            {
                var est = row.ContainsKey("establecimiento") ? row["establecimiento"] :
                          row.ContainsKey("eess") ? row["eess"] :
                          row.ContainsKey("ipress") ? row["ipress"] : "";
                if (string.IsNullOrWhiteSpace(est)) continue;
                var acu = row.TryGetValue("eda_acuosa", out var a1) ? CsvUtils.ToDouble(a1) :
                          row.TryGetValue("eda", out var a2) ? CsvUtils.ToDouble(a2) : 0;
                var dis = row.TryGetValue("disenterica", out var d1) ? CsvUtils.ToDouble(d1) :
                          row.TryGetValue("disenteria", out var d2) ? CsvUtils.ToDouble(d2) : 0;
                var r = Ensure(est);
                r = r with { EdaAcuosa = r.EdaAcuosa + (int)acu, Disenterica = r.Disenterica + (int)dis };
                Set(r);
            }
        }

        // febriles.csv
        var febPath = Path.Combine(_uploadDir, "febriles.csv");
        if (File.Exists(febPath))
        {
            using var fs = File.OpenRead(febPath);
            var data = CsvUtils.ReadCsv(fs);
            foreach (var row in data)
            {
                var est = row.ContainsKey("establecimiento") ? row["establecimiento"] :
                          row.ContainsKey("eess") ? row["eess"] :
                          row.ContainsKey("ipress") ? row["ipress"] : "";
                if (string.IsNullOrWhiteSpace(est)) continue;
                var feb = row.TryGetValue("feb", out var f1) ? CsvUtils.ToDouble(f1) :
                          row.TryGetValue("febriles", out var f2) ? CsvUtils.ToDouble(f2) : 0;
                var r = Ensure(est);
                r = r with { Feb = r.Feb + (int)feb };
                Set(r);
            }
        }

        // Lista maestra (individual.csv) para detectar faltantes
        var master = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masterPath = Path.Combine(_uploadDir, "individual.csv");
        if (File.Exists(masterPath))
        {
            using var fs = File.OpenRead(masterPath);
            var data = CsvUtils.ReadCsv(fs);
            foreach (var row in data)
            {
                if (row.TryGetValue("establecimiento", out var e) && !string.IsNullOrWhiteSpace(e)) master.Add(e);
                else if (row.TryGetValue("eess", out var e2) && !string.IsNullOrWhiteSpace(e2)) master.Add(e2);
                else if (row.TryGetValue("ipress", out var e3) && !string.IsNullOrWhiteSpace(e3)) master.Add(e3);
            }
        }
        // Si no hay lista maestra, usamos las claves existentes como universo
        if (master.Count == 0) foreach (var k in rows.Keys) master.Add(k);

        // Añadir filas faltantes con ceros (no notificados)
        foreach (var est in master)
        {
            if (!rows.ContainsKey(est)) rows[est] = new BoardRow(est, 0,0,0,0,0,0);
        }

        // Ordenar por nombre y calcular totales
        var list = rows.Values.OrderBy(r => r.Establecimiento).ToList();
        int tIRA = list.Sum(r=>r.IRA);
        int tNeu = list.Sum(r=>r.Neumonias);
        int tSob = list.Sum(r=>r.SobAsma);
        int tAcu = list.Sum(r=>r.EdaAcuosa);
        int tDis = list.Sum(r=>r.Disenterica);
        int tFeb = list.Sum(r=>r.Feb);

        int notificados = list.Count(r => (r.IRA + r.Neumonias + r.SobAsma + r.EdaAcuosa + r.Disenterica + r.Feb) > 0);
        int noNotificados = list.Count - notificados;

        var totals = new BoardTotals(tIRA, tNeu, tSob, tAcu, tDis, tFeb, notificados, noNotificados);
        return new BoardPayload(list, totals);
    }
}
