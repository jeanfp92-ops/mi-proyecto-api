# Super Mega Sistema — Epidemiología (MVP)

Carga 4 CSV y genera una tabla consolidada por **establecimiento**:

- **iras.csv**: columnas sugeridas `establecimiento, ira, neumonias, sob_asma`
- **edas.csv**: columnas sugeridas `establecimiento, eda_acuosa, disenterica`
- **febriles.csv**: columnas sugeridas `establecimiento, feb`
- **individual.csv** (opcional): lista maestra de establecimientos (`establecimiento`) para detectar **no notificados**

> El normalizador acepta variantes de encabezados: acentos/espacios/puntos se ignoran
> (ej. `SOB.ASMA` → `sob_asma`, `EDA ACUOSA` → `eda_acuosa`).

## Ejecutar
```bash
dotnet restore
dotnet run
```
- Frontend: http://localhost:5000
- APIs/Swagger: http://localhost:5000/swagger
