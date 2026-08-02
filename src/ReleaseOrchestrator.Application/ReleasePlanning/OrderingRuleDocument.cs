using System.Text.Json;
using ReleaseOrchestrator.Core.Enums;

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
/// JSON today, YAML when the parser package can be installed. Not a compromise on the format: YAML
/// 1.2 is a superset of JSON, so every document this accepts is a valid YAML document with the same
/// keys and the same nesting. What is being approved is the structure and what it means, and both are
/// identical either way — only the punctuation changes.
/// </para>
/// <para>
/// Reads with <see cref="JsonDocument"/> rather than deserializing to the record directly, for the
/// error messages. Deserialization answers "could not convert" and points at a type; an operator
/// editing deploy order needs to be told which key, which group and what the valid values were. It
/// also lets an unknown key be an ERROR: a mistyped <c>repositores:</c> that silently selects nothing
/// is precisely the failure this document must not have.
/// </para>
/// <para>
/// Every error is collected rather than thrown at the first one. A rule file is edited in bulk, and
/// being told about one mistake per round trip is how a five-minute edit becomes an afternoon.
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

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return new OrderingRuleParseResult(null, [$"The document is not valid JSON: {ex.Message}"]);
        }

        using (document)
        {
            var errors = new List<string>();
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return new OrderingRuleParseResult(null, ["The document must be an object."]);

            RejectUnknownKeys(root, [VersionKey, TasksKey, GroupsKey, OrderKey], "document", errors);

            var version = ReadVersion(root, errors);
            var groups = ReadGroups(root, errors);
            var order = ReadOrder(root, groups.Keys, errors);
            var tasks = ReadTasks(root, errors);

            var rules = new OrderingRules(version, tasks, groups, order);
            return new OrderingRuleParseResult(errors.Count == 0 ? rules : null, errors);
        }
    }

    private static int ReadVersion(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty(VersionKey, out var element))
        {
            errors.Add("'version' is required. The only version is 1.");
            return 1;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var version))
        {
            errors.Add("'version' must be a number. The only version is 1.");
            return 1;
        }

        // Refused rather than assumed: a document written against a future schema would otherwise be
        // read with today's rules and silently mean something else.
        if (version != 1) errors.Add($"Unsupported version {version}. The only version is 1.");

        return version;
    }

    private static Dictionary<string, WorkSelector> ReadGroups(JsonElement root, List<string> errors)
    {
        var groups = new Dictionary<string, WorkSelector>(StringComparer.Ordinal);

        if (!root.TryGetProperty(GroupsKey, out var element)) return groups;

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'groups' must be an object mapping a group name to a selector.");
            return groups;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                errors.Add("A group name cannot be blank.");
                continue;
            }

            var selector = ReadSelector(property.Value, $"groups.{property.Name}", errors);
            if (selector is not null) groups[property.Name] = selector;
        }

        return groups;
    }

    private static WorkSelector? ReadSelector(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"'{path}' must be an object.");
            return null;
        }

        RejectUnknownKeys(
            element, ["connectors", "repositories", "branches", "task_keys", "labels", "exclude"], path, errors);

        WorkSelector? exclude = null;
        if (element.TryGetProperty("exclude", out var excludeElement))
            exclude = ReadSelector(excludeElement, $"{path}.exclude", errors);

        return new WorkSelector(
            ReadStrings(element, "connectors", path, errors),
            ReadStrings(element, "repositories", path, errors),
            ReadStrings(element, "branches", path, errors),
            ReadStrings(element, "task_keys", path, errors),
            ReadStrings(element, "labels", path, errors),
            exclude);
    }

    private static List<GroupOrderSpec> ReadOrder(
        JsonElement root, ICollection<string> knownGroups, List<string> errors)
    {
        var order = new List<GroupOrderSpec>();

        if (!root.TryGetProperty(OrderKey, out var element)) return order;

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add("'order' must be an array of ordering rules.");
            return order;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"order[{index++}]";

            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"'{path}' must be an object.");
                continue;
            }

            RejectUnknownKeys(item, ["group", "needs", "type", "scope"], path, errors);

            var group = ReadString(item, "group", path, errors);
            var needs = ReadStrings(item, "needs", path, errors);

            if (group is null) continue;

            // A group nothing has defined is a typo, and the fix is knowing what was available.
            if (!knownGroups.Contains(group))
                errors.Add($"'{path}.group' names an undefined group '{group}'. Defined: {Describe(knownGroups)}.");

            // A rule that needs nothing orders nothing, so it is a mistake rather than a no-op.
            if (needs.Count == 0)
                errors.Add($"'{path}.needs' must name at least one group; a rule that needs nothing orders nothing.");

            foreach (var need in needs)
            {
                if (!knownGroups.Contains(need))
                    errors.Add($"'{path}.needs' names an undefined group '{need}'. Defined: {Describe(knownGroups)}.");

                // Would be a self-edge for every member, which carries no ordering at all.
                if (string.Equals(need, group, StringComparison.Ordinal))
                    errors.Add($"'{path}' has group '{group}' needing itself.");
            }

            var type = ReadEnum(item, "type", path, errors, StackDependencyType.Hard, new()
            {
                ["hard"] = StackDependencyType.Hard,
                ["soft"] = StackDependencyType.Soft
            });

            var scope = ReadEnum(item, "scope", path, errors, OrderScope.AcrossPlan, new()
            {
                ["across_plan"] = OrderScope.AcrossPlan,
                ["within_task"] = OrderScope.WithinTask
            });

            order.Add(new GroupOrderSpec(group, needs, type, scope));
        }

        return order;
    }

    private static TaskPolicySpec ReadTasks(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty(TasksKey, out var element))
            return new TaskPolicySpec(null, null, null, []);

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'tasks' must be an object.");
            return new TaskPolicySpec(null, null, null, []);
        }

        RejectUnknownKeys(
            element, ["wait_for_subtasks", "wait_for_linked", "group_order", "overrides"], TasksKey, errors);

        var overrides = new List<TaskPolicyOverride>();
        if (element.TryGetProperty("overrides", out var overridesElement))
        {
            if (overridesElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add("'tasks.overrides' must be an array.");
            }
            else
            {
                var index = 0;
                foreach (var item in overridesElement.EnumerateArray())
                {
                    var path = $"tasks.overrides[{index++}]";

                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"'{path}' must be an object.");
                        continue;
                    }

                    RejectUnknownKeys(
                        item, ["match", "wait_for_subtasks", "wait_for_linked", "group_order"], path, errors);

                    if (!item.TryGetProperty("match", out var matchElement))
                    {
                        errors.Add($"'{path}.match' is required; without it the override would apply to every task.");
                        continue;
                    }

                    var match = ReadSelector(matchElement, $"{path}.match", errors);
                    if (match is null) continue;

                    overrides.Add(new TaskPolicyOverride(
                        match,
                        ReadBool(item, "wait_for_subtasks", path, errors),
                        ReadBool(item, "wait_for_linked", path, errors),
                        ReadGroupOrder(item, path, errors)));
                }
            }
        }

        return new TaskPolicySpec(
            ReadBool(element, "wait_for_subtasks", TasksKey, errors),
            ReadBool(element, "wait_for_linked", TasksKey, errors),
            ReadGroupOrder(element, TasksKey, errors),
            overrides);
    }

    private static PrerequisiteGroupOrder? ReadGroupOrder(JsonElement element, string path, List<string> errors)
    {
        if (!element.TryGetProperty("group_order", out _)) return null;

        return ReadEnum<PrerequisiteGroupOrder?>(element, "group_order", path, errors, null, new()
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
        JsonElement element, string[] known, string path, List<string> errors)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (known.Contains(property.Name, StringComparer.Ordinal)) continue;

            errors.Add($"'{path}' has unknown key '{property.Name}'. Valid keys: {string.Join(", ", known)}.");
        }
    }

    private static string? ReadString(JsonElement element, string key, string path, List<string> errors)
    {
        if (!element.TryGetProperty(key, out var value))
        {
            errors.Add($"'{path}.{key}' is required.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            errors.Add($"'{path}.{key}' must be a non-empty string.");
            return null;
        }

        return value.GetString();
    }

    private static List<string> ReadStrings(JsonElement element, string key, string path, List<string> errors)
    {
        var values = new List<string>();

        if (!element.TryGetProperty(key, out var array)) return values;

        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"'{path}.{key}' must be an array of strings.");
            return values;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                errors.Add($"'{path}.{key}' must contain only non-empty strings.");
                continue;
            }

            values.Add(item.GetString()!);
        }

        return values;
    }

    private static bool? ReadBool(JsonElement element, string key, string path, List<string> errors)
    {
        if (!element.TryGetProperty(key, out var value)) return null;

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();

        errors.Add($"'{path}.{key}' must be true or false.");
        return null;
    }

    private static T ReadEnum<T>(
        JsonElement element, string key, string path, List<string> errors, T fallback, Dictionary<string, T> values)
    {
        if (!element.TryGetProperty(key, out var value)) return fallback;

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        if (text is not null && values.TryGetValue(text, out var parsed)) return parsed;

        errors.Add($"'{path}.{key}' must be one of: {string.Join(", ", values.Keys)}.");
        return fallback;
    }

    private static string Describe(ICollection<string> groups) =>
        groups.Count == 0 ? "(none defined)" : string.Join(", ", groups.OrderBy(g => g, StringComparer.Ordinal));
}
