using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ultracar.Api.Domain;
using Ultracar.Api.Integration;
using Ultracar.Api.Persistence;
using Ultracar.Api.Processing;

namespace Ultracar.Tests;

public class FiscalDocumentProcessorTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFiscalDocumentRepository _repository = new();
    private readonly StubIntegrator _integrator = new();
    private readonly FiscalProcessingOptions _options = new()
    {
        MaxAttempts = 3,
        BaseRetryDelay = TimeSpan.FromSeconds(2),
        MaxRetryDelay = TimeSpan.FromSeconds(5),
        IntegratorTimeout = TimeSpan.FromSeconds(5)
    };

    private FiscalDocumentProcessor CreateProcessor() => new(
        _repository, _integrator, Options.Create(_options), _clock, NullLogger<FiscalDocumentProcessor>.Instance);

    private async Task<FiscalDocument> ProcessNewDocumentAsync()
    {
        var document = new FiscalDocument("OS-1", "12345678901", 100m, "3550308", _clock.GetUtcNow());
        await _repository.GetOrAddAsync(document);
        await CreateProcessor().ProcessAsync(document, CancellationToken.None);
        return (await _repository.GetAsync(document.Id))!;
    }

    [Fact]
    public async Task Aprovada_fica_emitida_com_numero_e_protocolo()
    {
        _integrator.Respond = _ => IntegrationResult.Approved("000000001", "PROTO-1");

        var document = await ProcessNewDocumentAsync();

        Assert.Equal(FiscalDocumentStatus.Issued, document.Status);
        Assert.Equal("000000001", document.Number);
        Assert.Equal("PROTO-1", document.Protocol);
        Assert.Equal(document.Id.ToString("N"), _integrator.LastIdempotencyKey);
    }

    [Fact]
    public async Task Falha_temporaria_agenda_nova_tentativa()
    {
        _integrator.Respond = _ => IntegrationResult.Transient("INTEGRATOR_UNAVAILABLE", "503");

        var document = await ProcessNewDocumentAsync();

        Assert.Equal(FiscalDocumentStatus.WaitingRetry, document.Status);
        Assert.Equal(_clock.GetUtcNow().AddSeconds(2), document.NextAttemptAt);
        Assert.True(document.WillRetry);
    }

    [Fact]
    public async Task Retentativas_esgotadas_viram_falha_definitiva()
    {
        _integrator.Respond = _ => IntegrationResult.Transient("INTEGRATOR_UNAVAILABLE", "503");
        var document = await ProcessNewDocumentAsync();

        while (document.Status == FiscalDocumentStatus.WaitingRetry)
        {
            _clock.Advance(_options.MaxRetryDelay);
            await CreateProcessor().ProcessAsync(document, CancellationToken.None);
            document = (await _repository.GetAsync(document.Id))!;
        }

        Assert.Equal(FiscalDocumentStatus.Failed, document.Status);
        Assert.Equal("RETRIES_EXHAUSTED", document.ErrorCode);
        Assert.Equal(_options.MaxAttempts, document.Attempts);
        Assert.False(document.WillRetry);
    }

    [Fact]
    public async Task Falha_definitiva_nao_tenta_de_novo()
    {
        _integrator.Respond = _ => IntegrationResult.Permanent("INTEGRATOR_REJECTED", "422");

        var document = await ProcessNewDocumentAsync();

        Assert.Equal(FiscalDocumentStatus.Failed, document.Status);
        Assert.Equal("INTEGRATOR_REJECTED", document.ErrorCode);
        Assert.Equal(1, document.Attempts);
        Assert.False(document.WillRetry);
    }

    [Fact]
    public async Task Timeout_e_tratado_como_falha_temporaria()
    {
        _integrator.RespondAsync = async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _clock, ct);
            return IntegrationResult.Approved("nunca", "nunca");
        };
        var document = new FiscalDocument("OS-1", "12345678901", 100m, "3550308", _clock.GetUtcNow());
        await _repository.GetOrAddAsync(document);

        var processing = CreateProcessor().ProcessAsync(document, CancellationToken.None);
        _clock.Advance(_options.IntegratorTimeout);
        await processing;

        var stored = (await _repository.GetAsync(document.Id))!;
        Assert.Equal(FiscalDocumentStatus.WaitingRetry, stored.Status);
        Assert.Equal("INTEGRATOR_TIMEOUT", stored.ErrorCode);
    }

    [Fact]
    public async Task Aprovacao_sem_protocolo_e_tratada_como_falha_temporaria()
    {
        _integrator.Respond = _ => IntegrationResult.Approved("000000001", protocol: "");

        var document = await ProcessNewDocumentAsync();

        Assert.Equal(FiscalDocumentStatus.WaitingRetry, document.Status);
        Assert.Equal("INCONSISTENT_RESPONSE", document.ErrorCode);
    }

    [Fact]
    public async Task Erro_inesperado_da_integradora_e_tratado_como_falha_temporaria()
    {
        _integrator.Respond = _ => throw new HttpRequestException("conexão recusada");

        var document = await ProcessNewDocumentAsync();

        Assert.Equal(FiscalDocumentStatus.WaitingRetry, document.Status);
        Assert.Equal("INTEGRATOR_ERROR", document.ErrorCode);
    }

    [Fact]
    public async Task Historico_registra_cada_passo()
    {
        _integrator.Respond = _ => IntegrationResult.Approved("000000001", "PROTO-1");

        var document = await ProcessNewDocumentAsync();

        Assert.Equal(
            new[] { FiscalDocumentStatus.Pending, FiscalDocumentStatus.Processing, FiscalDocumentStatus.Issued },
            document.History.Select(e => e.Status));
    }

    private sealed class StubIntegrator : IFiscalIntegrator
    {
        public Func<CancellationToken, IntegrationResult> Respond { get; set; } = _ => IntegrationResult.Approved("1", "P");
        public Func<CancellationToken, Task<IntegrationResult>>? RespondAsync { get; set; }
        public string? LastIdempotencyKey { get; private set; }

        public Task<IntegrationResult> IssueAsync(FiscalDocument document, string idempotencyKey, CancellationToken cancellationToken)
        {
            LastIdempotencyKey = idempotencyKey;
            return RespondAsync?.Invoke(cancellationToken) ?? Task.FromResult(Respond(cancellationToken));
        }
    }
}
