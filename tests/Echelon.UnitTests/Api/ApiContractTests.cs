using System.Text.Json;
using System.Text.Json.Serialization;
using Echelon.Application.DTOs;
using Echelon.Pwa.Services;
using Xunit;

namespace Echelon.UnitTests.Api;

/// <summary>
/// What the API writes is what the admin UI reads.
/// </summary>
/// <remarks>
/// The UI used to declare its own copy of every response type. One copy drifted - the plan version
/// became an <c>int</c> on the server and stayed a <c>string</c> in the browser - and the first plan
/// ever fetched died with "DeserializeUnableToConvertValue ... Path: $.version", with the API
/// answering perfectly well. The copies are gone (the client references the server's DTOs), and these
/// tests hold the remaining half of the contract: the two ends must still agree on how those types
/// are written and read, which is a matter of serializer settings rather than of shape.
/// </remarks>
public class ApiContractTests
{
    /// <summary>The API's settings, as Program.cs configures MVC.</summary>
    private static readonly JsonSerializerOptions ServerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void ThePlanTheServerWritesIsThePlanTheClientReads()
    {
        var plan = new RolloutPlanDto(
            Id: Guid.NewGuid(),
            TargetTaskId: Guid.NewGuid(),
            TargetTaskKey: "ECH-1",
            Version: 7,
            Source: "Generated",
            Status: "Ready",
            IsActive: true,
            CreatedAt: new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt: new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
            Nodes:
            [
                new PlanTaskNodeDto(
                    TaskId: Guid.NewGuid(),
                    TaskKey: "ECH-1",
                    TaskTitle: "Parent",
                    IsTarget: true,
                    DependsOnTaskIds: [],
                    Items:
                    [
                        new PlanItemDto(
                            MergeRequestId: Guid.NewGuid(),
                            MrExternalId: "42",
                            RepositoryName: "api",
                            SourceBranch: "feature/ECH-1",
                            TargetBranch: "main",
                            MrStatus: "ReadyForDeploy",
                            Wave: 1,
                            ManuallyIncluded: false)
                    ])
            ],
            Waves: [new PlanWaveDto(1, [Guid.NewGuid()])],
            Conflicts: []);

        var json = JsonSerializer.Serialize(plan, ServerOptions);
        var read = JsonSerializer.Deserialize<RolloutPlanDto>(json, ApiService.Json);

        Assert.NotNull(read);
        Assert.Equal(plan.Version, read.Version);
        Assert.Equal(plan.TargetTaskKey, read.TargetTaskKey);
        Assert.Equal(plan.Nodes[0].Items[0].Wave, read.Nodes[0].Items[0].Wave);
        Assert.Equal(plan.Waves[0].MergeRequestIds[0], read.Waves[0].MergeRequestIds[0]);
    }

    [Fact]
    public void EnumsTravelAsNamesInBothDirections()
    {
        // The server writes enum names; the client has to be reading them the same way, or every
        // enum-typed field fails at the first response that carries one.
        var json = JsonSerializer.Serialize(new Holder(Echelon.Core.Enums.IngestionMode.Poll), ServerOptions);

        Assert.Contains("\"Poll\"", json, StringComparison.Ordinal);
        Assert.Equal(
            Echelon.Core.Enums.IngestionMode.Poll,
            JsonSerializer.Deserialize<Holder>(json, ApiService.Json)!.Mode);
    }

    private sealed record Holder(Echelon.Core.Enums.IngestionMode Mode);
}
