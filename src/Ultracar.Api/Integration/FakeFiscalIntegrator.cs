using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Ultracar.Api.Domain;

namespace Ultracar.Api.Integration;

public class FakeIntegratorOptions
{
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan SlowLatency { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan HangTime { get; set; } = TimeSpan.FromMinutes(1);
    public int UnstableFailures { get; set; } = 2;
}

public class IssuedDocument
{
    public string Number { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
}

// Integradora simulada. O cenário é escolhido pelo final do workOrderId:
// -SLOW (demora), -TIMEOUT (emite mas não responde), -UNSTABLE (falha nas primeiras),DOWN (sempre fora), -REJECT (rejeitada) e -INCONSISTENT (aprova sem protocolo na 1a vez).
public class FakeFiscalIntegrator : IFiscalIntegrator
{
    private readonly FakeIntegratorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FakeFiscalIntegrator> _logger;

    // documentos já emitidos por chave de idempotência
    private readonly ConcurrentDictionary<string, IssuedDocument> _issued = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private int _sequence;

    public FakeFiscalIntegrator(IOptions<FakeIntegratorOptions> options, TimeProvider time, ILogger<FakeFiscalIntegrator> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    // usados nos testes
    public IReadOnlyDictionary<string, IssuedDocument> Issued => _issued;
    public int CallsFor(string idempotencyKey) => _calls.GetValueOrDefault(idempotencyKey);

    public async Task<IntegrationResult> IssueAsync(FiscalDocument document, string idempotencyKey, CancellationToken cancellationToken)
    {
        var call = _calls.AddOrUpdate(idempotencyKey, 1, (_, count) => count + 1);
        var workOrder = document.WorkOrderId.ToUpperInvariant();

        // se essa chave já gerou documento, devolve o mesmo
        if (_issued.TryGetValue(idempotencyKey, out var alreadyIssued))
        {
            await Task.Delay(_options.Latency, _time, cancellationToken);
            return IntegrationResult.Approved(alreadyIssued.Number, alreadyIssued.Protocol);
        }

        if (workOrder.EndsWith("-TIMEOUT"))
        {
            // emite, mas a resposta não chega a tempo
            Issue(idempotencyKey, document.WorkOrderId);
            await Task.Delay(_options.HangTime, _time, cancellationToken);
        }

        var latency = workOrder.EndsWith("-SLOW") ? _options.SlowLatency : _options.Latency;
        await Task.Delay(latency, _time, cancellationToken);

        if (workOrder.EndsWith("-DOWN"))
            return IntegrationResult.Transient("INTEGRATOR_UNAVAILABLE", "HTTP 503: integradora temporariamente indisponível.");

        if (workOrder.EndsWith("-UNSTABLE") && call <= _options.UnstableFailures)
            return IntegrationResult.Transient("INTEGRATOR_UNAVAILABLE", "HTTP 503: integradora temporariamente indisponível.");

        if (workOrder.EndsWith("-REJECT"))
            return IntegrationResult.Permanent("INTEGRATOR_REJECTED", "HTTP 422: tomador com inscrição irregular no município.");

        if (workOrder.EndsWith("-INCONSISTENT") && call == 1)
            return IntegrationResult.Approved(null, "");

        var issued = Issue(idempotencyKey, document.WorkOrderId);
        return IntegrationResult.Approved(issued.Number, issued.Protocol);
    }

    private IssuedDocument Issue(string idempotencyKey, string workOrderId)
    {
        return _issued.GetOrAdd(idempotencyKey, _ =>
        {
            var number = Interlocked.Increment(ref _sequence).ToString("D9");
            var protocol = _time.GetUtcNow().ToString("yyyyMMddHHmmss") + Random.Shared.Next(1_000_000).ToString("D6");

            _logger.LogInformation("Integradora simulada emitiu o documento {Number} para a OS {WorkOrderId}.", number, workOrderId);
            return new IssuedDocument { Number = number, Protocol = protocol };
        });
    }
}
