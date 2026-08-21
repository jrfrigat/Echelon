using Flare.Components;
using Microsoft.AspNetCore.Components;
using Echelon.Pwa.Resources;
using Echelon.Pwa.Services.Api;

namespace Echelon.Pwa.Shared;

/// <summary>
/// Error/notice plumbing shared by every page that calls the API.
///
/// Handlers must funnel through <see cref="RunAsync"/> rather than calling an API client bare: an
/// unguarded handler lets the exception escape to Blazor's unhandled-error UI, which replaces
/// the page with a "reload" bar and throws away the reason. That reason is the useful part -
/// <see cref="ApiException"/> carries the server's own wording.
/// </summary>
public abstract class PageBase : LocalizedComponent
{
    /// <summary>Supplied by the FlareConfirmDialogProvider that App.razor wraps the router in.</summary>
    [CascadingParameter] protected FlareConfirmDialogProvider? ConfirmDialog { get; set; }

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

    /// <summary>
    /// Feeds a server-paged Flare grid, reporting a failure on the page instead of throwing into the
    /// grid's provider.
    /// </summary>
    /// <remarks>
    /// An exception raised inside an ItemsProvider leaves the grid showing its spinner for ever and
    /// takes the reason with it - the same failure mode <see cref="RunAsync"/> exists to prevent for
    /// handlers. An empty result plus a visible error is the honest answer.
    /// </remarks>
    /// <typeparam name="T">The grid's row type.</typeparam>
    /// <param name="fetch">Reads one page from the API and shapes it into a grid result.</param>
    protected async Task<DataGridResult<T>> LoadPageAsync<T>(Func<Task<DataGridResult<T>>> fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);

        Error = null;
        try
        {
            return await fetch();
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
            return new DataGridResult<T>([], 0);
        }
    }

    /// <summary>
    /// Asks for confirmation before a destructive action. True only on an explicit confirm: Flare
    /// reports a cancel as false and a dismissal (Escape) as null, and neither is consent. A missing
    /// provider answers false for the same reason - losing the dialog must not mean losing the
    /// prompt.
    /// </summary>
    protected async Task<bool> ConfirmAsync(string title, string message)
    {
        if (ConfirmDialog is null) return false;

        return await ConfirmDialog.ConfirmAsync(title, message, confirmLabel: UiStrings.Common_Delete) is true;
    }

    /// <summary>
    /// An ApiException already reads as a sentence written for the operator - and the server
    /// picked its language from our Accept-Language, so it is already in the user's. Anything
    /// else is a transport or client fault, which needs framing to not look like a server
    /// rejection.
    /// </summary>
    protected static string Describe(Exception ex) => ex switch
    {
        ApiException api => api.Message,
        _ => string.Format(UiStrings.Error_Unreachable, ex.Message)
    };
}
