using System.Security.Cryptography;
using System.Text;
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

    public static YamlReleasePlanModel Deserialize(string yaml)
    {
        var root = Deserializer.Deserialize<Dictionary<string, YamlReleasePlanModel>>(yaml);
        return root["release_plan"];
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
