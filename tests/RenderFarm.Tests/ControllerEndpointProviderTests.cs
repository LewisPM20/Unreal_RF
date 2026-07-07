using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RenderFarm.Worker.Agent;
using Xunit;

namespace RenderFarm.Tests;

public sealed class ControllerEndpointProviderTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CONTROLLER_IP")]
    [InlineData("http://CONTROLLER_IP:9200")]
    [InlineData("https://CONTROLLER_IP:9200")]
    [InlineData("http://controller_ip:9200/")]
    public void PlaceholderControllerUrlsAreIgnored(string value)
    {
        Assert.True(ControllerEndpointProvider.IsPlaceholderControllerUrl(value));
    }

    [Fact]
    public async Task ManualControllerUrlHasPriorityOverDiscovery()
    {
        var provider = CreateProvider(new WorkerAgentOptions
        {
            ControllerUrl = "http://192.168.1.50:9200",
            DiscoveryEnabled = true,
            DiscoverySeconds = 1
        });

        var uri = await provider.GetControllerBaseUriAsync(CancellationToken.None);

        Assert.Equal("http://192.168.1.50:9200/", uri.AbsoluteUri);
    }

    [Fact]
    public async Task PlaceholderWithDiscoveryDisabledFallsBackToLocalhost()
    {
        var provider = CreateProvider(new WorkerAgentOptions
        {
            ControllerUrl = "http://CONTROLLER_IP:9200",
            DiscoveryEnabled = false
        });

        var uri = await provider.GetControllerBaseUriAsync(CancellationToken.None);

        Assert.Equal("http://127.0.0.1:9200/", uri.AbsoluteUri);
    }

    [Fact]
    public async Task BlankWithDiscoveryDisabledFallsBackToLocalhost()
    {
        var provider = CreateProvider(new WorkerAgentOptions
        {
            ControllerUrl = "",
            DiscoveryEnabled = false
        });

        var uri = await provider.GetControllerBaseUriAsync(CancellationToken.None);

        Assert.Equal("http://127.0.0.1:9200/", uri.AbsoluteUri);
    }

    [Fact]
    public void WorkerResolutionPlanOrdersFallbacksSafely()
    {
        var options = new WorkerAgentOptions
        {
            ControllerUrl = "",
            DiscoveryEnabled = true,
            LanScanEnabled = true
        };

        var plan = ControllerEndpointProvider.GetResolutionPlan(options, hasLastKnownController: true);

        Assert.Equal(new[] { "last-known", "udp-discovery", "lan-scan", "localhost" }, plan);
    }

    [Fact]
    public void WorkerResolutionPlanKeepsManualUrlFirst()
    {
        var options = new WorkerAgentOptions
        {
            ControllerUrl = "http://192.168.1.50:9200",
            DiscoveryEnabled = true,
            LanScanEnabled = true
        };

        var plan = ControllerEndpointProvider.GetResolutionPlan(options, hasLastKnownController: true);

        Assert.Equal(new[] { "manual", "last-known", "udp-discovery", "lan-scan", "localhost" }, plan);
    }

    [Fact]
    public void LanScanCandidatesStayWithinPrivateSubnetAndLimit()
    {
        var candidates = ControllerEndpointProvider.BuildPrivateLanScanCandidates(
            IPAddress.Parse("192.168.1.20"),
            IPAddress.Parse("255.255.255.0"),
            maxHosts: 8);

        Assert.Equal(8, candidates.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.20"), candidates);
        Assert.All(candidates, address => Assert.StartsWith("192.168.1.", address.ToString()));
    }
    private static ControllerEndpointProvider CreateProvider(WorkerAgentOptions options) =>
        new(Options.Create(options), NullLogger<ControllerEndpointProvider>.Instance);
}

