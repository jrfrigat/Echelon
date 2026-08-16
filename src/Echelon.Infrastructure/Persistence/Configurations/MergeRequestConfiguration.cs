using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Echelon.Core.Enums;
using Echelon.Infrastructure.Persistence.Models;

namespace Echelon.Infrastructure.Persistence.Configurations;

/// <summary>
/// The one thing about <see cref="MergeRequest"/> that no attribute can say.
/// </summary>
public class MergeRequestConfiguration : IEntityTypeConfiguration<MergeRequest>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<MergeRequest> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        // Stored as text, so the column reads "Merged" rather than "4": a status is read by
        // operators in ad-hoc SQL far more often than it is written, and renumbering the enum must
        // not silently reinterpret existing rows. There is no data annotation for a value
        // conversion, so this is one of the two mappings that stay fluent.
        b.Property(e => e.Status)
            .HasConversion(v => v.ToString(), v => Enum.Parse<MergeRequestStatus>(v))
            .HasMaxLength(50);
    }
}
