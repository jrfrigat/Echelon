using System.Text.Json.Serialization;

namespace ReleaseOrchestrator.Ingress.Webhooks.Models;

public record GitLabMrPayload(
    [property: JsonPropertyName("object_kind")] string ObjectKind,
    [property: JsonPropertyName("project")] GitLabProject Project,
    [property: JsonPropertyName("object_attributes")] GitLabMrAttributes ObjectAttributes);

public record GitLabProject(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path_with_namespace")] string PathWithNamespace);

public record GitLabMrAttributes(
    [property: JsonPropertyName("iid")] int Iid,
    [property: JsonPropertyName("source_branch")] string SourceBranch,
    [property: JsonPropertyName("target_branch")] string TargetBranch,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("title")] string Title);
