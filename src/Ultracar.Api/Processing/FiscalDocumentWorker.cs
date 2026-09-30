using Microsoft.Extensions.Options;
using Ultracar.Api.Persistence;

namespace Ultracar.Api.Processing;

// Roda em segundo plano: a cada intervalo pega o que está pendente ou com retentativa
// vencida e processa. O mesmo loop faz a primeira tentativa e as retentativas.
public class FiscalDocumentWorker : BackgroundService
{
    private readonly IFiscalDocumentRepository _repository;
    private readonly FiscalDocumentProcessor _processor;
    private readonly FiscalProcessingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FiscalDocumentWorker> _logger;

    public FiscalDocumentWorker(
        IFiscalDocumentRepository repository,
        FiscalDocumentProcessor processor,
        IOptions<FiscalProcessingOptions> options,
        TimeProvider time,
        ILogger<FiscalDocumentWorker> logger)
    {
        _repository = repository;
        _processor = processor;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.PollingInterval, _time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var documents = await _repository.GetDueAsync(_time.GetUtcNow(), stoppingToken);

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.MaxParallelism,
                    CancellationToken = stoppingToken
                };

                await Parallel.ForEachAsync(documents, parallelOptions, async (document, token) =>
                {
                    await _processor.ProcessAsync(document, token);
                });
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // não deixa o worker parar por causa de um erro, tenta de novo no próximo ciclo
                _logger.LogError(ex, "Erro ao processar as emissões.");
            }
        }
    }
}
