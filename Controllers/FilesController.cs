// Controllers/FilesController.cs
namespace SuperMegaSistemaEpi.Controllers;

using Microsoft.AspNetCore.Mvc;
using System.Runtime.InteropServices;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly string _root;

    public FilesController(IConfiguration cfg)
    {
        _root = Environment.GetEnvironmentVariable("UPLOAD_DIR") ?? "/tmp/uploads";
        Directory.CreateDirectory(_root);
    }

    [HttpPost]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Upload([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("Archivo vacío o clave incorrecta (debe ser 'file').");

        var safeName = Path.GetFileName(file.FileName);
        var path = Path.Combine(_root, safeName);

        try
        {
            await using var fs = System.IO.File.Create(path);
            await file.CopyToAsync(fs);
            return Ok(new { saved = safeName, path });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"No se pudo guardar: {ex.Message}");
        }
    }

    [HttpPost("consolidar")]
    public async Task<IActionResult> Consolidar([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("CSV vacío.");

        var acc = new Dictionary<(string Estab, string Ris), Contador>(capacity: 10000);

        await foreach (var r in ReadCsvAsync(file.OpenReadStream()))
        {
            var key = (r.Establecimiento, r.RIS);
            ref var c = ref CollectionsMarshal.GetValueRefOrAddDefault(acc, key, out _);
            c ??= new Contador();
            c.Casos += r.Casos;
        }

        var salida = acc.Select(kv => new
        {
            Establecimiento = kv.Key.Estab,
            RIS            = kv.Key.Ris,
            Casos          = kv.Value.Casos
        }).OrderBy(x => x.Establecimiento);

        return Ok(salida);
    }

    // 👇 ESTE MÉTODO VA *DENTRO* DE LA CLASE
    private static async IAsyncEnumerable<Registro> ReadCsvAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true
        };
        using var csv = new CsvReader(reader, cfg);
        await foreach (var r in csv.GetRecordsAsync<Registro>())
            yield return r;
    }

    private sealed class Contador { public int Casos; }

    // Puedes dejar este record dentro o fuera de la clase (pero no métodos fuera)
    public sealed record Registro(string Establecimiento, string RIS, int Casos);
}
