using Microsoft.Extensions.Options;
using Ultracar.Api.Domain;
using Ultracar.Api.Integration;
using Ultracar.Api.Persistence;

namespace Ultracar.Api.Processing;

// Faz uma tentativa de emissão: chama a integradora com timeout, vê se a falha é
// temporária ou definitiva e agenda a próxima tentativa quando precisa.
public class FiscalDocumentProcessor
{
    private readonly IFiscalDocumentRepository _repository;
    private readonly IFiscalIntegrator _integrator;
    private readonly FiscalProcessingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FiscalDocumentProcessor> _logger;

    public FiscalDocumentProcessor(
        IFiscalDocumentRepository repository,
        IFiscalIntegrator integrator,
        IOptions<FiscalProcessingOptions> options,
        TimeProvider time,
        ILogger<FiscalDocumentProcessor> logger)
    {
        _repository = repository;
        _integrator = integrator;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task ProcessAsync(FiscalDocument document, CancellationToken cancellationToken)
    {
        document.StartAttempt(_time.GetUtcNow());
        await _repository.UpdateAsync(document, cancellationToken);

        _logger.LogInformation("Tentativa {Attempt} da solicitação {Id} (OS {WorkOrderId}).", document.Attempts, document.Id, document.WorkOrderId);

        var result = await CallIntegratorAsync(document, cancellationToken);

        ApplyResult(document, result);
        await _repository.UpdateAsync(document, CancellationToken.None);
    }

    private async Task<IntegrationResult> CallIntegratorAsync(FiscalDocument document, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.IntegratorTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            // o Id da solicitação vai como chave de idempotência. Se der timeout e tentarmos de novo,
            // a integradora devolve o documento que já emitiu em vez de emitir outro.
            return await _integrator.IssueAsync(document, document.Id.ToString("N"), linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return IntegrationResult.Transient("INTEGRATOR_TIMEOUT", $"A integradora não respondeu em {_options.IntegratorTimeout.TotalSeconds}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // erro que não conhecemos (rede, parsing...). A chamada é idempotente, então dá para tentar de novo.
            _logger.LogError(ex, "Erro inesperado ao chamar a integradora. Solicitação {Id}.", document.Id);
            return IntegrationResult.Transient("INTEGRATOR_ERROR", ex.Message);
        }
    }

    private void ApplyResult(FiscalDocument document, IntegrationResult result)
    {
        var now = _time.GetUtcNow();

        // aprovado sem número ou protocolo não dá para confiar, tratamos como temporário
        if (result.Outcome == IntegrationOutcome.Approved && (string.IsNullOrWhiteSpace(result.Number) || string.IsNullOrWhiteSpace(result.Protocol)))
            result = IntegrationResult.Transient("INCONSISTENT_RESPONSE", "A integradora aprovou, mas não mandou número ou protocolo.");

        if (result.Outcome == IntegrationOutcome.Approved)
        {
            _logger.LogInformation("Documento {Number} emitido para a solicitação {Id}.", result.Number, document.Id);
            document.Issue(result.Number!, result.Protocol!, now);
            return;
        }

        if (result.Outcome == IntegrationOutcome.PermanentError)
        {
            _logger.LogWarning("Emissão rejeitada ({ErrorCode}): {ErrorMessage}. Solicitação {Id}.", result.ErrorCode, result.ErrorMessage, document.Id);
            document.Fail(result.ErrorCode!, result.ErrorMessage!, now);
            return;
        }

        if (document.Attempts >= _options.MaxAttempts)
        {
            _logger.LogError("Tentativas esgotadas para a solicitação {Id}. Último erro: {ErrorMessage}", document.Id, result.ErrorMessage);
            document.Fail("RETRIES_EXHAUSTED", $"{document.Attempts} tentativas sem sucesso. Último erro: {result.ErrorMessage}", now);
            return;
        }

        var nextAttemptAt = now + GetRetryDelay(document.Attempts);
        _logger.LogWarning("Falha temporária ({ErrorCode}) na solicitação {Id}. Nova tentativa em {NextAttemptAt}.", result.ErrorCode, document.Id, nextAttemptAt);
        document.ScheduleRetry(result.ErrorCode!, result.ErrorMessage!, nextAttemptAt, now);
    }

    // espera dobra a cada tentativa (2s, 4s, 8s...) até o limite configurado
    private TimeSpan GetRetryDelay(int attempt)
    {
        var delay = _options.BaseRetryDelay * Math.Pow(2, attempt - 1);

        if (delay > _options.MaxRetryDelay)
            return _options.MaxRetryDelay;

        return delay;
    }
}
