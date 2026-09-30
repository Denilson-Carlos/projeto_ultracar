using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ultracar.Api.Domain;
using Ultracar.Api.Integration;

namespace Ultracar.Tests;

public class FakeFiscalIntegratorTests
{
    private readonly FakeFiscalIntegrator _integrator = new(
        Options.Create(new FakeIntegratorOptions { Latency = TimeSpan.Zero, SlowLatency = TimeSpan.Zero }),
        new FakeTimeProvider(),
        NullLogger<FakeFiscalIntegrator>.Instance);

    private static FiscalDocument Document(string workOrderId) =>
        new FiscalDocument(workOrderId, "12345678901", 100m, "3550308", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Mesma_chave_devolve_o_mesmo_documento()
    {
        var first = await _integrator.IssueAsync(Document("OS-1"), "chave", CancellationToken.None);
        var second = await _integrator.IssueAsync(Document("OS-1"), "chave", CancellationToken.None);

        Assert.Equal(first.Number, second.Number);
        Assert.Single(_integrator.Issued);
    }

    [Fact]
    public async Task Timeout_emite_uma_vez_e_a_retentativa_recebe_o_mesmo_documento()
    {
        using var cts = new CancellationTokenSource();
        var hanging = _integrator.IssueAsync(Document("OS-1-TIMEOUT"), "chave", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging);

        var retry = await _integrator.IssueAsync(Document("OS-1-TIMEOUT"), "chave", CancellationToken.None);

        Assert.Equal(IntegrationOutcome.Approved, retry.Outcome);
        Assert.Equal(_integrator.Issued["chave"].Number, retry.Number);
        Assert.Single(_integrator.Issued);
    }

    [Fact]
    public async Task Instavel_falha_nas_primeiras_chamadas_e_depois_aprova()
    {
        var outcomes = new List<IntegrationOutcome>();
        for (var i = 0; i < 3; i++)
            outcomes.Add((await _integrator.IssueAsync(Document("OS-1-UNSTABLE"), "chave", CancellationToken.None)).Outcome);

        Assert.Equal(new[] { IntegrationOutcome.TransientError, IntegrationOutcome.TransientError, IntegrationOutcome.Approved }, outcomes);
    }

    [Fact]
    public async Task Rejeicao_e_falha_definitiva()
    {
        var result = await _integrator.IssueAsync(Document("OS-1-REJECT"), "chave", CancellationToken.None);

        Assert.Equal(IntegrationOutcome.PermanentError, result.Outcome);
        Assert.Empty(_integrator.Issued);
    }
}
