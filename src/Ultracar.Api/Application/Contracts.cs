using System.ComponentModel.DataAnnotations;
using Ultracar.Api.Domain;

namespace Ultracar.Api.Application;

public class CreateFiscalDocumentRequest
{
    [Required(ErrorMessage = "Informe a ordem de serviço.")]
    [StringLength(50, ErrorMessage = "A ordem de serviço deve ter no máximo 50 caracteres.")]
    public string? WorkOrderId { get; set; }

    [Required(ErrorMessage = "Informe o CPF ou CNPJ do cliente.")]
    [RegularExpression(@"^(\d{11}|\d{14})$", ErrorMessage = "Informe um CPF (11 dígitos) ou CNPJ (14 dígitos), só números.")]
    public string? CustomerDocument { get; set; }

    [Required(ErrorMessage = "Informe o valor.")]
    [Range(0.01, 999_999_999.99, ErrorMessage = "O valor deve ser maior que zero.")]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Informe o código IBGE do município.")]
    [RegularExpression(@"^\d{7}$", ErrorMessage = "O código do município deve ter 7 dígitos (código IBGE).")]
    public string? MunicipalityCode { get; set; }
}

public class FiscalDocumentResponse
{
    public Guid Id { get; set; }
    public string WorkOrderId { get; set; } = string.Empty;
    public FiscalDocumentStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public IssuedDocumentResponse? Document { get; set; }
    public FailureResponse? Failure { get; set; }

    public static FiscalDocumentResponse From(FiscalDocument document)
    {
        var response = new FiscalDocumentResponse
        {
            Id = document.Id,
            WorkOrderId = document.WorkOrderId,
            Status = document.Status,
            Attempts = document.Attempts,
            CreatedAt = document.CreatedAt,
            UpdatedAt = document.UpdatedAt
        };

        if (document.Status == FiscalDocumentStatus.Issued)
        {
            response.Document = new IssuedDocumentResponse
            {
                Number = document.Number!,
                Protocol = document.Protocol!,
                IssuedAt = document.IssuedAt!.Value
            };
        }

        if (document.ErrorCode != null)
        {
            response.Failure = new FailureResponse
            {
                Code = document.ErrorCode,
                Reason = document.ErrorMessage!,
                WillRetry = document.WillRetry,
                NextAttemptAt = document.NextAttemptAt
            };
        }

        return response;
    }
}

public class IssuedDocumentResponse
{
    public string Number { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }
}

public class FailureResponse
{
    public string Code { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    // se o sistema ainda vai tentar emitir sozinho
    public bool WillRetry { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
}
