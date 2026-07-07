using RenderFarm.Domain;
using RenderFarm.Shared;
using Xunit;

namespace RenderFarm.Tests;

public sealed class RenderFarmVersionTests
{
    [Fact]
    public void CurrentWorkerVersionIsCompatible()
    {
        var agentVersion = RenderFarmVersion.FormatWorkerAgentVersion(
            RenderFarmVersion.ProductVersion,
            RenderFarmVersion.ProtocolVersion,
            RenderFarmVersion.ApiContractVersion,
            "test-build");

        var parsed = RenderFarmVersion.ParseWorkerAgentVersion(agentVersion);
        var compatibility = RenderFarmVersion.EvaluateWorkerAgent(agentVersion);

        Assert.Equal(RenderFarmVersion.ProductVersion, parsed.ProductVersion);
        Assert.Equal(RenderFarmVersion.ProtocolVersion, parsed.ProtocolVersion);
        Assert.Equal(RenderFarmVersion.ApiContractVersion, parsed.ApiContractVersion);
        Assert.Equal("test-build", parsed.BuildId);
        Assert.True(compatibility.Compatible);
    }

    [Fact]
    public void OldWorkerVersionIsIncompatible()
    {
        var compatibility = RenderFarmVersion.EvaluateWorkerAgent("0.12.0-csharp-takeover");

        Assert.False(compatibility.Compatible);
        Assert.Contains(RenderFarmVersion.MinimumWorkerProductVersion, compatibility.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeartbeatStoresStructuredVersionInAgentVersionField()
    {
        var heartbeat = new WorkerHeartbeatDto(
            "worker-1",
            "Worker",
            "host",
            "127.0.0.1",
            null,
            WorkerStatus.Idle.ToString(),
            null,
            null,
            null,
            EmptyCapabilities(),
            null,
            RenderFarmVersion.ProductVersion,
            RenderFarmVersion.ProtocolVersion,
            RenderFarmVersion.ApiContractVersion,
            "build-123");

        var worker = heartbeat.ToWorker(DateTimeOffset.UtcNow);
        var dto = worker.ToDto();

        Assert.Contains("protocol=3", worker.AgentVersion, StringComparison.OrdinalIgnoreCase);
        Assert.True(dto.Compatibility?.Compatible);
        Assert.Equal(RenderFarmVersion.ProductVersion, dto.ProductVersion);
        Assert.Equal(RenderFarmVersion.ProtocolVersion, dto.ProtocolVersion);
    }

    private static WorkerCapabilitiesDto EmptyCapabilities() => new(
        null,
        null,
        null,
        null,
        null,
        Array.Empty<UnrealEngineInstallationDto>(),
        Array.Empty<ProjectPathStatusDto>(),
        Array.Empty<SharedOutputStatusDto>());
}
