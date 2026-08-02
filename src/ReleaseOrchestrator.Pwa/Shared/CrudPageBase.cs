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

    /// <summary>
    /// Whether the editor is missing something required. Overridden by a page whose dialog can be
    /// submitted with Enter, so the keyboard path refuses exactly what the disabled button refuses.
    /// </summary>
    /// <remarks>
    /// Defaults to false — a page that never overrides it behaves as it always did. It exists because
    /// Enter does not consult a button's <c>Disabled</c>: once a dialog became a real form, the
    /// keyboard could submit a half-filled editor that the mouse could not.
    /// </remarks>
    protected virtual bool IsEditorIncomplete => false;

    /// <summary>Saves via <paramref name="save"/>, then closes the modal and reloads the page on success.</summary>
    /// <param name="success">Notice to show; the generic "Saved." when omitted. Not a default
    /// parameter value: a resource lookup is not a compile-time constant.</param>
    protected async Task SaveAsync(Func<Task> save, string? success = null)
    {
        // Guards the keyboard path: Enter can fire this while a save is already in flight, or while
        // the editor is in the state the disabled Save button exists to refuse.
        if (Saving || IsEditorIncomplete) return;

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
