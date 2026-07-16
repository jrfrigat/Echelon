using Flare.Abstractions.Tokens;
using Flare.Extensions;
using Flare.Theme.VisualStudio;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ReleaseOrchestrator.Pwa;
using ReleaseOrchestrator.Pwa.Services;

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

builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    var scope = builder.Configuration["ApiScope"];
    if (!string.IsNullOrEmpty(scope))
        options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
    options.ProviderOptions.LoginMode = "redirect";
});

// AddHttpMessageHandler is what attaches the access token. A bare HttpClient was being
// injected into ApiService, so no request ever carried an Authorization header and every
// call hit the API's RequireAuthenticatedUser fallback with a 401.
builder.Services.AddHttpClient<ApiService>(client =>
        client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<BaseAddressAuthorizationMessageHandler>();

await builder.Build().RunAsync();
