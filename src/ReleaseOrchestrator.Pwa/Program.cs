using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ReleaseOrchestrator.Pwa;
using ReleaseOrchestrator.Pwa.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    var scope = builder.Configuration["ApiScope"];
    if (!string.IsNullOrEmpty(scope))
        options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
    options.ProviderOptions.LoginMode = "redirect";
});

builder.Services.AddScoped<ApiService>();

await builder.Build().RunAsync();
