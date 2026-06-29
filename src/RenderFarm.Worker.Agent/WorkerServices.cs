using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using RenderFarm.Domain;
using RenderFarm.Shared;

namespace RenderFarm.Worker.Agent;

public interface IWorkerIdentityProvider
{
    string GetWorkerId();
    string GetDisplayName();
    string GetHostname();
    string? GetPrimaryIpAddress();
    IReadOnlyList<string> GetLanIpAddresses();
}

public sealed class WorkerIdentityProvider(Microsoft.Extensions.Options.IOptions<WorkerAgentOptions> options) : IWorkerIdentityProvider
{
    private readonly Lazy<string> _workerId = new(() => ResolveWorkerId(options.Value));

    public string GetWorkerId() => _workerId.Value;

    public string GetDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.DisplayName))
        {
            return options.Value.DisplayName.Trim();
        }

        var hostname = GetHostname();
        var ip = GetPrimaryIpAddress();
        return string.IsNullOrWhiteSpace(ip) ? hostname : $"{hostname} ({ip})";
    }

    public string GetHostname() => Dns.GetHostName();

    public string? GetPrimaryIpAddress() => GetLanIpAddresses().FirstOrDefault();

    public IReadOnlyList<string> GetLanIpAddresses()
    {
        try
        {
            return Dns.GetHostEntry(GetHostname())
                .AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string ResolveWorkerId(WorkerAgentOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkerId))
        {
            return options.WorkerId.Trim();
        }

        var identityPath = ResolveIdentityPath(options.IdentityFilePath);
        try
        {
            if (File.Exists(identityPath))
            {
                var existing = File.ReadAllText(identityPath).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }
        }
        catch (Exception)
        {
            // A generated fallback is still better than crashing a render worker at startup.
        }

        var generated = $"worker-{Sanitize(Environment.MachineName)}-{Guid.NewGuid().ToString("N")[..12]}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(identityPath)!);
            File.WriteAllText(identityPath, generated);
        }
        catch (Exception)
        {
            // Keep running even if the identity store is not writable.
        }

        return generated;
    }

    private static string ResolveIdentityPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "RenderFarm")
            : Path.Combine(localAppData, "RenderFarm");
        return Path.Combine(root, "worker.identity");
    }

    private static string Sanitize(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var sanitized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}

public interface IControllerEndpointProvider
{
    ValueTask<Uri> GetControllerBaseUriAsync(CancellationToken cancellationToken);
}

public sealed class ControllerEndpointProvider(
    Microsoft.Extensions.Options.IOptions<WorkerAgentOptions> options,
    ILogger<ControllerEndpointProvider> logger) : IControllerEndpointProvider
{
    private static readonly JsonSerializerOptions DiscoveryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private Uri? _discoveredController;

    public async ValueTask<Uri> GetControllerBaseUriAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.ControllerUrl))
        {
            return EnsureTrailingSlash(new Uri(options.Value.ControllerUrl.Trim(), UriKind.Absolute));
        }

        if (_discoveredController is not null)
        {
            return _discoveredController;
        }

        if (options.Value.DiscoveryEnabled && await TryDiscoverControllerAsync(cancellationToken) is { } discovered)
        {
            _discoveredController = discovered;
            return discovered;
        }

        return EnsureTrailingSlash(new Uri("http://127.0.0.1:9200", UriKind.Absolute));
    }

    private async Task<Uri?> TryDiscoverControllerAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.DiscoverySeconds, 1, 30)));

        try
        {
            using var udp = new UdpClient(options.Value.DiscoveryPort) { EnableBroadcast = true };
            while (!timeout.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                var payload = System.Text.Encoding.UTF8.GetString(result.Buffer);
                var announcement = JsonSerializer.Deserialize<ControllerDiscoveryAnnouncement>(payload, DiscoveryJsonOptions);
                if (announcement is null || !string.Equals(announcement.Service, "renderfarm-controller", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Uri.TryCreate(announcement.Url, UriKind.Absolute, out var uri))
                {
                    logger.LogInformation("Discovered RenderFarm controller at {ControllerUrl}", uri);
                    return EnsureTrailingSlash(uri);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("No RenderFarm controller discovery packet was received; falling back to localhost.");
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "Controller discovery could not listen on UDP port {DiscoveryPort}", options.Value.DiscoveryPort);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Ignored malformed controller discovery packet");
        }

        return null;
    }

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");

    private sealed record ControllerDiscoveryAnnouncement(string Service, string Url, string? MachineName);
}

public interface IWorkerCapabilityDetector
{
    Task<WorkerCapabilities> DetectAsync(CancellationToken cancellationToken);
}

