using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ultracar.Api.Application;
using Ultracar.Api.Domain;
using Ultracar.Api.Integration;

namespace Ultracar.Tests;

/// <summary>
/// Sobe a API de verdade (com o worker rodando) usando tempos curtos para os testes não demorarem.
/// </summary>
public class UltracarApp : WebApplicationFactory<Program>
{
    public const string ApiKey = "chave-de-teste";

    protected virtual bool ApiKeyEnabled => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ApiKey:Enabled", ApiKeyEnabled.ToString());
        builder.UseSetting("ApiKey:Value", ApiKey);
        builder.UseSetting("FiscalProcessing:MaxAttempts", "3");
        builder.UseSetting("FiscalProcessing:BaseRetryDelay", "00:00:00.050");
        builder.UseSetting("FiscalProcessing:MaxRetryDelay", "00:00:00.200");
        builder.UseSetting("FiscalProcessing:IntegratorTimeout", "00:00:00.500");
        builder.UseSetting("FiscalProcessing:PollingInterval", "00:00:00.050");
        builder.UseSetting("FakeIntegrator:Latency", "00:00:00.010");
        builder.UseSetting("FakeIntegrator:SlowLatency", "00:00:00.200");
        builder.UseSetting("FakeIntegrator:HangTime", "00:00:10");
    }
}

public class UltracarAppWithoutApiKey : UltracarApp
{
    protected override bool ApiKeyEnabled => false;
}

public class ApiTests : IClassFixture<UltracarApp>
{
    private const string Route = "/api/v1/fiscal-documents";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly UltracarApp _app;
    private readonly HttpClient _client;

    public ApiTests(UltracarApp app)
    {
        _app = app;
        _client = app.CreateClient();
        _client.DefaultRequestHeaders.Add("api-key", UltracarApp.ApiKey);
    }

    private static object Payload(string workOrderId, decimal amount = 250.75m) => new
    {
        workOrderId,
        customerDocument = "12345678901",
        amount,
        municipalityCode = "3550308"
    };

    private static string NewWorkOrder(string suffix = "") => $"OS-{Guid.NewGuid():N}{suffix}";

    private async Task<FiscalDocumentResponse> CreateAsync(string workOrderId)
    {
        var response = await _client.PostAsJsonAsync(Route, Payload(workOrderId));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FiscalDocumentResponse>(Json))!;
    }

    // Consulta até a emissão terminar (emitida ou falha definitiva).
    private async Task<FiscalDocumentResponse> WaitUntilFinishedAsync(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var document = (await _client.GetFromJsonAsync<FiscalDocumentResponse>($"{Route}/{id}", Json))!;
            if (document.Status is FiscalDocumentStatus.Issued or FiscalDocumentStatus.Failed)
                return document;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"A solicitação {id} não terminou. Status atual: {document.Status}.");

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Post_valido_retorna_202_com_location()
    {
        var response = await _client.PostAsJsonAsync(Route, Payload(NewWorkOrder()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FiscalDocumentResponse>(Json);
        Assert.Equal(FiscalDocumentStatus.Pending, body!.Status);
        Assert.EndsWith($"{Route}/{body.Id}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Post_repetido_retorna_200_com_o_mesmo_id()
    {
        var workOrderId = NewWorkOrder();
        var first = await CreateAsync(workOrderId);

        var response = await _client.PostAsJsonAsync(Route, Payload(workOrderId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(first.Id, (await response.Content.ReadFromJsonAsync<FiscalDocumentResponse>(Json))!.Id);
    }

    [Fact]
    public async Task Mesma_OS_com_outro_valor_retorna_409()
    {
        var workOrderId = NewWorkOrder();
        await CreateAsync(workOrderId);

        var response = await _client.PostAsJsonAsync(Route, Payload(workOrderId, amount: 10m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Dados_invalidos_retornam_400_com_erro_por_campo()
    {
        var response = await _client.PostAsJsonAsync(Route, new { workOrderId = "", customerDocument = "123", amount = 0, municipalityCode = "1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        foreach (var field in new[] { "WorkOrderId", "CustomerDocument", "Amount", "MunicipalityCode" })
            Assert.True(errors.TryGetProperty(field, out _), $"Faltou erro para {field}.");
    }

    [Fact]
    public async Task Id_inexistente_retorna_404()
    {
        var response = await _client.GetAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Sem_api_key_retorna_401()
    {
        var response = await _app.CreateClient().GetAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("-SLOW", 1)]
    [InlineData("-UNSTABLE", 3)]
    [InlineData("-INCONSISTENT", 2)]
    public async Task Cenarios_que_terminam_emitidos(string scenario, int expectedAttempts)
    {
        var created = await CreateAsync(NewWorkOrder(scenario));

        var final = await WaitUntilFinishedAsync(created.Id);

        Assert.Equal(FiscalDocumentStatus.Issued, final.Status);
        Assert.Equal(expectedAttempts, final.Attempts);
        Assert.NotNull(final.Document);
        Assert.Null(final.Failure);
    }

    [Fact]
    public async Task Timeout_tenta_de_novo_e_emite_um_unico_documento()
    {
        var created = await CreateAsync(NewWorkOrder("-TIMEOUT"));

        var final = await WaitUntilFinishedAsync(created.Id);

        var integrator = _app.Services.GetRequiredService<FakeFiscalIntegrator>();
        var key = created.Id.ToString("N");
        Assert.Equal(FiscalDocumentStatus.Issued, final.Status);
        Assert.Equal(2, final.Attempts);
        Assert.Equal(2, integrator.CallsFor(key));
        Assert.Equal(integrator.Issued[key].Number, final.Document!.Number);
    }

    [Fact]
    public async Task Rejeicao_falha_sem_nova_tentativa()
    {
        var created = await CreateAsync(NewWorkOrder("-REJECT"));

        var final = await WaitUntilFinishedAsync(created.Id);

        Assert.Equal(FiscalDocumentStatus.Failed, final.Status);
        Assert.Equal(1, final.Attempts);
        Assert.Equal("INTEGRATOR_REJECTED", final.Failure!.Code);
        Assert.False(final.Failure.WillRetry);
    }

    [Fact]
    public async Task Integradora_fora_do_ar_esgota_as_tentativas()
    {
        var created = await CreateAsync(NewWorkOrder("-DOWN"));

        var final = await WaitUntilFinishedAsync(created.Id);

        Assert.Equal(FiscalDocumentStatus.Failed, final.Status);
        Assert.Equal(3, final.Attempts);
        Assert.Equal("RETRIES_EXHAUSTED", final.Failure!.Code);

        var history = await _client.GetFromJsonAsync<List<FiscalDocumentEvent>>($"{Route}/{created.Id}/history", Json);
        Assert.Equal(3, history!.Count(e => e.Status == FiscalDocumentStatus.Processing));
    }
}

public class ApiWithoutApiKeyTests(UltracarAppWithoutApiKey app) : IClassFixture<UltracarAppWithoutApiKey>
{
    [Fact]
    public async Task Com_api_key_desligada_aceita_requisicao_sem_header()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/v1/fiscal-documents", new
        {
            workOrderId = $"OS-{Guid.NewGuid():N}",
            customerDocument = "12345678901",
            amount = 250.75m,
            municipalityCode = "3550308"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
