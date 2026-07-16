using ReleaseOrchestrator.Pwa.Models;
using ReleaseOrchestrator.Pwa.Resources;

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

    /// <summary>Page count for FlarePagination, which counts pages where the API reports rows.</summary>
    protected int TotalPages => Result is null || Result.PageSize <= 0
        ? 1
        : (int)Math.Ceiling(Result.Total / (double)Result.PageSize);

    protected abstract Task<PagedResult<TItem>> FetchPageAsync(int page);

    protected override Task OnInitializedAsync() => LoadPage(1);

    protected void CloseModal() => ShowModal = false;

    /// <summary>FlareDialog reports its own dismissals (Escape, scrim click) through here.</summary>
    protected void OnDialogVisibleChanged(bool visible) => ShowModal = visible;

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
    /// <param name="success">Notice to show; the generic "Saved." when omitted. Not a default
    /// parameter value: a resource lookup is not a compile-time constant.</param>
    protected async Task SaveAsync(Func<Task> save, string? success = null)
    {
        Saving = true;
        var saved = await RunAsync(save, success ?? UiStrings.Common_Saved);
        Saving = false;

        if (!saved) return;

        ShowModal = false;
        await LoadPage(Page);
    }

    /// <param name="what">Names the row in the prompt, e.g. "connection 'gitlab-prod'".</param>
    protected async Task DeleteAsync(string what, Func<Task> delete)
    {
        if (!await ConfirmAsync(
                UiStrings.Confirm_Delete_Title,
                string.Format(UiStrings.Confirm_Delete_Message, what))) return;

        if (await RunAsync(delete, UiStrings.Common_Deleted)) await LoadPage(Page);
    }
}
