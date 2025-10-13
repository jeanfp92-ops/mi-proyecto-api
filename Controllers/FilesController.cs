// FilesController.cs
using System.Runtime.InteropServices; // <-- necesario para CollectionsMarshal
using CsvHelper;                      // si usas CsvHelper
using CsvHelper.Configuration;
using System.Globalization;

record Registro(string Establecimiento, string RIS, int Casos); // ajusta a tus columnas

static async IAsyncEnumerable<Registro> ReadCsvAsync(Stream stream)
{
    using var reader = new StreamReader(stream);
    var cfg = new CsvConfiguration(CultureInfo.InvariantCulture) { DetectDelimiter = true };
    using var csv = new CsvReader(reader, cfg);
    await foreach (var r in csv.GetRecordsAsync<Registro>()) yield return r;
}

[HttpPost("consolidar")]
public async Task<IActionResult> Consolidar([FromForm] IFormFile file)
{
    if (file == null || file.Length == 0) return BadRequest("CSV vacío.");

    // 🔥 Mini-patrón de agregación sin reventar memoria
    var acc = new Dictionary<(string Estab, string Ris), Contador>(capacity: 10000);
    await foreach (var r in ReadCsvAsync(file.OpenReadStream()))
    {
        var key = (r.Establecimiento, r.RIS);
        ref var c = ref CollectionsMarshal.GetValueRefOrAddDefault(acc, key, out _);
        c ??= new Contador();
        c.Casos += r.Casos; // suma lo que necesites (IRA/EDA/FEB, etc.)
    }

    // transforma a salida (lista para tu tabla)
    var salida = acc.Select(kv => new {
        Establecimiento = kv.Key.Estab,
        RIS = kv.Key.Ris,
        Casos = kv.Value.Casos
    }).OrderBy(x => x.Establecimiento);

    return Ok(salida);
}

class Contador
{
    public int Casos;
}
