using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using Ultracar.Api;
using Ultracar.Api.Application;
using Ultracar.Api.Integration;
using Ultracar.Api.Persistence;
using Ultracar.Api.Processing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ApiKeyOptions>()
    .Bind(builder.Configuration.GetSection("ApiKey"))
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Value), "Configure ApiKey:Value ou desligue com ApiKey:Enabled = false.")
    .ValidateOnStart();
builder.Services.Configure<FiscalProcessingOptions>(builder.Configuration.GetSection("FiscalProcessing"));
builder.Services.Configure<FakeIntegratorOptions>(builder.Configuration.GetSection("FakeIntegrator"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFiscalDocumentRepository, InMemoryFiscalDocumentRepository>();
builder.Services.AddSingleton<FakeFiscalIntegrator>();
builder.Services.AddSingleton<IFiscalIntegrator>(sp => sp.GetRequiredService<FakeFiscalIntegrator>());
builder.Services.AddSingleton<FiscalDocumentService>();
builder.Services.AddSingleton<FiscalDocumentProcessor>();
builder.Services.AddHostedService<FiscalDocumentWorker>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new OpenApiInfo { Title = "Ultracar - Documentos Fiscais", Version = "v1" });
    swagger.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));

    if (builder.Configuration.GetValue("ApiKey:Enabled", true))
    {
        var scheme = new OpenApiSecurityScheme
        {
            Name = ApiKeyMiddleware.HeaderName,
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.ApiKey,
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
        };
        swagger.AddSecurityDefinition("ApiKey", scheme);
        swagger.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
    }
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
