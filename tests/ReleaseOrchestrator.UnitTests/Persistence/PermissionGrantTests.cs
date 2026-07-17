using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Persistence;

/// <summary>
/// A permission may be held once, or not at all.
/// </summary>
/// <remarks>
/// The API grants by check-then-insert, which races with itself: two admins granting the same
/// claim at the same moment both see nothing and both insert. Revocation deletes one row by id and
/// logs "revoked", so the group keeps the permission that the audit trail says it lost — a
/// permission surviving its own revocation, reported as gone. The same shape the merge request and
/// task tables already closed with a unique index; the permission tables were left out.
/// </remarks>
public sealed class PermissionGrantTests : IAsyncLifetime
{
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private PermissionClaim _claim = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _claim = new PermissionClaim { Id = Guid.NewGuid(), Name = "release.plan.approve" };
        _db.PermissionClaims.Add(_claim);
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task GrantToGroupAsync(string sid, Guid claimId)
    {
        _db.GroupPermissionMappings.Add(new GroupPermissionMapping
        {
            Id = Guid.NewGuid(),
            AdGroupSid = sid,
            PermissionClaimId = claimId
        });
        await _db.SaveChangesAsync(Ct);
    }

    private async Task GrantToUserAsync(string userId, Guid claimId)
    {
        _db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionClaimId = claimId
        });
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AGroupCannotHoldTheSameClaimTwice()
    {
        await GrantToGroupAsync("S-1-5-21-1", _claim.Id);

        await Assert.ThrowsAsync<DbUpdateException>(() => GrantToGroupAsync("S-1-5-21-1", _claim.Id));
    }

    [Fact]
    public async Task AUserCannotHoldTheSameClaimTwice()
    {
        var userId = Guid.NewGuid().ToString("D");
        await GrantToUserAsync(userId, _claim.Id);

        await Assert.ThrowsAsync<DbUpdateException>(() => GrantToUserAsync(userId, _claim.Id));
    }

    /// <summary>
    /// The constraint is the pair, not either half: a group may hold several claims, and a claim
    /// may be held by several groups. An index over one column alone would break the product.
    /// </summary>
    [Fact]
    public async Task TheSameGroupMayHoldDifferentClaimsAndTheSameClaimMayGoToDifferentGroups()
    {
        var other = new PermissionClaim { Id = Guid.NewGuid(), Name = "config.edit" };
        _db.PermissionClaims.Add(other);
        await _db.SaveChangesAsync(Ct);

        await GrantToGroupAsync("S-1-5-21-1", _claim.Id);
        await GrantToGroupAsync("S-1-5-21-1", other.Id);
        await GrantToGroupAsync("S-1-5-21-2", _claim.Id);

        Assert.Equal(3, await _db.GroupPermissionMappings.CountAsync(Ct));
    }

    [Fact]
    public async Task TheSameUserMayHoldDifferentClaims()
    {
        var other = new PermissionClaim { Id = Guid.NewGuid(), Name = "config.edit" };
        _db.PermissionClaims.Add(other);
        await _db.SaveChangesAsync(Ct);

        var userId = Guid.NewGuid().ToString("D");
        await GrantToUserAsync(userId, _claim.Id);
        await GrantToUserAsync(userId, other.Id);

        Assert.Equal(2, await _db.UserPermissionOverrides.CountAsync(Ct));
    }

    /// <summary>
    /// The consequence the index exists to prevent, stated as a test: with duplicates possible,
    /// deleting the one row an admin can see leaves the permission in force.
    /// </summary>
    [Fact]
    public async Task RevokingAGrantLeavesTheGroupWithoutTheClaim()
    {
        await GrantToGroupAsync("S-1-5-21-1", _claim.Id);
        var granted = await _db.GroupPermissionMappings.SingleAsync(Ct);

        _db.GroupPermissionMappings.Remove(granted);
        await _db.SaveChangesAsync(Ct);

        Assert.False(await _db.GroupPermissionMappings.AnyAsync(
            m => m.AdGroupSid == "S-1-5-21-1" && m.PermissionClaimId == _claim.Id, Ct));
    }

    /// <summary>
    /// UserId is 36 characters because a normalised object id is exactly that long. The length is
    /// not cosmetic: at 450 the column is nvarchar(450) — 900 bytes, SQL Server's whole index key
    /// budget — and the unique index above could not be created next to a 16-byte Guid.
    /// </summary>
    [Fact]
    public void UserIdIsExactlyLongEnoughForANormalisedObjectId()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=model-only;Database=model-only;Trusted_Connection=True")
            .Options;
        using var context = new AppDbContext(options);

        var maxLength = context.Model
            .FindEntityType(typeof(UserPermissionOverride))!
            .FindProperty(nameof(UserPermissionOverride.UserId))!
            .GetMaxLength();

        Assert.Equal(Guid.NewGuid().ToString("D").Length, maxLength);
    }
}
