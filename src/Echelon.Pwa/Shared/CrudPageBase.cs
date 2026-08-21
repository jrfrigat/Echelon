using Flare.Components;
using Echelon.Pwa.Models;
using Echelon.Pwa.Resources;

namespace Echelon.Pwa.Shared;

/// <summary>
/// The paged list + modal-editor shape shared by the admin pages. Each page supplies its fetch call
/// and its own markup; loading, paging, filtering, save and delete live here so they cannot drift
/// apart again.
/// </summary>
/// <remarks>
/// The grid owns the page number, the page size and the column filter boxes, and asks for a page
/// through <see cref="LoadPage"/>. That is why the page state that used to live here is gone: two
/// copies of "which page are we on" - one in the pager, one in the page - is how a filtered list ends
/// up asking for page 4 of a result that now has two.
/// </remarks>
public abstract class CrudPageBase<TItem> : PageBase
{
    /// <summary>The grid, so a save or a delete can make it re-read what it is showing.</summary>
    protected FlareDataGrid<TItem>? Grid;

    /// <summary>Which page the grid is on, 0-based. Kept so a reload returns to it.</summary>
    protected int CurrentPageIndex;

    protected bool ShowModal;
    protected bool Saving;

    /// <summary>Reads one page from the API, honouring the request's paging and column filters.</summary>
    /// <param name="request">What the grid is asking for.</param>
    protected abstract Task<PagedResult<TItem>> FetchPageAsync(DataGridRequest request);

    /// <summary>The grid's items provider: one page, or an empty one with the failure on screen.</summary>
    /// <param name="request">What the grid is asking for.</param>
    protected Task<DataGridResult<TItem>> LoadPage(DataGridRequest request) =>
        LoadPageAsync(async () =>
        {
            var page = await FetchPageAsync(request);
            return new DataGridResult<TItem>(page.Items, page.Total);
        });

    /// <summary>Remembers the page the grid moved to, so a reload can come back to it.</summary>
    /// <param name="pageIndex">0-based page index, as the grid counts.</param>
    protected void OnGridPageChanged(int pageIndex) => CurrentPageIndex = pageIndex;

    /// <summary>Re-reads the page the grid is on - what a save or a delete has to do.</summary>
    protected async Task ReloadAsync()
    {
        if (Grid is not null) await Grid.GoToPageAsync(CurrentPageIndex);
    }

    protected void CloseModal() => ShowModal = false;

    /// <summary>FlareDialog reports its own dismissals (Escape, scrim click) through here.</summary>
    protected void OnDialogVisibleChanged(bool visible) => ShowModal = visible;

    /// <summary>
    /// Whether the editor is missing something required. Overridden by a page whose dialog can be
    /// submitted with Enter, so the keyboard path refuses exactly what the disabled button refuses.
    /// </summary>
    /// <remarks>
    /// Defaults to false - a page that never overrides it behaves as it always did. It exists because
    /// Enter does not consult a button's <c>Disabled</c>: once a dialog became a real form, the
    /// keyboard could submit a half-filled editor that the mouse could not.
    /// </remarks>
    protected virtual bool IsEditorIncomplete => false;

    /// <summary>Saves via <paramref name="save"/>, then closes the modal and reloads on success.</summary>
    /// <param name="save">The call that persists the editor.</param>
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
        await ReloadAsync();
    }

    /// <param name="what">Names the row in the prompt, e.g. "connection 'gitlab-prod'".</param>
    /// <param name="delete">The call that removes it.</param>
    protected async Task DeleteAsync(string what, Func<Task> delete)
    {
        if (!await ConfirmAsync(
                UiStrings.Confirm_Delete_Title,
                string.Format(UiStrings.Confirm_Delete_Message, what))) return;

        if (await RunAsync(delete, UiStrings.Common_Deleted)) await ReloadAsync();
    }
}