public sealed class WorkerCapabilityDetector(
    Microsoft.Extensions.Options.IOptions<WorkerAgentOptions> options,
    IUnrealEngineLocator unrealEngineLocator,
    ISharedOutputValidator sharedOutputValidator) : IWorkerCapabilityDetector
{
    public async Task<WorkerCapabilities> DetectAsync(CancellationToken cancellationToken)
    {
        var projectPaths = options.Value.ProjectPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ProjectPathStatus(path, Directory.Exists(path) || File.Exists(path)))
            .ToArray();

        var outputRoots = new List<SharedOutputStatus>();
        foreach (var root in options.Value.SharedOutputRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = await sharedOutputValidator.ValidateAsync(new SharedOutputValidationRequest(root, "_rf_probe", true), cancellationToken);
            outputRoots.Add(new SharedOutputStatus(root, Directory.Exists(root), result.Ok, TryGetFreeDiskGb(root), result.Message));
        }

        return new WorkerCapabilities(
            CpuCores: Environment.ProcessorCount,
            RamGb: null,
            GpuName: null,
            VramGb: null,
            FreeDiskGb: TryGetFreeDiskGb(AppContext.BaseDirectory),
            UnrealInstallations: unrealEngineLocator.FindInstallations(options.Value.UnrealSearchRoots).ToArray(),
            ProjectPaths: projectPaths,
            SharedOutputRoots: outputRoots);
    }

    private static double? TryGetFreeDiskGb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) ? null : Math.Round(new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d, 2);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public interface ISharedOutputValidator
{
    Task<SharedOutputValidationResult> ValidateAsync(SharedOutputValidationRequest request, CancellationToken cancellationToken);
}

public sealed class SharedOutputValidator : ISharedOutputValidator
{
    public async Task<SharedOutputValidationResult> ValidateAsync(SharedOutputValidationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SharedOutputRoot))
        {
            return new(false, FailureCategory.SharedOutputUnreachable, "Shared output root is empty.");
        }

        try
        {
            Directory.CreateDirectory(request.SharedOutputRoot);
            var fullRoot = Path.GetFullPath(request.SharedOutputRoot);
            var jobDirectory = Path.GetFullPath(Path.Combine(fullRoot, request.JobOutputDirectory));
            if (!jobDirectory.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, FailureCategory.CommandValidationFailed, "Job output directory escapes the shared output root.");
            }

            Directory.CreateDirectory(jobDirectory);
            if (request.MinFreeGb is { } minFreeGb)
            {
                var drive = new DriveInfo(Path.GetPathRoot(jobDirectory)!);
                var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                if (freeGb < minFreeGb)
                {
                    return new(false, FailureCategory.SharedOutputUnreachable, $"Shared output root has {freeGb:0.##} GB free, below required {minFreeGb:0.##} GB.", jobDirectory);
                }
            }

            if (request.CreateTestFile)
            {
                var probe = Path.Combine(jobDirectory, $".rf_write_probe_{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(probe, "renderfarm-write-probe", cancellationToken);
                File.Delete(probe);
            }

            return new(true, FailureCategory.None, "Shared output path is reachable and writable.", jobDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(false, FailureCategory.SharedOutputNotWritable, ex.Message);
        }
        catch (IOException ex)
        {
            return new(false, FailureCategory.SharedOutputUnreachable, ex.Message);
        }
        catch (Exception ex)
        {
            return new(false, FailureCategory.Unknown, ex.Message);
        }
    }
}

public sealed class WorkerHeartbeatService(
    IHttpClientFactory httpClientFactory,
    Microsoft.Extensions.Options.IOptions<WorkerAgentOptions> options,
    IWorkerIdentityProvider identity,
    IWorkerCapabilityDetector capabilities,
    IControllerEndpointProvider controllerEndpoints,
    IWorkerExecutionStateStore executionState,
    ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, options.Value.HeartbeatSeconds)));
        do
        {
            await SendHeartbeatAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var detectedCapabilities = await capabilities.DetectAsync(cancellationToken);
            var currentExecution = await executionState.ReadAsync(cancellationToken);
            var heartbeat = new WorkerHeartbeatDto(
                WorkerId: identity.GetWorkerId(),
                Name: identity.GetDisplayName(),
                Hostname: identity.GetHostname(),
                Ip: identity.GetPrimaryIpAddress(),
                ServiceUrl: options.Value.ServiceUrl,
                Status: (currentExecution is null ? WorkerStatus.Idle : WorkerStatus.Busy).ToString(),
                Stage: currentExecution is null ? null : "rendering",
                CurrentJobId: currentExecution?.JobId,
                AgentVersion: "0.12.0-csharp-takeover",
                Capabilities: detectedCapabilities.ToDto(),
                LastError: null);

            var client = httpClientFactory.CreateClient();
            WorkerHttp.ApplyControllerToken(client, options.Value.ApiToken);
            var endpoint = new Uri(await controllerEndpoints.GetControllerBaseUriAsync(cancellationToken), "api/workers/heartbeat");
            using var response = await client.PostAsJsonAsync(endpoint, heartbeat, RenderFarmJson.SerializerOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send heartbeat to controller");
        }
    }
}
