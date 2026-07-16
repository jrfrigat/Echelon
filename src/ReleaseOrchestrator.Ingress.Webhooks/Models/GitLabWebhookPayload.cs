using System.Text.Json.Serialization;

namespace ReleaseOrchestrator.Ingress.Webhooks.Models;

// Every member is nullable on purpose. System.Text.Json does not enforce non-nullable
// reference types, so a payload missing "project" binds null into a non-nullable property
// and blows up as a 500 on first dereference. The endpoint validates explicitly instead.
public record GitLabMrPayload(
    [property: JsonPropertyName("object_kind")] string? ObjectKind,
    [property: JsonPropertyName("project")] GitLabProject? Project,
    [property: JsonPropertyName("object_attributes")] GitLabMrAttributes? ObjectAttributes,
    [property: JsonPropertyName("labels")] IReadOnlyList<GitLabLabel>? Labels);

public record GitLabProject(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path_with_namespace")] string? PathWithNamespace);

public record GitLabMrAttributes(
    [property: JsonPropertyName("iid")] int? Iid,
    [property: JsonPropertyName("source_branch")] string? SourceBranch,
    [property: JsonPropertyName("target_branch")] string? TargetBranch,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("action")] string? Action,
    // GitLab populates labels at the top level and, for some events, here as well.
    [property: JsonPropertyName("labels")] IReadOnlyList<GitLabLabel>? Labels);

public record GitLabLabel(
    [property: JsonPropertyName("title")] string? Title);
