using System.Security.Cryptography;
using System.Text;
using ReleaseOrchestrator.Application.Exceptions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReleaseOrchestrator.Infrastructure.ReleasePlanning;

public static class YamlReleasePlanSerializer
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Serialize(YamlReleasePlanModel model)
        => Serializer.Serialize(new { release_plan = model });

    /// <summary>
    /// Parses a release plan document. Every malformed input surfaces as a domain
    /// validation error (HTTP 400) rather than an unhandled YamlException or
    /// KeyNotFoundException, which the API would report as a 500.
    /// </summary>
    public static YamlReleasePlanModel Deserialize(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new DomainValidationException("YAML document is empty.");

        Dictionary<string, YamlReleasePlanModel>? root;
        try
        {
            root = Deserializer.Deserialize<Dictionary<string, YamlReleasePlanModel>>(yaml);
        }
        catch (YamlException ex)
        {
            throw new DomainValidationException($"Malformed YAML: {ex.Message}");
        }

        if (root is null || !root.TryGetValue("release_plan", out var model) || model is null)
            throw new DomainValidationException("YAML document must have a top-level 'release_plan' key.");

        if (string.IsNullOrWhiteSpace(model.Name))
            throw new DomainValidationException("release_plan.name is required.");
        if (string.IsNullOrWhiteSpace(model.Version))
            throw new DomainValidationException("release_plan.version is required.");

        var duplicateSeq = model.Stages.GroupBy(s => s.Seq).FirstOrDefault(g => g.Count() > 1);
        if (duplicateSeq is not null)
            throw new DomainValidationException($"release_plan.stages has duplicate seq {duplicateSeq.Key}.");

        return model;
    }

    public static string ComputeHash(string yaml)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(yaml));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public class YamlReleasePlanModel
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Created { get; set; }
    public List<YamlStageModel> Stages { get; set; } = [];
    public List<YamlManualOverride>? ManualOverrides { get; set; }
}

public class YamlStageModel
{
    public int Seq { get; set; }
    public string? Name { get; set; }
    public List<YamlStageItemModel> Items { get; set; } = [];
}

public class YamlStageItemModel
{
    public string MrId { get; set; } = string.Empty;
    public string? Task { get; set; }
}

public class YamlManualOverride
{
    public string Type { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
