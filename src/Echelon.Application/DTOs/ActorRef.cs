namespace Echelon.Application.DTOs;

/// <summary>
/// Who caused a change: a person, a service principal, or one of the machine paths that run with no
/// caller at all.
/// </summary>
/// <remarks>
/// <para>
/// Three fields rather than a bare object id, because a null id is ambiguous in a way that matters.
/// "No id" is true of a webhook consumer, of a poller, and of a signed-in operator whose token
/// carried no usable <c>oid</c> - and those are different facts. <see cref="Kind"/> is what
/// separates them, so the timeline can say "an operator, identity unresolvable" instead of quietly
/// showing a machine.
/// </para>
/// <para>
/// <see cref="DisplayName"/> is captured at write time on purpose. The context derives from
/// <c>IdentityDbContext</c>, but nothing in this codebase ever creates a user - there is no
/// <c>UserManager</c> anywhere - so <c>AspNetUsers</c> stays empty and a name cannot be looked up
/// afterwards. A name not captured now is a raw GUID forever.
/// </para>
/// <para>
/// Primitives only: this crosses the port boundary into Infrastructure exactly as the bare
/// <c>launchedByOid</c> string did before it, and carries no ASP.NET or EF type with it.
/// </para>
/// </remarks>
/// <param name="Kind">One of <see cref="ActorKinds"/>. Never null - an unknown actor is still a kind.</param>
/// <param name="Oid">The normalized Entra object id, when there is one.</param>
/// <param name="DisplayName">The name as the token spelled it, captured because it cannot be resolved later.</param>
public sealed record ActorRef(string Kind, string? Oid, string? DisplayName)
{
    /// <summary>A machine path with no caller: the default for anything Infrastructure initiates itself.</summary>
    public static ActorRef System { get; } = new(ActorKinds.System, null, null);

    /// <summary>True when a person - not a service principal or a background path - caused this.</summary>
    public bool IsPerson => Kind is ActorKinds.User or ActorKinds.Unidentified;
}
