using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Ultracar.Api;

public class ApiKeyOptions
{
    public bool Enabled { get; set; } = true;
    public string? Value { get; set; }
}

// Exige o header "api-key" nas rotas /api. Vem ligado por padrão,
// o appsettings.Development.json desliga para facilitar os testes locais, neste projeto da ultracar
public class ApiKeyMiddleware
{
    public const string HeaderName = "api-key";

    private readonly RequestDelegate _next;
    private readonly ApiKeyOptions _options;

    public ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        string? receivedKey = context.Request.Headers[HeaderName];

        if (receivedKey != null && IsValid(receivedKey, _options.Value!))
        {
            await _next(context);
            return;
        }

        await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Não autorizado.",
                detail: $"Header '{HeaderName}' ausente ou inválido.")
            .ExecuteAsync(context);
    }


    private static bool IsValid(string received, string expected)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected));
    }
}
