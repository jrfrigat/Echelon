using ReleaseOrchestrator.Pwa.Models;

namespace ReleaseOrchestrator.Pwa.Shared;

/// <summary>
/// The paged list + modal-editor shape shared by the admin pages. Each page supplies its fetch
/// call and its own markup; load, paging, save and delete flows live here so they cannot drift
/// apart again.
/// </summary>
public abstract class CrudPageBase<TItem> : PageBase
{
    protected PagedResult<TItem>? Result;
    protected bool Loading = true;
    protected bool ShowModal;
    protected bool Saving;
    protected int Page = 1;

    protected abstract Task<PagedResult<TItem>> FetchPageAsync(int page);

    protected override Task OnInitializedAsync() => LoadPage(1);

    protected async Task LoadPage(int page)
    {
        Loading = true;
        Error = null;
        try
        {
            Result = await FetchPageAsync(page);
            // Only after a successful fetch, so the pager keeps describing the rows on screen.
            Page = page;
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Saves via <paramref name="save"/>, then closes the modal and reloads the page on success.</summary>
    protected async Task SaveAsync(Func<Task> save, string success = "Saved.")
    {
        Saving = true;
        var saved = await RunAsync(save, success);
        Saving = false;

        if (!saved) return;

        ShowModal = false;
        await LoadPage(Page);
    }

    /// <param name="what">Names the row in the prompt, e.g. "connection 'gitlab-prod'".</param>
    protected async Task DeleteAsync(string what, Func<Task> delete)
    {
        if (!await ConfirmAsync($"Delete {what}? This cannot be undone.")) return;

        if (await RunAsync(delete, "Deleted.")) await LoadPage(Page);
    }
}
