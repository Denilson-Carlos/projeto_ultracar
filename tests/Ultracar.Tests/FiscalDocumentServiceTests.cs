using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Ultracar.Api.Application;
using Ultracar.Api.Domain;
using Ultracar.Api.Persistence;

namespace Ultracar.Tests;

public class FiscalDocumentServiceTests
{
    private readonly FiscalDocumentService _service = new(
        new InMemoryFiscalDocumentRepository(),
        new FakeTimeProvider(),
        NullLogger<FiscalDocumentService>.Instance);

    private static CreateFiscalDocumentRequest Request(decimal amount = 250.75m) => new()
    {
        WorkOrderId = "OS-12345",
        CustomerDocument = "12345678901",
        Amount = amount,
        MunicipalityCode = "3550308"
    };

    [Fact]
    public async Task Nova_solicitacao_comeca_pendente()
    {
        var (result, document) = await _service.RequestAsync(Request(), CancellationToken.None);

        Assert.Equal(RequestResult.Created, result);
        Assert.Equal(FiscalDocumentStatus.Pending, document.Status);
    }

    [Fact]
    public async Task Repetir_com_os_mesmos_dados_devolve_a_solicitacao_existente()
    {
        var (_, first) = await _service.RequestAsync(Request(), CancellationToken.None);
        var (result, second) = await _service.RequestAsync(Request(), CancellationToken.None);

        Assert.Equal(RequestResult.AlreadyExists, result);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Mesma_OS_com_outros_dados_e_conflito()
    {
        await _service.RequestAsync(Request(), CancellationToken.None);

        var (result, _) = await _service.RequestAsync(Request(amount: 10m), CancellationToken.None);

        Assert.Equal(RequestResult.Conflict, result);
    }

    [Fact]
    public async Task Requisicoes_simultaneas_para_a_mesma_OS_criam_uma_so_solicitacao()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => _service.RequestAsync(Request(), CancellationToken.None))));

        Assert.Single(results, r => r.Result == RequestResult.Created);
        Assert.Single(results.Select(r => r.Document.Id).Distinct());
    }
}
