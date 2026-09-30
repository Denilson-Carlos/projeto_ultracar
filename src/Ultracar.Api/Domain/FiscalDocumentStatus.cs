namespace Ultracar.Api.Domain;

/// <summary>
/// Status da solicitação de emissão do documento fiscal.
/// </summary>
public enum FiscalDocumentStatus
{
    Pending,
    Processing,
    WaitingRetry,
    Issued,
    Failed
}
