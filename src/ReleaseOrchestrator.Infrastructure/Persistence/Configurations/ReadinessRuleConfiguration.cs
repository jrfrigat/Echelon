using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

/// <summary>
/// The one thing about <see cref="ReadinessRule"/> that no attribute can say.
/// </summary>
public class ReadinessRuleConfiguration : IEntityTypeConfiguration<ReadinessRule>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ReadinessRule> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        // Stored as text, like every other ReadyRule column: the enum has no zero member on purpose,
        // so an integer column defaulting to 0 would hold a value the enum does not define. Text has no
        // such default -- an unwritten column fails to parse loudly rather than becoming a silent rule.
        b.Property(r => r.Mode)
            .HasConversion(v => v.ToString(), v => Enum.Parse<ReadyRule>(v))
            .HasMaxLength(20);
    }
}
