using System.Text.Json.Serialization;

namespace Echelon.Providers.GitLab.Webhooks;

// Every member is nullable on purpose. System.Text.Json does not enforce non-nullable reference
// types, so a payload missing "project" binds null into a non-nullable property and blows up as a
// 500 on first dereference. GitLabWebhookParser validates explicitly instead.
//
// Moved here from the ingress: this is GitLab's wire shape, and only GitLab's parser reads it, so it
// belongs with the provider that owns it rather than in the host that used to.

/// <summary>The GitLab merge-request webhook payload, only the fields this app reads.</summary>
internal record GitLabMrPayload(
    [property: JsonPropertyName("object_kind")] string? ObjectKind,
    [property: JsonPropertyName("project")] GitLabProject? Project,
    [property: JsonPropertyName("object_attributes")] GitLabMrAttributes? ObjectAttributes,
    [property: JsonPropertyName("labels")] IReadOnlyList<GitLabLabel>? Labels);

/// <summary>The project a merge request belongs to.</summary>
internal record GitLabProject(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path_with_namespace")] string? PathWithNamespace);

/// <summary>The merge request's attributes.</summary>
internal record GitLabMrAttributes(
    [property: JsonPropertyName("iid")] int? Iid,
    [property: JsonPropertyName("source_branch")] string? SourceBranch,
    [property: JsonPropertyName("target_branch")] string? TargetBranch,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("action")] string? Action,
    // GitLab populates labels at the top level and, for some events, here as well.
    [property: JsonPropertyName("labels")] IReadOnlyList<GitLabLabel>? Labels);

/// <summary>One label on a merge request.</summary>
internal record GitLabLabel(
    [property: JsonPropertyName("title")] string? Title);
