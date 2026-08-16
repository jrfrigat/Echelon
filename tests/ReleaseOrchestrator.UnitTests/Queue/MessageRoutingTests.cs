using System.Reflection;
using Rebus.Handlers;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Queue;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// Guards the routing invariant: every message a handler consumes is routable.
/// </summary>
/// <remarks>
/// The bus routes <c>MapAssemblyDerivedFrom&lt;IMessage&gt;</c> - a message type that does not
/// implement <see cref="IMessage"/> has no destination, and the failure surfaces only at runtime
/// when something calls <c>bus.Send</c> on it and Rebus finds no route. Every message here is both
/// handled and sent, so requiring each handled type to implement the marker pins the whole set at
/// test time: add a handler for a message that forgot the interface and this fails, rather than the
/// first send in production.
/// </remarks>
public sealed class MessageRoutingTests
{
    [Fact]
    public void EveryHandledMessageImplementsIMessageAndIsRoutable()
    {
        var handledMessageTypes = typeof(MessagingSetup).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHandleMessages<>))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct()
            .ToList();

        // Sanity: the reflection actually found the handlers, so a green result means something.
        Assert.NotEmpty(handledMessageTypes);

        var unroutable = handledMessageTypes
            .Where(message => !typeof(IMessage).IsAssignableFrom(message))
            .Select(message => message.Name)
            .ToList();

        Assert.True(
            unroutable.Count == 0,
            $"These handled messages do not implement IMessage and would have no route: {string.Join(", ", unroutable)}");
    }

    /// <summary>
    /// The contracts namespace holds messages and nothing else that looks like one: every record in
    /// it implements <see cref="IMessage"/>, so a new contract dropped in the folder without the
    /// marker is caught here even before a handler for it exists.
    /// </summary>
    [Fact]
    public void EveryRecordInTheContractsNamespaceImplementsIMessage()
    {
        const string contractsNamespace = "ReleaseOrchestrator.Application.Contracts.Messages";

        var records = typeof(IMessage).Assembly
            .GetTypes()
            .Where(type => type.Namespace == contractsNamespace)
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            // Only top-level records: a NESTED record is a payload of the message that contains it
            // (BranchesObserved.Branch), never sent on its own, so it needs no routing marker. Nested
            // types report their containing type's namespace, which is why they must be excluded here
            // rather than by the namespace filter above.
            .Where(type => !type.IsNested)
            // Records carry a compiler-generated clone method; this distinguishes them from the
            // static MessageRouting helper that also lives here.
            .Where(type => type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null)
            .ToList();

        Assert.NotEmpty(records);

        var missing = records
            .Where(record => !typeof(IMessage).IsAssignableFrom(record))
            .Select(record => record.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These message records do not implement IMessage: {string.Join(", ", missing)}");
    }
}
