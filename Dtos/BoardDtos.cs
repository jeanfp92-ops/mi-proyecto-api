namespace SuperMegaSistema_Epi.Dtos;

public record UploadResult(bool ok, string message);
public record BoardRow(string Establecimiento, int IRA, int Neumonias, int SobAsma, int EdaAcuosa, int Disenterica, int Feb);
public record BoardTotals(int IRA, int Neumonias, int SobAsma, int EdaAcuosa, int Disenterica, int Feb, int Notificados, int NoNotificados);
public record BoardPayload(List<BoardRow> Rows, BoardTotals Totals);
