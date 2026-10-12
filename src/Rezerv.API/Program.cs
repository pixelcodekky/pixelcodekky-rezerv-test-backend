using System.Text.Json.Serialization;
using Rezerv.API.Infrastructure;
using Rezerv.Application;
using Rezerv.Application.Services;
using Rezerv.Infrastructure;
using Rezerv.Infrastructure.Persistence.Seed;
using Serilog;

// Bootstrap logger captures startup failures before configuration is loaded; replaced once the host is built.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Sinks, levels and enrichers come from the "Serilog" section of appsettings.
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", builder.Environment.ApplicationName)
        .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName));

    builder.Services.AddControllers()
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
        o.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml")));
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<AppExceptionHandler>();

    builder.Services.AddApplication(builder.Configuration.GetSection(CheckoutOptions.SectionName).Get<CheckoutOptions>());
    builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());

    var app = builder.Build();

    // One summary event per request, tagged with the idempotency key and invoice so a payment can be traced end to end.
    app.UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = (ctx, http) =>
    {
        ctx.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString());
        if (http.Request.Headers.TryGetValue("Idempotency-Key", out var key))
            ctx.Set("IdempotencyKey", key.ToString());
        if (http.GetRouteValue("invoiceNo") is { } invoiceNo)
            ctx.Set("InvoiceNo", invoiceNo);
    });
    app.UseExceptionHandler();
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapControllers();
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

    // Creates the schema and seeds customers, products and saved cards on first start.
    await DbInitializer.InitializeAsync(app.Services);

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Rezerv.API terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
