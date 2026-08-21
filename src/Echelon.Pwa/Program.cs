using Flare.Abstractions.Tokens;
using Flare.Extensions;
using Flare.Theme.VisualStudio;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Echelon.Pwa;
using Echelon.Pwa.Services;
using Echelon.Pwa.Services.Api;
using Echelon.Pwa.Services.LocalAuth;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddFlare(opts =>
{
    opts.DefaultTheme = new VisualStudioTheme();
    opts.DefaultPalette = VisualStudioPalettes.Blue;
    opts.DefaultMode = ThemeMode.Auto;

    // VisualStudio is the only theme this app registers, and it is passed explicitly above.
    // Auto-discovery would force-load the whole referenced assembly graph at startup to reflect
    // over it, which buys nothing here and works against trimming.
    opts.RegisterAllBuiltInThemes = false;
});

// The auth provider is fixed at deploy time by Auth:Provider (read from wwwroot/appsettings.json),
// mirroring the server. Local runs a self-contained login; anything else uses MSAL (Entra ID).
// OIDC on the client is a follow-up - it needs its own AuthenticationService.js, which cannot share
// index.html with MSAL's.
var isLocalAuth = string.Equals(builder.Configuration["Auth:Provider"], "Local", StringComparison.OrdinalIgnoreCase);

if (isLocalAuth)
{
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<LocalAuthStateProvider>();
    builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<LocalAuthStateProvider>());
    builder.Services.AddScoped<LocalAuthService>();
    builder.Services.AddScoped<LocalAuthMessageHandler>();
    // A bare client for the anonymous POST /auth/login - no bearer handler on this one.
    builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
}
else
{
    builder.Services.AddMsalAuthentication(options =>
    {
        builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
        var scope = builder.Configuration["ApiScope"];
        if (!string.IsNullOrEmpty(scope))
            options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
        options.ProviderOptions.LoginMode = "redirect";
    });
}

// AddHttpMessageHandler is what attaches the access token. A bare HttpClient was being injected into
// the API client, so no request ever carried an Authorization header and every call hit the API's
// RequireAuthenticatedUser fallback with a 401.
builder.Services.AddTransient<AcceptLanguageHandler>();

// One typed client per area of the API, each with the same handlers: the token goes on, the language
// goes on, and the base address is this host. Registered individually so a page takes only the area it
// needs - a screen about permissions has no business holding the code that launches rollouts.
AddApiClient<TasksApi>();
AddApiClient<WorkApi>();
AddApiClient<VcsApi>();
AddApiClient<TrackerApi>();
AddApiClient<RepositoriesApi>();
AddApiClient<PlanningApi>();
AddApiClient<EnvironmentsApi>();
AddApiClient<ReadinessApi>();
AddApiClient<DeployApi>();
AddApiClient<RolloutsApi>();
AddApiClient<ActionsApi>();
AddApiClient<AuditApi>();
AddApiClient<PermissionsApi>();
AddApiClient<PluginsApi>();
AddApiClient<IngestionApi>();

builder.Services.AddScoped<LanguageService>();

var host = builder.Build();

// Before RunAsync, not after: the culture has to be in place for the first render, or the shell
// paints in English and only flips once something else re-renders it.
await host.Services.GetRequiredService<LanguageService>().InitializeCultureAsync();

await host.RunAsync();

// Local function so the handler chain is written once: a client registered without it silently loses
// its Authorization header, which is a 401 nobody can explain from the browser.
void AddApiClient<TClient>() where TClient : class
{
    var client = builder.Services.AddHttpClient<TClient>(c =>
        c.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));

    if (isLocalAuth)
        client.AddHttpMessageHandler<LocalAuthMessageHandler>();
    else
        client.AddHttpMessageHandler<BaseAddressAuthorizationMessageHandler>();

    client.AddHttpMessageHandler<AcceptLanguageHandler>();
}
