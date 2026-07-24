using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

/// <summary>
/// The one thing about <see cref="RepositoryDeployTarget"/> that no attribute can say.
/// </summary>
public class RepositoryDeployTargetConfiguration : IEntityTypeConfiguration<RepositoryDeployTarget>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<RepositoryDeployTarget> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        // Stored as text, like MergeRequestStatus, and here it matters more than readability:
        // RedeployPolicy has no zero member on purpose, so a column defaulting to the integer 0 would
        // hold a value the enum does not define and read back as "redeploy freely" by accident. Text
        // has no such default -- an empty or unknown string fails to parse loudly instead of quietly
        // becoming a dangerous answer.
        b.Property(e => e.RedeployPolicy)
            .HasConversion(v => v.ToString(), v => Enum.Parse<RedeployPolicy>(v))
            .HasMaxLength(20);
    }
}
