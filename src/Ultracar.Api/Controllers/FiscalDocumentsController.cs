using Microsoft.AspNetCore.Mvc;
using Ultracar.Api.Application;
using Ultracar.Api.Domain;

namespace Ultracar.Api.Controllers;

[ApiController]
[Route("api/v1/fiscal-documents")]
public class FiscalDocumentsController : ControllerBase
{
    private readonly FiscalDocumentService _service;

    public FiscalDocumentsController(FiscalDocumentService service)
    {
        _service = service;
    }

    /// <summary>
    /// Solicita a emissão do documento fiscal de uma ordem de serviço.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(FiscalDocumentResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(FiscalDocumentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateFiscalDocumentRequest request, CancellationToken cancellationToken)
    {
        var (result, document) = await _service.RequestAsync(request, cancellationToken);
        var response = FiscalDocumentResponse.From(document);

        if (result == RequestResult.Created)
            return AcceptedAtAction(nameof(GetById), new { id = document.Id }, response);

        if (result == RequestResult.AlreadyExists)
            return Ok(response);

        return Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Esta OS já tem uma solicitação de emissão com outros dados.",
            detail: $"Solicitação existente: {document.Id}.");
    }

    /// <summary>
    /// Consulta o status da solicitação
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(FiscalDocumentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var document = await _service.GetAsync(id, cancellationToken);
        if (document == null)
            return NotFound();

        return Ok(FiscalDocumentResponse.From(document));
    }

    /// <summary>
    /// Histórico da solicitação
    /// </summary>
    [HttpGet("{id:guid}/history")]
    [ProducesResponseType(typeof(List<FiscalDocumentEvent>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory(Guid id, CancellationToken cancellationToken)
    {
        var document = await _service.GetAsync(id, cancellationToken);
        if (document == null)
            return NotFound();

        return Ok(document.History);
    }
}
