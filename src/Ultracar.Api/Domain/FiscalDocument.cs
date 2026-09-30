namespace Ultracar.Api.Domain;

public class FiscalDocument
{
    public Guid Id { get; set; }
    public string WorkOrderId { get; set; } = string.Empty;
    public string CustomerDocument { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string MunicipalityCode { get; set; } = string.Empty;

    public FiscalDocumentStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }

    // preenchidos quando o documento é emitido
    public string? Number { get; set; }
    public string? Protocol { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }

    // última falha
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public List<FiscalDocumentEvent> History { get; set; } = new();

    public FiscalDocument(string workOrderId, string customerDocument, decimal amount, string municipalityCode, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        WorkOrderId = workOrderId;
        CustomerDocument = customerDocument;
        Amount = amount;
        MunicipalityCode = municipalityCode;
        Status = FiscalDocumentStatus.Pending;
        CreatedAt = now;
        UpdatedAt = now;

        AddHistory(now, "Solicitação registrada.");
    }

    public bool WillRetry =>
        Status == FiscalDocumentStatus.Pending ||
        Status == FiscalDocumentStatus.Processing ||
        Status == FiscalDocumentStatus.WaitingRetry;

    // pronto para o worker processar
    public bool IsDue(DateTimeOffset now)
    {
        if (Status != FiscalDocumentStatus.Pending && Status != FiscalDocumentStatus.WaitingRetry)
            return false;

        return NextAttemptAt == null || NextAttemptAt <= now;
    }

    public bool HasSameData(FiscalDocument other)
    {
        return CustomerDocument == other.CustomerDocument
            && Amount == other.Amount
            && MunicipalityCode == other.MunicipalityCode;
    }

    public void StartAttempt(DateTimeOffset now)
    {
        Status = FiscalDocumentStatus.Processing;
        Attempts++;
        NextAttemptAt = null;
        UpdatedAt = now;

        AddHistory(now, $"Tentativa {Attempts} iniciada.");
    }

    public void Issue(string number, string protocol, DateTimeOffset now)
    {
        Status = FiscalDocumentStatus.Issued;
        Number = number;
        Protocol = protocol;
        IssuedAt = now;
        ErrorCode = null;
        ErrorMessage = null;
        UpdatedAt = now;

        AddHistory(now, $"Documento {number} emitido, protocolo {protocol}.");
    }

    public void ScheduleRetry(string errorCode, string errorMessage, DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        Status = FiscalDocumentStatus.WaitingRetry;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        NextAttemptAt = nextAttemptAt;
        UpdatedAt = now;

        AddHistory(now, $"Falha temporária ({errorCode}): {errorMessage} Nova tentativa em {nextAttemptAt:dd/MM/yyyy HH:mm:ss}.");
    }

    public void Fail(string errorCode, string errorMessage, DateTimeOffset now)
    {
        Status = FiscalDocumentStatus.Failed;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        UpdatedAt = now;

        AddHistory(now, $"Falha definitiva ({errorCode}): {errorMessage}");
    }

    // o repositório trabalha com cópias, para a API não ler o objeto enquanto o worker altera
    public FiscalDocument Clone()
    {
        var copy = (FiscalDocument)MemberwiseClone();
        copy.History = new List<FiscalDocumentEvent>(History);
        return copy;
    }

    private void AddHistory(DateTimeOffset date, string message)
    {
        History.Add(new FiscalDocumentEvent
        {
            Date = date,
            Status = Status,
            Attempt = Attempts,
            Message = message
        });
    }
}
