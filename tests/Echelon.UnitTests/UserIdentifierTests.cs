using System.Security.Claims;
using Echelon.Infrastructure.Auth;
using Xunit;

namespace Echelon.UnitTests;

/// <summary>
/// Permissions are keyed on this identifier, so a mismatch here silently grants nothing -
/// which is exactly what happened while lookups preferred `sub` and admins entered an oid.
/// </summary>
public class UserIdentifierTests
{
    private static ClaimsPrincipal PrincipalWith(string claimType, string value) =>
        new(new ClaimsIdentity([new Claim(claimType, value)], "test"));

    [Fact]
    public void ResolvesObjectIdClaim()
    {
        var oid = Guid.NewGuid();

        Assert.True(UserIdentifier.TryResolve(PrincipalWith(UserIdentifier.ObjectIdClaimType, oid.ToString()), out var userId));
        Assert.Equal(oid.ToString("D"), userId);
    }

    [Fact]
    public void ResolvesMappedObjectIdClaimUri()
    {
        // Inbound claim mapping rewrites "oid" to this URI.
        var oid = Guid.NewGuid();

        Assert.True(UserIdentifier.TryResolve(PrincipalWith(UserIdentifier.ObjectIdClaimUri, oid.ToString()), out var userId));
        Assert.Equal(oid.ToString("D"), userId);
    }

    [Fact]
    public void IgnoresNameIdentifier()
    {
        // `sub` is pairwise and never appears in the portal, so it must not be used.
        Assert.False(UserIdentifier.TryResolve(
            PrincipalWith(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), out _));
    }

    [Fact]
    public void FailsWhenNoObjectIdIsPresent()
    {
        // Fail closed: falling back to an empty id collapsed every such caller onto one cache entry.
        Assert.False(UserIdentifier.TryResolve(new ClaimsPrincipal(new ClaimsIdentity([], "test")), out var userId));
        Assert.Equal(string.Empty, userId);
    }

    [Theory]
    [InlineData("{6F9619FF-8B86-D011-B42D-00C04FC964FF}")]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964FF")]
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    public void NormalisesCasingAndBraces(string raw)
    {
        Assert.True(UserIdentifier.TryNormalize(raw, out var userId));
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", userId);
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("DOMAIN\\user")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData(null)]
    // Guid.Empty is a parseable GUID but never a real object id, so it must not match a row.
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void RejectsAnythingThatIsNotAnObjectId(string? raw)
        => Assert.False(UserIdentifier.TryNormalize(raw, out _));
}
