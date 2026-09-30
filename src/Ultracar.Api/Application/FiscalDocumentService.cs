using Ultracar.Api.Domain;
using Ultracar.Api.Persistence;

namespace Ultracar.Api.Application;

public enum RequestResult
{
    Created,
    AlreadyExists,
    Conflict
}

public class FiscalDocumentService
{
    private readonly IFiscalDocumentRepository _repository;
    private readonly TimeProvider _time;
    private readonly ILogger<FiscalDocumentService> _logger;

    public FiscalDocumentService(IFiscalDocumentRepository repository, TimeProvider time, ILogger<FiscalDocumentService> logger)
    {
        _repository = repository;
        _time = time;
        _logger = logger;
    }

    // a OS é a chave: mesmo pedido repetido devolve o que já existe, dados diferentes é conflito
    public async Task<(RequestResult Result, FiscalDocument Document)> RequestAsync(CreateFiscalDocumentRequest request, CancellationToken cancellationToken)
    {
        var newDocument = new FiscalDocument(
            request.WorkOrderId!.Trim(),
            request.CustomerDocument!,
            request.Amount!.Value,
            request.MunicipalityCode!,
            _time.GetUtcNow());

        var stored = await _repository.GetOrAddAsync(newDocument, cancellationToken);

        if (stored.Id == newDocument.Id)
        {
            _logger.LogInformation("Solicitação {Id} criada para a OS {WorkOrderId}.", stored.Id, stored.WorkOrderId);
            return (RequestResult.Created, stored);
        }

        if (stored.HasSameData(newDocument))
            return (RequestResult.AlreadyExists, stored);

        _logger.LogWarning("OS {WorkOrderId} já tem a solicitação {Id} com outros dados.", stored.WorkOrderId, stored.Id);
        return (RequestResult.Conflict, stored);
    }

    public Task<FiscalDocument?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return _repository.GetAsync(id, cancellationToken);
    }
}
