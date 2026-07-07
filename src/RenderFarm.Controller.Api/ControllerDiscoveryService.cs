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
            logger.LogDebug("Controller discovery broadcast is disabled.");
            return;
        }

        var advertisedUrl = ResolveControllerUrl(options.Value.ControllerUrl, logger);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.IntervalSeconds, 2, 60)));
        var endpoint = new IPEndPoint(IPAddress.Broadcast, options.Value.Port);
        logger.LogInformation(
            "Controller discovery broadcast enabled. Machine {MachineName}; URL {ControllerUrl}; UDP port {DiscoveryPort}",
            Environment.MachineName,
            advertisedUrl,
            options.Value.Port);

        do
        {
            try
            {
                var payload = JsonSerializer.Serialize(new ControllerDiscoveryAnnouncement(
                    Service: "renderfarm-controller",
                    Url: advertisedUrl,
                    MachineName: Environment.MachineName));
                var bytes = Encoding.UTF8.GetBytes(payload);
                await udp.SendAsync(bytes, endpoint, stoppingToken);
                logger.LogDebug("Broadcasted RenderFarm controller discovery announcement for {ControllerUrl} on UDP {DiscoveryPort}", advertisedUrl, options.Value.Port);
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Could not broadcast controller discovery packet on UDP {DiscoveryPort}. Check Windows Firewall, UDP broadcast policy, VPNs, or WiFi/AP client isolation.", options.Value.Port);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not broadcast controller discovery packet for {ControllerUrl}", advertisedUrl);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static string ResolveControllerUrl(string? configuredUrl, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl) && Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var configured))
        {
            if (!IsLoopbackOrWildcard(configured.Host))
            {
                return configured.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? configured.AbsoluteUri : configured.AbsoluteUri + "/";
            }

            logger?.LogWarning("Configured discovery URL {ControllerUrl} is not LAN-reachable; resolving a LAN IP for advertisement instead.", configuredUrl);
        }

        var address = GetPrimaryLanIpAddress() ?? Dns.GetHostName();
        return $"http://{address}:9200/";
    }

    public static bool IsLoopbackOrWildcard(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "+", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && (IPAddress.IsLoopback(address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address));
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


