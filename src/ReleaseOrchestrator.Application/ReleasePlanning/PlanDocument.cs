using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>A merge request in an imported plan, with the wave the author put it in.</summary>
/// <param name="MergeRequestKey">The natural key: connection, repository path and the provider's id.</param>
/// <param name="Wave">The 1-based wave. The one field of a plan document an author actually sets.</param>
public sealed record PlanDocumentItem(string MergeRequestKey, int Wave);

/// <summary>A task in an imported plan, with the merge requests hanging off it.</summary>
public sealed record PlanDocumentNode(string TaskKey, IReadOnlyList<PlanDocumentItem> Items);

/// <summary>A per-task rollout plan as written in YAML.</summary>
/// <param name="Version">Schema version. The only version is 1.</param>
/// <param name="TargetTaskKey">The task the plan rolls out.</param>
/// <param name="Nodes">The closure, one node per task.</param>
public sealed record PlanDocument(int Version, string TargetTaskKey, IReadOnlyList<PlanDocumentNode> Nodes);

/// <summary>What reading a plan document produced.</summary>
/// <param name="Document">The parsed document, or null when it could not be read.</param>
/// <param name="Errors">Everything wrong with it, in the order found. Empty when it is valid.</param>
public sealed record PlanDocumentParseResult(PlanDocument? Document, IReadOnlyList<string> Errors)
{
    /// <summary>True when the document parsed and nothing was wrong with it.</summary>
    public bool IsValid => Document is not null && Errors.Count == 0;
}

/// <summary>
/// Reads the per-task plan document - the schema <c>RolloutPlanner</c> exports - back into
/// <see cref="PlanDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads the representation model rather than deserializing onto the records, for the same reasons as
/// <see cref="OrderingRuleDocument"/>: error messages that name a line and a key, unknown keys as
/// errors rather than silent no-ops, and every problem reported in one pass.
/// </para>
/// <para>
/// Three keys the exporter writes are accepted and IGNORED - <c>plan_version</c>, <c>depends_on</c>
/// and <c>conflicts</c>. They are output, not input: a plan's version is assigned when it is stored,
/// the wait graph belongs to the atlas, and the conflicts are what the derivation concluded. Ignoring
/// them rather than rejecting them is what lets an operator export a plan, edit the waves and post it
/// straight back - a schema that could not round-trip would be a schema nobody uses.
/// </para>
/// <para>
/// This is a SHAPE reader. Whether the keys name real tasks and merge requests, and whether the plan
/// is even the same plan, is decided against the database by the planner: this class has no way to
/// know and no business guessing.
/// </para>
/// </remarks>
public static class PlanDocumentReader
{
    private const string VersionKey = "version";
    private const string TargetTaskKey = "target_task";
    private const string NodesKey = "nodes";
    private const string PlanVersionKey = "plan_version";
    private const string ConflictsKey = "conflicts";
    private const string TaskKey = "task";
    private const string DependsOnKey = "depends_on";
    private const string MergeRequestsKey = "merge_requests";
    private const string MrKey = "mr";
    private const string WaveKey = "wave";

