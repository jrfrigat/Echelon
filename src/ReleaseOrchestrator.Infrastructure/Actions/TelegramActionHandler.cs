using System.Net.Http.Json;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Actions;

namespace ReleaseOrchestrator.Infrastructure.Actions;

/// <summary>
/// Sends a Telegram message when its event fires (via the Bot API <c>sendMessage</c>).
/// </summary>
/// <remarks>
/// NOT VERIFIED against live Telegram. The message is a small template over the event payload.
///
/// The bot token is a secret carried in the binding's settings; today those settings are stored as
/// plaintext JSON on <c>ActionBinding</c>. Encrypting secret-flagged settings (as the VCS/tracker
/// tokens already are) is a follow-up -- until then, treat the token as visible to anyone with
/// database access.
/// </remarks>
internal sealed class TelegramActionHandler(HttpClient http) : IActionHandler
{
    /// <summary>The action-type key this handler is registered under.</summary>
    public const string ActionType = "telegram";

    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema =>
    [
        new ProviderSettingSchema("botToken", "Bot token", "Telegram bot token from BotFather.", Required: true, Secret: true),
        new ProviderSettingSchema("chatId", "Chat id", "Target chat id (a user, group, or channel).", Required: true),
        new ProviderSettingSchema("template", "Message template",
            "Optional. Placeholders: {task}, {environment}, {status}, {event}. Defaults to a one-line summary.", Required: false)
    ];

    /// <inheritdoc/>
    public async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken ct)
    {
        if (!context.Settings.TryGetValue("botToken", out var token) || string.IsNullOrWhiteSpace(token))
            return new ActionResult(false, "botToken is required.");
        if (!context.Settings.TryGetValue("chatId", out var chatId) || string.IsNullOrWhiteSpace(chatId))
            return new ActionResult(false, "chatId is required.");

        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        var response = await http.PostAsJsonAsync(url, new { chat_id = chatId, text = Render(context) }, ct).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? new ActionResult(true)
            : new ActionResult(false, $"Telegram returned {(int)response.StatusCode}.");
    }

    private static string Render(ActionContext context)
    {
        var template = context.Settings.TryGetValue("template", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : "Rollout {task} -> {environment}: {status}";

        return template
            .Replace("{task}", context.Payload.GetValueOrDefault("task", ""), StringComparison.Ordinal)
            .Replace("{environment}", context.Payload.GetValueOrDefault("environment", ""), StringComparison.Ordinal)
            .Replace("{status}", context.Payload.GetValueOrDefault("status", ""), StringComparison.Ordinal)
            .Replace("{event}", context.EventType, StringComparison.Ordinal);
    }
}
