using ReleaseOrchestrator.Core.Enums;
using YamlDotNet.Serialization;

namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>
/// Writes <see cref="OrderingRules"/> back out as the YAML document
/// <see cref="OrderingRuleDocument"/> reads.
/// </summary>
/// <remarks>
/// <para>
/// Exists so the rules can be edited by clicking rather than typing. The visual editor does NOT
/// become a second source of truth: it builds a model, this renders the document, and the document is
/// what gets stored and what the planner reads. An editor that wrote to its own storage would put the
/// installation's deploy order in two places that could disagree — which is the failure the whole
/// document design exists to prevent.
/// </para>
/// <para>
/// Only what is set is written. An empty group axis, a default type, an unset wait policy: none of
/// them appear. A generated document should read like one a person would write, or nobody will edit
/// it by hand again — and hand editing has to stay possible, because the language expresses things
/// (nested excludes, deep glob patterns) no reasonable form ever will.
/// </para>
/// <para>
/// Assembled as nested collections and handed to the serializer rather than concatenated: group
/// names, globs and labels are operator text, and deciding when a string needs quoting in YAML is
/// exactly the job a serializer exists to do correctly.
/// </para>
/// </remarks>
public static class OrderingRuleWriter
{
    /// <summary>Renders a document.</summary>
    /// <param name="rules">The model to write.</param>
    public static string Write(OrderingRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var document = new Dictionary<string, object?> { ["version"] = rules.Version };

        if (WriteTasks(rules.Tasks) is { Count: > 0 } tasks) document["tasks"] = tasks;

        if (rules.Groups.Count > 0)
            document["groups"] = rules.Groups
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (object?)WriteSelector(g.Value), StringComparer.Ordinal);

        if (rules.Order.Count > 0)
            document["order"] = rules.Order.Select(WriteOrder).ToList();

        return new SerializerBuilder().WithIndentedSequences().Build().Serialize(document);
    }

    private static Dictionary<string, object?> WriteTasks(TaskPolicySpec tasks)
    {
        var mapping = new Dictionary<string, object?>();

        if (tasks.WaitForSubtasks is { } subtasks) mapping["wait_for_subtasks"] = subtasks;
        if (tasks.WaitForLinked is { } linked) mapping["wait_for_linked"] = linked;
        if (tasks.GroupOrder is { } order) mapping["group_order"] = GroupOrderName(order);

        if (tasks.Overrides.Count > 0)
            mapping["overrides"] = tasks.Overrides.Select(o =>
            {
                var entry = new Dictionary<string, object?> { ["match"] = WriteSelector(o.Match) };
                if (o.WaitForSubtasks is { } s) entry["wait_for_subtasks"] = s;
                if (o.WaitForLinked is { } l) entry["wait_for_linked"] = l;
                if (o.GroupOrder is { } g) entry["group_order"] = GroupOrderName(g);
                return entry;
            }).ToList();

        return mapping;
    }

    private static Dictionary<string, object?> WriteSelector(WorkSelector selector)
    {
        var mapping = new Dictionary<string, object?>();

        Add("connectors", selector.Connectors);
        Add("repositories", selector.Repositories);
        Add("branches", selector.Branches);
        Add("task_keys", selector.TaskKeys);
        Add("labels", selector.Labels);

        if (selector.Exclude is { } exclude) mapping["exclude"] = WriteSelector(exclude);

        return mapping;

        void Add(string key, IReadOnlyList<string> values)
        {
            if (values.Count > 0) mapping[key] = values.ToList();
        }
    }

    private static Dictionary<string, object?> WriteOrder(GroupOrderSpec spec)
    {
        var mapping = new Dictionary<string, object?>
        {
            ["group"] = spec.Group,
            ["needs"] = spec.Needs.ToList()
        };

        // Defaults are omitted, so a document says only what departs from them. Written explicitly
        // they would read as decisions somebody made, and the next reader would hesitate to change
        // them.
        if (spec.Type != StackDependencyType.Hard)
            mapping["type"] = spec.Type == StackDependencyType.Hard ? "hard" : "soft";

        if (spec.Scope != OrderScope.AcrossPlan)
            mapping["scope"] = "within_task";

        return mapping;
    }

    private static string GroupOrderName(PrerequisiteGroupOrder order) => order switch
    {
        PrerequisiteGroupOrder.SubtasksFirst => "subtasks_first",
        PrerequisiteGroupOrder.LinkedFirst => "linked_first",
        _ => "together"
    };
}
