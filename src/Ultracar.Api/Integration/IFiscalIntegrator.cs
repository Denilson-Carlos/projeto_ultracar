using Ultracar.Api.Domain;

namespace Ultracar.Api.Integration;

public interface IFiscalIntegrator
{
    //  deixa seguro tentar de novo depois de um timeout.
    Task<IntegrationResult> IssueAsync(FiscalDocument document, string idempotencyKey, CancellationToken cancellationToken);
}

public enum IntegrationOutcome
{
    Approved,
    TransientError,
    PermanentError
}

public class IntegrationResult
{
    public IntegrationOutcome Outcome { get; set; }
    public string? Number { get; set; }
    public string? Protocol { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static IntegrationResult Approved(string? number, string? protocol)
    {
        return new IntegrationResult { Outcome = IntegrationOutcome.Approved, Number = number, Protocol = protocol };
    }

    public static IntegrationResult Transient(string errorCode, string errorMessage)
    {
        return new IntegrationResult { Outcome = IntegrationOutcome.TransientError, ErrorCode = errorCode, ErrorMessage = errorMessage };
    }

    public static IntegrationResult Permanent(string errorCode, string errorMessage)
    {
        return new IntegrationResult { Outcome = IntegrationOutcome.PermanentError, ErrorCode = errorCode, ErrorMessage = errorMessage };
    }
}
