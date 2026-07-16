using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReleaseOrchestrator.Pwa.Services;

namespace ReleaseOrchestrator.Pwa.Shared;

/// <summary>
/// Error/notice plumbing shared by every page that calls the API.
///
/// Handlers must funnel through <see cref="RunAsync"/> rather than calling the API bare: an
/// unguarded handler lets the exception escape to Blazor's unhandled-error UI, which replaces
/// the page with a "reload" bar and throws away the reason. That reason is the useful part —
/// <see cref="ApiException"/> carries the server's own wording.
/// </summary>
public abstract class PageBase : ComponentBase
{
    [Inject] protected ApiService Api { get; set; } = default!;
    [Inject] protected IJSRuntime Js { get; set; } = default!;

    protected string? Error;
    protected string? Success;

    /// <summary>Runs a mutation, reporting any failure instead of letting it escape. True on success.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? success = null)
    {
        Error = null;
        try
        {
            await action();
            if (success is not null) Success = success;
            return true;
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
            return false;
        }
    }

    protected ValueTask<bool> ConfirmAsync(string message) => Js.InvokeAsync<bool>("confirm", message);

    /// <summary>
    /// An ApiException already reads as a sentence written for the operator. Anything else is a
    /// transport or client fault, which needs framing to not look like a server rejection.
    /// </summary>
    protected static string Describe(Exception ex) => ex switch
    {
        ApiException api => api.Message,
        _ => $"Could not reach the server: {ex.Message}"
    };
}
