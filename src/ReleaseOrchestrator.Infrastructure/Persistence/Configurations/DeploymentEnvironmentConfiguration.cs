using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

/// <summary>
/// The one thing about <see cref="DeploymentEnvironment"/> that no attribute can say.
/// </summary>
public class DeploymentEnvironmentConfiguration : IEntityTypeConfiguration<DeploymentEnvironment>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<DeploymentEnvironment> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        // Stored as text, like the other readiness enums and for the sharper reason: ReadyRule has
        // no zero member on purpose, so an integer column defaulting to 0 would hold a value the enum
        // does not define. Text has no such default -- an unset column would fail to parse loudly
        // rather than quietly become a rule, and an existing row reads "NoGate" as written.
        b.Property(e => e.ReadyRule)
            .HasConversion(v => v.ToString(), v => Enum.Parse<ReadyRule>(v))
            .HasMaxLength(20);
    }
}
