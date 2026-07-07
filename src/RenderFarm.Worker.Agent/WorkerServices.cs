using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    private Uri? _resolvedController;

    public async ValueTask<Uri> GetControllerBaseUriAsync(CancellationToken cancellationToken)
    {
        var configuredUrl = options.Value.ControllerUrl;
        if (!string.IsNullOrWhiteSpace(configuredUrl) && IsPlaceholderControllerUrl(configuredUrl))
        {
            logger.LogWarning("Ignoring placeholder ControllerUrl '{ControllerUrl}'. Leave ControllerUrl blank for discovery, or enter a real URL such as http://<controller-lan-ip>:9200/.", configuredUrl);
            configuredUrl = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            var manual = EnsureTrailingSlash(new Uri(configuredUrl.Trim(), UriKind.Absolute));
            logger.LogInformation("Using manual RenderFarm controller URL {ControllerUrl}; LAN discovery and LAN scan are skipped.", manual);
            SaveLastKnownControllerUrl(manual);
            return manual;
        }

        if (_resolvedController is not null && await IsControllerReachableAsync(_resolvedController, TimeSpan.FromSeconds(1), cancellationToken))
        {
            logger.LogDebug("Using cached reachable RenderFarm controller URL {ControllerUrl}.", _resolvedController);
            return _resolvedController;
        }

        if (TryReadLastKnownControllerUrl() is { } lastKnown)
        {
            logger.LogInformation("Trying last known RenderFarm controller URL {ControllerUrl} before discovery.", lastKnown);
            if (await IsControllerReachableAsync(lastKnown, TimeSpan.FromSeconds(1), cancellationToken))
            {
                _resolvedController = lastKnown;
                logger.LogInformation("Last known RenderFarm controller URL is reachable: {ControllerUrl}", lastKnown);
                return lastKnown;
            }

            logger.LogWarning("Last known RenderFarm controller URL {ControllerUrl} is not reachable; continuing with discovery.", lastKnown);
        }

        if (options.Value.DiscoveryEnabled)
        {
            logger.LogInformation("ControllerUrl is blank/placeholder and LAN discovery is enabled; listening for controller announcements on UDP {DiscoveryPort} for up to {DiscoverySeconds} second(s).", options.Value.DiscoveryPort, options.Value.DiscoverySeconds);
            if (await TryDiscoverControllerAsync(cancellationToken) is { } discovered)
            {
                _resolvedController = discovered;
                SaveLastKnownControllerUrl(discovered);
                return discovered;
            }
        }
        else
        {
            logger.LogInformation("ControllerUrl is blank/placeholder and LAN discovery is disabled.");
        }

        if (options.Value.LanScanEnabled)
        {
            logger.LogInformation("Attempting bounded private LAN scan for RenderFarm controller on TCP port {ControllerPort}; max hosts {MaxHosts}; timeout {TimeoutSeconds}s.", options.Value.ControllerPort, options.Value.LanScanMaxHosts, options.Value.LanScanTimeoutSeconds);
            if (await TryScanLanForControllerAsync(cancellationToken) is { } scanned)
            {
                _resolvedController = scanned;
                SaveLastKnownControllerUrl(scanned);
                return scanned;
            }
        }
        else
        {
            logger.LogInformation("LAN scan is disabled; skipping subnet probe.");
        }

        var fallback = EnsureTrailingSlash(new Uri("http://127.0.0.1:9200", UriKind.Absolute));
        logger.LogWarning("No manual, last known, UDP discovery, or LAN scan controller URL was usable. Falling back to localhost {ControllerUrl}; remote workers will not connect unless the controller is local.", fallback);
        return fallback;
    }

    public static bool IsPlaceholderControllerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (string.Equals(trimmed, "CONTROLLER_IP", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return string.Equals(uri.Host, "CONTROLLER_IP", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static IReadOnlyList<string> GetResolutionPlan(WorkerAgentOptions options, bool hasLastKnownController)
    {
        var steps = new List<string>();
        if (!IsPlaceholderControllerUrl(options.ControllerUrl)) steps.Add("manual");
        if (hasLastKnownController) steps.Add("last-known");
        if (options.DiscoveryEnabled) steps.Add("udp-discovery");
        if (options.LanScanEnabled) steps.Add("lan-scan");
        steps.Add("localhost");
        return steps;
    }

    public static IReadOnlyList<IPAddress> BuildPrivateLanScanCandidates(IPAddress localAddress, IPAddress? mask, int maxHosts)
    {
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || !IsPrivateLanAddress(localAddress))
        {
            return Array.Empty<IPAddress>();
        }

        var hostLimit = Math.Clamp(maxHosts, 1, 4096);
        var local = ToUInt32(localAddress);
        var maskValue = mask is null || mask.Equals(IPAddress.Any) ? 0xFFFFFF00u : ToUInt32(mask);
        var network = local & maskValue;
        var broadcast = network | ~maskValue;
        if (broadcast - network > (uint)hostLimit + 1u)
        {
            maskValue = 0xFFFFFF00u;
            network = local & maskValue;
            broadcast = network | ~maskValue;
        }

        var addresses = new List<IPAddress>();
        for (var value = network + 1; value < broadcast && addresses.Count < hostLimit; value++)
        {
            if (value == local) continue;
            addresses.Add(FromUInt32(value));
        }

        return addresses;
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

                if (Uri.TryCreate(announcement.Url, UriKind.Absolute, out var uri) && IsUsableRemoteControllerUri(uri))
                {
                    var controller = EnsureTrailingSlash(uri);
                    logger.LogInformation("Discovered RenderFarm controller at {ControllerUrl} from {RemoteEndpoint}", controller, result.RemoteEndPoint);
                    return controller;
                }

                logger.LogWarning("Ignored unusable controller discovery URL {ControllerUrl} from {RemoteEndpoint}; discovery must not advertise 127.0.0.1 or 0.0.0.0.", announcement.Url, result.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("No RenderFarm controller discovery packet was received; trying LAN scan next if enabled.");
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "Controller discovery could not listen on UDP port {DiscoveryPort}. Check Windows Firewall, another worker using the port, or endpoint protection.", options.Value.DiscoveryPort);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Ignored malformed controller discovery packet");
        }

        return null;
    }

    private async Task<Uri?> TryScanLanForControllerAsync(CancellationToken cancellationToken)
    {
        var candidates = GetLanScanCandidates().Take(Math.Clamp(options.Value.LanScanMaxHosts, 1, 4096)).Distinct().ToArray();
        if (candidates.Length == 0)
        {
            logger.LogInformation("LAN scan skipped because no private IPv4 LAN adapters were available.");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.LanScanTimeoutSeconds, 1, 30)));
        using var semaphore = new SemaphoreSlim(64);
        var found = new ConcurrentQueue<Uri>();
        var tasks = candidates.Select(async address =>
        {
            await semaphore.WaitAsync(timeout.Token);
            try
            {
                if (!found.IsEmpty) return;
                var candidate = EnsureTrailingSlash(new Uri($"http://{address}:{options.Value.ControllerPort}/", UriKind.Absolute));
                if (await IsControllerReachableAsync(candidate, TimeSpan.FromMilliseconds(450), timeout.Token))
                {
                    found.Enqueue(candidate);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        if (found.TryDequeue(out var controller))
        {
            logger.LogInformation("LAN scan found RenderFarm controller at {ControllerUrl}", controller);
            return controller;
        }

        logger.LogWarning("LAN scan did not find a RenderFarm controller on TCP port {ControllerPort}. Manual URL can still work if the controller is reachable and firewall allows TCP.", options.Value.ControllerPort);
        return null;
    }

    private IReadOnlyList<IPAddress> GetLanScanCandidates()
    {
        var candidates = new List<IPAddress>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivateLanAddress(unicast.Address))
                {
                    continue;
                }

                candidates.AddRange(BuildPrivateLanScanCandidates(unicast.Address, unicast.IPv4Mask, options.Value.LanScanMaxHosts));
            }
        }

        return candidates;
    }

    private async Task<bool> IsControllerReachableAsync(Uri baseUri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            using var client = new HttpClient { Timeout = timeout };
            using var response = await client.GetAsync(new Uri(baseUri, "health"), HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token);
            return document.RootElement.TryGetProperty("service", out var service)
                && string.Equals(service.GetString(), "RenderFarm.Controller.Api", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or SocketException)
        {
            return false;
        }
    }

    private Uri? TryReadLastKnownControllerUrl()
    {
        try
        {
            var path = ResolveLastKnownControllerPath();
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? EnsureTrailingSlash(uri) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveLastKnownControllerUrl(Uri uri)
    {
        try
        {
            var path = ResolveLastKnownControllerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, EnsureTrailingSlash(uri).AbsoluteUri);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not persist last known controller URL.");
        }
    }

    private string ResolveLastKnownControllerPath()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.LastKnownControllerPath))
        {
            return Path.GetFullPath(options.Value.LastKnownControllerPath);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : Path.Combine(localAppData, "RenderFarm");
        return Path.Combine(root, "worker-last-controller.url");
    }

    private static bool IsUsableRemoteControllerUri(Uri uri) =>
        !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(uri.Host, "0.0.0.0", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            && (bytes[0] == 10
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static IPAddress FromUInt32(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return new IPAddress(bytes);
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
                AgentVersion: RenderFarmVersion.FormatWorkerAgentVersion(RenderFarmVersion.ProductVersion, RenderFarmVersion.ProtocolVersion, RenderFarmVersion.ApiContractVersion, RenderFarmVersion.BuildId),
                Capabilities: detectedCapabilities.ToDto(),
                LastError: null,
                ProductVersion: RenderFarmVersion.ProductVersion,
                ProtocolVersion: RenderFarmVersion.ProtocolVersion,
                ApiContractVersion: RenderFarmVersion.ApiContractVersion,
                BuildId: RenderFarmVersion.BuildId);

            var client = httpClientFactory.CreateClient();
            WorkerHttp.ApplyControllerToken(client, options.Value.ApiToken);
            var endpoint = new Uri(await controllerEndpoints.GetControllerBaseUriAsync(cancellationToken), "api/workers/heartbeat");
            using var response = await client.PostAsJsonAsync(endpoint, heartbeat, RenderFarmJson.SerializerOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
            var acknowledgement = await response.Content.ReadFromJsonAsync<WorkerHeartbeatAcknowledgement>(RenderFarmJson.SerializerOptions, cancellationToken);
            if (acknowledgement?.Accepted == true)
            {
                logger.LogDebug(
                    "Worker {WorkerId} heartbeat accepted by controller; approval {Approval}; scheduling {SchedulingMode}; status {Status}",
                    heartbeat.WorkerId,
                    acknowledgement.Approval ?? "accepted",
                    acknowledgement.SchedulingMode ?? "unknown",
                    heartbeat.Status);
            }
            else
            {
                logger.LogInformation(
                    "Worker {WorkerId} heartbeat reached controller but is not available for scheduling yet; approval {Approval}; compatibility {CompatibilityReason}",
                    heartbeat.WorkerId,
                    acknowledgement?.Approval ?? "pending",
                    acknowledgement?.Compatibility?.Reason ?? "not reported");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Controller unavailable for worker heartbeat; will retry");
        }
    }

    private sealed class WorkerHeartbeatAcknowledgement
    {
        public bool Accepted { get; set; }
        public string? Approval { get; set; }
        [JsonPropertyName("scheduling_mode")]
        public string? SchedulingMode { get; set; }
        public VersionCompatibility? Compatibility { get; set; }
        [JsonPropertyName("controller_version")]
        public string? ControllerVersion { get; set; }
    }
}







