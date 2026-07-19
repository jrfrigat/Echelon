using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseOrchestrator.Infrastructure.Actions;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Actions;
using ReleaseOrchestrator.UnitTests.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Actions;

/// <summary>
/// Covers the dispatcher over a real (in-memory SQLite) database: it runs the enabled bindings that
/// match the event and scope, skips the rest, and never lets a handler failure escape.
/// </summary>
public class ActionDispatcherTests : PlannerTestBase
{
    private sealed class RecordingHandler : IActionHandler
    {
        public readonly List<ActionContext> Calls = [];
        public bool Throw { get; init; }

        public IReadOnlyList<ProviderSettingSchema> SettingsSchema => [];

        public Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException("boom");
            Calls.Add(context);
            return Task.FromResult(new ActionResult(true));
        }
    }

    private sealed class FakeFactory(IActionHandler handler) : IActionHandlerFactory
    {
        public IReadOnlyCollection<string> AvailableActionTypes => ["telegram"];
        public IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string actionType) => [];
        public IActionHandler Resolve(string actionType) => handler;
    }

    internal static TokenProtector Protector() =>
        new(new ServiceCollection().AddDataProtection().Services.BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>());

    private ActionDispatcher Dispatcher(IActionHandler handler) =>
        new(Db, new FakeFactory(handler), Protector(), NullLogger<ActionDispatcher>.Instance);

    private void AddBinding(string eventType, string? scope = null, bool enabled = true) =>
        Db.ActionBindings.Add(new ActionBinding
        {
            Id = Guid.NewGuid(), EventType = eventType, ActionType = "telegram", Scope = scope, Enabled = enabled
        });

    private static Dictionary<string, string> Payload(string environment = "prod") =>
        new() { ["task"] = "PROJ-1", ["environment"] = environment, ["status"] = "Succeeded" };

    [Fact]
    public async Task Dispatches_EnabledMatchingBinding()
    {
        var handler = new RecordingHandler();
        AddBinding("RolloutSucceeded");
        await Db.SaveChangesAsync(Ct);

        await Dispatcher(handler).DispatchAsync("RolloutSucceeded", Payload(), Ct);

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Skips_DisabledAndWrongEvent()
    {
        var handler = new RecordingHandler();
        AddBinding("RolloutSucceeded", enabled: false);
        AddBinding("RolloutFailed");
        await Db.SaveChangesAsync(Ct);

        await Dispatcher(handler).DispatchAsync("RolloutSucceeded", Payload(), Ct);

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Honours_ScopeFilter()
    {
        var handler = new RecordingHandler();
        AddBinding("RolloutSucceeded", scope: "env:prod");
        await Db.SaveChangesAsync(Ct);

        await Dispatcher(handler).DispatchAsync("RolloutSucceeded", Payload("staging"), Ct);
        Assert.Empty(handler.Calls);   // bound to prod, event was staging

        await Dispatcher(handler).DispatchAsync("RolloutSucceeded", Payload("prod"), Ct);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Isolates_ThrowingHandler()
    {
        var handler = new RecordingHandler { Throw = true };
        AddBinding("RolloutSucceeded");
        await Db.SaveChangesAsync(Ct);

        // Must not throw: an action failure never propagates to the deploy path.
        await Dispatcher(handler).DispatchAsync("RolloutSucceeded", Payload(), Ct);
    }
}
