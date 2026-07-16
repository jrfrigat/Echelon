using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using ReleaseOrchestrator.Infrastructure;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
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
    builder.Services.AddControllers();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

    builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration, "AD");

    builder.Services.AddAuthorization(opts =>
    {
        opts.AddPolicy(Permissions.ReleasePlanApprove, p => p.AddRequirements(new PermissionRequirement(Permissions.ReleasePlanApprove)));
        opts.AddPolicy(Permissions.ConfigEdit, p => p.AddRequirements(new PermissionRequirement(Permissions.ConfigEdit)));
        opts.AddPolicy(Permissions.ReleasePlanView, p => p.AddRequirements(new PermissionRequirement(Permissions.ReleasePlanView)));
        opts.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    });

    var app = builder.Build();

    // Apply EF Core migrations on startup so fresh deployments are self-bootstrapping.
    using (var scope = app.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.EnsureCreatedAsync();
    }

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
        app.MapOpenApi();

    app.UseHttpsRedirection();
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
    app.MapFallbackToFile("index.html");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup failed");
}
finally
{
    Log.CloseAndFlush();
}
