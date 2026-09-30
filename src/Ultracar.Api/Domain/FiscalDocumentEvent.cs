namespace Ultracar.Api.Domain;

public class FiscalDocumentEvent
{
    public DateTimeOffset Date { get; set; }
    public FiscalDocumentStatus Status { get; set; }
    public int Attempt { get; set; }
    public string Message { get; set; } = string.Empty;
}
