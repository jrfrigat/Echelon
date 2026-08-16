using ReleaseOrchestrator.Core.Enums;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>What reading an ordering-rule document produced.</summary>
/// <param name="Rules">The parsed document, or null when it could not be read.</param>
/// <param name="Errors">Everything wrong with it, in the order found. Empty when it is valid.</param>
public sealed record OrderingRuleParseResult(OrderingRules? Rules, IReadOnlyList<string> Errors)
{
    /// <summary>True when the document parsed and nothing was wrong with it.</summary>
    public bool IsValid => Rules is not null && Errors.Count == 0;
}

/// <summary>
/// Reads the ordering-rule document text into <see cref="OrderingRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads the representation model (<see cref="YamlStream"/>) rather than deserializing onto the
/// records, for the error messages. Deserialization answers "could not convert" and points at a type;
/// an operator editing deploy order needs the line, the key, and what the valid values were. It also
/// lets an unknown key be an ERROR: a mistyped <c>repositores:</c> that silently selects nothing is
/// precisely the failure this document must not have.
/// </para>
/// <para>
/// Every error is collected rather than thrown at the first one. A rule file is edited in bulk, and
/// being told about one mistake per round trip is how a five-minute edit becomes an afternoon.
/// </para>
/// <para>
/// Scalars are read as their literal text and converted here, which is deliberate. YAML's own
/// implicit typing is where <c>no</c> becomes <c>false</c> and <c>NO</c> becomes a boolean while
/// <c>No</c> may not - the "Norway problem". Reading the text and accepting only <c>true</c>/
/// <c>false</c> means an ambiguous value is rejected with a message instead of silently becoming
/// something the author did not write.
/// </para>
/// <para>
/// JSON is valid YAML, so a document saved before this reader existed still loads unchanged.
/// </para>
/// </remarks>
public static class OrderingRuleDocument
{
    private const string GroupsKey = "groups";
    private const string OrderKey = "order";
    private const string TasksKey = "tasks";
    private const string VersionKey = "version";

    /// <summary>Parses and validates a document.</summary>
    /// <param name="text">The document text. Empty or whitespace reads as "no rules", not as an error.</param>
    public static OrderingRuleParseResult Read(string? text)
    {
        // An installation that has configured nothing is the ordinary case, not a broken document.
        if (string.IsNullOrWhiteSpace(text)) return new OrderingRuleParseResult(OrderingRules.Empty, []);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            return new OrderingRuleParseResult(
                null, [$"The document is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}"]);
        }

        if (stream.Documents.Count == 0) return new OrderingRuleParseResult(OrderingRules.Empty, []);

        // A second document would be silently ignored, and "my rules did nothing" is a bad way to
        // discover a stray `---`.
        if (stream.Documents.Count > 1)
            return new OrderingRuleParseResult(
                null, ["The file holds more than one YAML document; ordering rules must be a single document."]);

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            return new OrderingRuleParseResult(null, ["The document must be a mapping."]);

        var errors = new List<string>();

        RejectUnknownKeys(root, [VersionKey, TasksKey, GroupsKey, OrderKey], "document", errors);

        var version = ReadVersion(root, errors);
        var groups = ReadGroups(root, errors);
        var order = ReadOrder(root, groups.Keys, errors);
        var tasks = ReadTasks(root, errors);

        var rules = new OrderingRules(version, tasks, groups, order);
        return new OrderingRuleParseResult(errors.Count == 0 ? rules : null, errors);
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

        // Refused rather than assumed: a document written against a future schema would otherwise be
        // read with today's rules and silently mean something else.
        if (version != 1) errors.Add($"{At(node)}Unsupported version {version}. The only version is 1.");

