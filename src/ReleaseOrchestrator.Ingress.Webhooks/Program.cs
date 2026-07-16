using MassTransit;
using ReleaseOrchestrator.Ingress.Webhooks.Endpoints;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://localhost:5341"));

    builder.Services.AddOpenApi();

    builder.Services.AddMassTransit(x =>
    {
        x.UsingRabbitMq((_, cfg) =>
        {
            cfg.Host(builder.Configuration["Queue:Host"] ?? "localhost", h =>
            {
                h.Username(builder.Configuration["Queue:Username"] ?? "guest");
                h.Password(builder.Configuration["Queue:Password"] ?? "guest");
            });
        });
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
        app.MapOpenApi();

    app.UseHttpsRedirection();
    app.MapGitLabWebhooks();
    app.MapYandexTrackerWebhooks();
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Ingress startup failed");
}
finally
{
    Log.CloseAndFlush();
}
