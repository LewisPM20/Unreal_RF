using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RenderFarm.Controller.Api;

/// <summary>
/// Optional UDP broadcaster that lets workers discover a LAN controller without hard-coding its IP.
/// </summary>
public sealed class ControllerDiscoveryOptions
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 39200;
    public int IntervalSeconds { get; set; } = 5;
    public string? ControllerUrl { get; set; }
}

public sealed class ControllerDiscoveryService(
    IOptions<ControllerDiscoveryOptions> options,
    ILogger<ControllerDiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var udp = new UdpClient { EnableBroadcast = true };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.IntervalSeconds, 2, 60)));
        var endpoint = new IPEndPoint(IPAddress.Broadcast, options.Value.Port);
        logger.LogInformation("Controller discovery broadcast enabled on UDP port {DiscoveryPort}", options.Value.Port);

        do
        {
            try
            {
                var payload = JsonSerializer.Serialize(new ControllerDiscoveryAnnouncement(
                    Service: "renderfarm-controller",
                    Url: ResolveControllerUrl(),
                    MachineName: Environment.MachineName));
                var bytes = Encoding.UTF8.GetBytes(payload);
                await udp.SendAsync(bytes, endpoint, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not broadcast controller discovery packet");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private string ResolveControllerUrl()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.ControllerUrl))
        {
            return options.Value.ControllerUrl.Trim().TrimEnd('/') + "/";
        }

        var address = GetPrimaryLanIpAddress() ?? "127.0.0.1";
        return $"http://{address}:9200/";
    }

    private static string? GetPrimaryLanIpAddress()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                ?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record ControllerDiscoveryAnnouncement(string Service, string Url, string MachineName);
}