    /// <summary>Parses and shape-checks a plan document.</summary>
    /// <param name="text">The document text.</param>
    /// <remarks>
    /// Empty text is an ERROR here, unlike an empty ordering-rule document. "No rules configured" is
    /// an ordinary state of an installation; "import this empty plan" is a mistake every time.
    /// </remarks>
    public static PlanDocumentParseResult Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new PlanDocumentParseResult(null, ["The document is empty."]);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            return new PlanDocumentParseResult(
                null, [$"The document is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}"]);
        }

        if (stream.Documents.Count == 0)
            return new PlanDocumentParseResult(null, ["The document is empty."]);

        if (stream.Documents.Count > 1)
            return new PlanDocumentParseResult(
                null, ["The file holds more than one YAML document; a plan must be a single document."]);

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            return new PlanDocumentParseResult(null, ["The document must be a mapping."]);

        var errors = new List<string>();

        RejectUnknownKeys(root, [VersionKey, TargetTaskKey, PlanVersionKey, NodesKey, ConflictsKey], "document", errors);

        var version = ReadVersion(root, errors);
        var target = ReadRequiredString(root, TargetTaskKey, "document", errors);
        var nodes = ReadNodes(root, errors);

        var document = new PlanDocument(version, target ?? string.Empty, nodes);
        return new PlanDocumentParseResult(errors.Count == 0 ? document : null, errors);
    }

    private static int ReadVersion(YamlMappingNode root, List<string> errors)
    {
        if (Child(root, VersionKey) is not { } node)
        {
            errors.Add("'version' is required. The only version is 1.");
            return 1;
        }

        if (node is not YamlScalarNode scalar || !int.TryParse(scalar.Value, out var version))
        {
            errors.Add($"{At(node)}'version' must be a number. The only version is 1.");
            return 1;
        }

        // Refused rather than assumed, as in the ordering rules: a document written against a future
        // schema would otherwise be read with today's rules and silently mean something else.
        if (version != 1) errors.Add($"{At(node)}Unsupported version {version}. The only version is 1.");

        return version;
    }

    private static List<PlanDocumentNode> ReadNodes(YamlMappingNode root, List<string> errors)
    {
        var nodes = new List<PlanDocumentNode>();

        if (Child(root, NodesKey) is not { } node)
        {
            errors.Add("'nodes' is required: a plan lists the tasks it covers.");
            return nodes;
        }

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{At(node)}'nodes' must be a list.");
            return nodes;
        }

        var seenTasks = new HashSet<string>(StringComparer.Ordinal);
        var seenMrs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in sequence)
        {
            if (entry is not YamlMappingNode mapping)
            {
                errors.Add($"{At(entry)}Each entry of 'nodes' must be a mapping.");
                continue;
            }

            RejectUnknownKeys(mapping, [TaskKey, DependsOnKey, MergeRequestsKey], "nodes[]", errors);

            var taskKey = ReadRequiredString(mapping, TaskKey, "nodes[]", errors);
            if (taskKey is null) continue;

            // A task stated twice would give two answers about the same node's merge requests, and
            // whichever won would depend on read order.
            if (!seenTasks.Add(taskKey))
            {
                errors.Add($"{At(mapping)}Task '{taskKey}' appears more than once in 'nodes'.");
                continue;
            }

            nodes.Add(new PlanDocumentNode(taskKey, ReadItems(mapping, taskKey, seenMrs, errors)));
        }

        return nodes;
    }

    private static List<PlanDocumentItem> ReadItems(
        YamlMappingNode node, string taskKey, HashSet<string> seenMrs, List<string> errors)
    {
        var items = new List<PlanDocumentItem>();

        if (Child(node, MergeRequestsKey) is not { } listNode) return items;

        if (listNode is not YamlSequenceNode sequence)
        {
            errors.Add($"{At(listNode)}'{taskKey}.merge_requests' must be a list.");
            return items;
        }

        foreach (var entry in sequence)
        {
            if (entry is not YamlMappingNode mapping)
            {
                errors.Add($"{At(entry)}Each entry of '{taskKey}.merge_requests' must be a mapping.");
                continue;
            }

            RejectUnknownKeys(mapping, [MrKey, WaveKey], $"{taskKey}.merge_requests[]", errors);

            var key = ReadRequiredString(mapping, MrKey, $"{taskKey}.merge_requests[]", errors);
            var wave = ReadWave(mapping, taskKey, errors);
            if (key is null || wave is null) continue;

            // Across the whole document, not just this node: the same merge request in two waves is
            // two contradictory instructions, and it would also mean it belongs to two tasks.
            if (!seenMrs.Add(key))
            {
                errors.Add($"{At(mapping)}Merge request '{key}' appears more than once in the document.");
                continue;
            }

            items.Add(new PlanDocumentItem(key, wave.Value));
        }

        return items;
    }

    private static int? ReadWave(YamlMappingNode mapping, string taskKey, List<string> errors)
    {
        if (Child(mapping, WaveKey) is not { } node)
        {
            errors.Add($"{At(mapping)}'{taskKey}.merge_requests[].wave' is required: it is what an import states.");
            return null;
        }

        if (node is not YamlScalarNode scalar || !int.TryParse(scalar.Value, out var wave))
        {
            errors.Add($"{At(node)}'{taskKey}.merge_requests[].wave' must be a whole number.");
            return null;
        }

        if (wave < 1)
        {
            errors.Add($"{At(node)}'{taskKey}.merge_requests[].wave' must be 1 or greater; waves are 1-based.");
            return null;
        }

        return wave;
    }

    private static string? ReadRequiredString(
        YamlMappingNode mapping, string key, string path, List<string> errors)
    {
        if (Child(mapping, key) is not { } node)
        {
            errors.Add($"'{path}.{key}' is required.");
            return null;
        }

        var value = (node as YamlScalarNode)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{At(node)}'{path}.{key}' must be a non-empty string.");
            return null;
        }

        return value;
    }

    private static void RejectUnknownKeys(
        YamlMappingNode mapping, string[] known, string path, List<string> errors)
    {
        foreach (var (keyNode, _) in mapping)
        {
            var key = (keyNode as YamlScalarNode)?.Value;
            if (key is null || known.Contains(key, StringComparer.Ordinal)) continue;

            errors.Add($"{At(keyNode)}'{path}' has unknown key '{key}'. Valid keys: {string.Join(", ", known)}.");
        }
    }

    private static YamlNode? Child(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    /// <summary>Where a node sits, so an error points at the line the operator has to edit.</summary>
    private static string At(YamlNode node) =>
        node.Start.Line > 0 ? $"Line {node.Start.Line}: " : "";
}
