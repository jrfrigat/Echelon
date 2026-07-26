using System.Security.Cryptography;
using System.Text;

namespace ReleaseOrchestrator.Infrastructure.Ingestion;

/// <summary>
/// Builds a deterministic event id for a polled merge request.
/// </summary>
/// <remarks>
/// The whole reason a poller can share the dedup inbox: an unchanged merge request hashes to the
/// same id every cycle and self-dedups, while a status or label change hashes to a new id and is
/// processed. Without this, every poll would re-process every open merge request
/// (docs/issues/008-ingestion-and-messaging.md).
/// </remarks>
internal static class PollingEventId
{
    /// <summary>A stable id over the fields that decide whether a poll observation is a change.</summary>
    public static string For(
        string source, string repositoryExternalId, string mergeRequestId, string status,
        IEnumerable<string> labels, string? pipeline = null)
    {
        var material = string.Join('|',
            source, repositoryExternalId, mergeRequestId, status,
            string.Join(',', labels.OrderBy(l => l, StringComparer.Ordinal)),
            // Pipeline is part of what makes an observation a change: a green pipeline that was
            // running must re-process so a pipeline:success rule sees it, not dedup away.
            pipeline ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
