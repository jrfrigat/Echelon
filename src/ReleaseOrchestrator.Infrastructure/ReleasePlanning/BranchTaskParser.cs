using System.Text.RegularExpressions;

namespace ReleaseOrchestrator.Infrastructure.ReleasePlanning;

public static class BranchTaskParser
{
    private static readonly Regex Pattern = new(@"([A-Z][A-Z0-9]+-\d+)", RegexOptions.Compiled);

    public static string? ParseTaskId(string branchName)
    {
        var match = Pattern.Match(branchName);
        return match.Success ? match.Groups[1].Value : null;
    }
}
