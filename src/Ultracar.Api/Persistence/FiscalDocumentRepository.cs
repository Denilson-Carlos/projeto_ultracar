using Ultracar.Api.Domain;

namespace Ultracar.Api.Persistence;

public interface IFiscalDocumentRepository
{
    /// <summary>
    /// Grava a solicitação se a OS ainda não tiver uma. e irá devolver o que ficou gravado:
    /// </summary>
    Task<FiscalDocument> GetOrAddAsync(FiscalDocument document, CancellationToken cancellationToken = default);

    Task<FiscalDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(FiscalDocument document, CancellationToken cancellationToken = default);

    /// <summary>Solicitações pendentes ou com nova tentativa já vencida.</summary>
    Task<IReadOnlyList<FiscalDocument>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>
/// ficará em memória. Os dados irão sumir quando a aplicação reinicia
/// </summary>
public class InMemoryFiscalDocumentRepository : IFiscalDocumentRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, FiscalDocument> _byId = new();
    private readonly Dictionary<string, Guid> _idByWorkOrder = new(StringComparer.OrdinalIgnoreCase);

    public Task<FiscalDocument> GetOrAddAsync(FiscalDocument document, CancellationToken cancellationToken = default)
    {
        // o indice é unica por OS: com duas requisições ao mesmo tempo, só irá gravar uma.
        lock (_lock)
        {
            if (_idByWorkOrder.TryGetValue(document.WorkOrderId, out var existingId))
                return Task.FromResult(_byId[existingId].Clone());

            _byId[document.Id] = document.Clone();
            _idByWorkOrder[document.WorkOrderId] = document.Id;
            return Task.FromResult(document);
        }
    }

    public Task<FiscalDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_byId.TryGetValue(id, out var document) ? document.Clone() : null);
        }
    }

    public Task UpdateAsync(FiscalDocument document, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _byId[document.Id] = document.Clone();
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<FiscalDocument>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<FiscalDocument> due = _byId.Values
                .Where(d => d.IsDue(now))
                .OrderBy(d => d.NextAttemptAt ?? d.CreatedAt)
                .Select(d => d.Clone())
                .ToList();

            return Task.FromResult(due);
        }
    }
}