        return version;
    }

    private static Dictionary<string, WorkSelector> ReadGroups(YamlMappingNode root, List<string> errors)
    {
        var groups = new Dictionary<string, WorkSelector>(StringComparer.Ordinal);

        if (Child(root, GroupsKey) is not { } node) return groups;

        if (node is not YamlMappingNode mapping)
        {
            errors.Add($"{At(node)}'groups' must be a mapping of a group name to a selector.");
            return groups;
        }

        foreach (var (keyNode, valueNode) in mapping)
        {
            var name = (keyNode as YamlScalarNode)?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{At(keyNode)}A group name cannot be blank.");
                continue;
            }

            // Duplicate keys are a YAML error in a strict parser but tolerated by many; saying so is
            // cheaper than the operator wondering which of two definitions won.
            if (groups.ContainsKey(name))
            {
                errors.Add($"{At(keyNode)}Group '{name}' is defined more than once.");
                continue;
            }

            var selector = ReadSelector(valueNode, $"groups.{name}", errors);
            if (selector is not null) groups[name] = selector;
        }

        return groups;
    }

    private static WorkSelector? ReadSelector(YamlNode node, string path, List<string> errors)
    {
        // An empty mapping is legitimate: a group with no axes selects everything.
        if (node is YamlScalarNode { Value: null or "" }) return WorkSelector.Any;

        if (node is not YamlMappingNode mapping)
        {
            errors.Add($"{At(node)}'{path}' must be a mapping.");
            return null;
        }

        RejectUnknownKeys(
            mapping, ["connectors", "repositories", "branches", "task_keys", "labels", "exclude"], path, errors);

        WorkSelector? exclude = null;
        if (Child(mapping, "exclude") is { } excludeNode)
            exclude = ReadSelector(excludeNode, $"{path}.exclude", errors);

        return new WorkSelector(
            ReadStrings(mapping, "connectors", path, errors),
            ReadStrings(mapping, "repositories", path, errors),
            ReadStrings(mapping, "branches", path, errors),
            ReadStrings(mapping, "task_keys", path, errors),
            ReadStrings(mapping, "labels", path, errors),
            exclude);
    }

    private static List<GroupOrderSpec> ReadOrder(
        YamlMappingNode root, ICollection<string> knownGroups, List<string> errors)
    {
        var order = new List<GroupOrderSpec>();

        if (Child(root, OrderKey) is not { } node) return order;

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{At(node)}'order' must be a sequence of ordering rules.");
            return order;
        }

        var index = 0;
        foreach (var item in sequence)
        {
            var path = $"order[{index++}]";

            if (item is not YamlMappingNode mapping)
            {
                errors.Add($"{At(item)}'{path}' must be a mapping.");
                continue;
            }

            RejectUnknownKeys(mapping, ["group", "needs", "type", "scope"], path, errors);

            var group = ReadString(mapping, "group", path, errors);
            var needs = ReadStrings(mapping, "needs", path, errors);

            if (group is null) continue;

            // A group nothing has defined is a typo, and the fix is knowing what was available.
            if (!knownGroups.Contains(group))
                errors.Add($"{At(mapping)}'{path}.group' names an undefined group '{group}'. Defined: {Describe(knownGroups)}.");

            // A rule that needs nothing orders nothing, so it is a mistake rather than a no-op.
            if (needs.Count == 0)
                errors.Add($"{At(mapping)}'{path}.needs' must name at least one group; a rule that needs nothing orders nothing.");

            foreach (var need in needs)
            {
                if (!knownGroups.Contains(need))
                    errors.Add($"{At(mapping)}'{path}.needs' names an undefined group '{need}'. Defined: {Describe(knownGroups)}.");

                // Would be a self-edge for every member, which carries no ordering at all.
                if (string.Equals(need, group, StringComparison.Ordinal))
                    errors.Add($"{At(mapping)}'{path}' has group '{group}' needing itself.");
            }

            var type = ReadEnum(mapping, "type", path, errors, StackDependencyType.Hard, new()
            {
                ["hard"] = StackDependencyType.Hard,
                ["soft"] = StackDependencyType.Soft
            });

            var scope = ReadEnum(mapping, "scope", path, errors, OrderScope.AcrossPlan, new()
            {
                ["across_plan"] = OrderScope.AcrossPlan,
                ["within_task"] = OrderScope.WithinTask
            });

            order.Add(new GroupOrderSpec(group, needs, type, scope));
        }

        return order;
    }

    private static TaskPolicySpec ReadTasks(YamlMappingNode root, List<string> errors)
    {
        if (Child(root, TasksKey) is not { } node) return new TaskPolicySpec(null, null, null, []);

        if (node is not YamlMappingNode mapping)
        {
            errors.Add($"{At(node)}'tasks' must be a mapping.");
            return new TaskPolicySpec(null, null, null, []);
        }

        RejectUnknownKeys(
            mapping, ["wait_for_subtasks", "wait_for_linked", "group_order", "overrides"], TasksKey, errors);

        var overrides = ReadPolicyOverrides(mapping, errors);

        return new TaskPolicySpec(
            ReadBool(mapping, "wait_for_subtasks", TasksKey, errors),
            ReadBool(mapping, "wait_for_linked", TasksKey, errors),
            ReadGroupOrder(mapping, TasksKey, errors),
            overrides);
    }

    private static List<TaskPolicyOverride> ReadPolicyOverrides(YamlMappingNode tasks, List<string> errors)
    {
        var overrides = new List<TaskPolicyOverride>();

        if (Child(tasks, "overrides") is not { } node) return overrides;

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{At(node)}'tasks.overrides' must be a sequence.");
            return overrides;
        }

        var index = 0;
        foreach (var item in sequence)
        {
            var path = $"tasks.overrides[{index++}]";

            if (item is not YamlMappingNode mapping)
            {
                errors.Add($"{At(item)}'{path}' must be a mapping.");
                continue;
            }

            RejectUnknownKeys(
                mapping, ["match", "wait_for_subtasks", "wait_for_linked", "group_order"], path, errors);

            if (Child(mapping, "match") is not { } matchNode)
            {
                errors.Add($"{At(mapping)}'{path}.match' is required; without it the override would apply to every task.");
                continue;
            }

            var match = ReadSelector(matchNode, $"{path}.match", errors);
            if (match is null) continue;

            overrides.Add(new TaskPolicyOverride(
                match,
                ReadBool(mapping, "wait_for_subtasks", path, errors),
                ReadBool(mapping, "wait_for_linked", path, errors),
                ReadGroupOrder(mapping, path, errors)));
        }

        return overrides;
    }

    private static PrerequisiteGroupOrder? ReadGroupOrder(YamlMappingNode mapping, string path, List<string> errors)
    {
        if (Child(mapping, "group_order") is null) return null;

        return ReadEnum<PrerequisiteGroupOrder?>(mapping, "group_order", path, errors, null, new()
        {
            ["together"] = PrerequisiteGroupOrder.Together,
            ["subtasks_first"] = PrerequisiteGroupOrder.SubtasksFirst,
            ["linked_first"] = PrerequisiteGroupOrder.LinkedFirst
        });
    }

    /// <summary>
    /// An unknown key is an error, not something skipped.
    /// </summary>
    /// <remarks>
    /// A mistyped <c>repositores:</c> would otherwise leave a selector matching everything, or a rule
    /// doing nothing, with no sign anywhere. That is the exact class of silence this document cannot
    /// afford: the operator finds out at deploy time, from the order being wrong.
    /// </remarks>
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

    private static string? ReadString(YamlMappingNode mapping, string key, string path, List<string> errors)
    {
        if (Child(mapping, key) is not { } node)
        {
            errors.Add($"'{path}.{key}' is required.");
            return null;
        }

        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            errors.Add($"{At(node)}'{path}.{key}' must be a non-empty string.");
            return null;
        }

        return scalar.Value;
    }

    private static List<string> ReadStrings(YamlMappingNode mapping, string key, string path, List<string> errors)
    {
        var values = new List<string>();

        if (Child(mapping, key) is not { } node) return values;

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{At(node)}'{path}.{key}' must be a sequence of strings.");
            return values;
        }

        foreach (var item in sequence)
        {
            if (item is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                errors.Add($"{At(item)}'{path}.{key}' must contain only non-empty strings.");
                continue;
            }

            values.Add(scalar.Value);
        }

        return values;
    }

    /// <summary>
    /// Reads a boolean, accepting only <c>true</c> and <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than YAML's implicit typing, which also reads <c>yes</c>, <c>on</c> and
    /// - famously - <c>no</c> as booleans, with the exact set depending on the YAML version. On a
    /// switch that decides whether a rollout waits for its subtasks, an ambiguous spelling is better
    /// refused than resolved by a rule nobody remembers.
    /// </remarks>
    private static bool? ReadBool(YamlMappingNode mapping, string key, string path, List<string> errors)
    {
        if (Child(mapping, key) is not { } node) return null;

        if (node is YamlScalarNode scalar)
        {
            if (scalar.Value == "true") return true;
            if (scalar.Value == "false") return false;
        }

        errors.Add($"{At(node)}'{path}.{key}' must be true or false.");
        return null;
    }

    private static T ReadEnum<T>(
        YamlMappingNode mapping, string key, string path, List<string> errors, T fallback, Dictionary<string, T> values)
    {
        if (Child(mapping, key) is not { } node) return fallback;

        var text = (node as YamlScalarNode)?.Value;
        if (text is not null && values.TryGetValue(text, out var parsed)) return parsed;

        errors.Add($"{At(node)}'{path}.{key}' must be one of: {string.Join(", ", values.Keys)}.");
        return fallback;
    }

    /// <summary>The value for a key, or null when the mapping does not have it.</summary>
    private static YamlNode? Child(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    /// <summary>Where a node sits, so an error points at the line the operator has to edit.</summary>
    private static string At(YamlNode node) =>
        node.Start.Line > 0 ? $"Line {node.Start.Line}: " : "";

    private static string Describe(ICollection<string> groups) =>
        groups.Count == 0 ? "(none defined)" : string.Join(", ", groups.OrderBy(g => g, StringComparer.Ordinal));
}
