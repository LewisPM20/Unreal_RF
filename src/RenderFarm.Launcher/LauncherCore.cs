using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace RenderFarm.Launcher;

internal static class RenderFarmLauncher
{
    private const string ControllerRole = "controller";
    private const string WorkerRole = "worker";
    private static readonly TimeSpan DefaultControllerStartupTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan HealthAttemptTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions ConsoleJsonOptions = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        var options = LauncherOptions.Parse(args);
        using var launcherGuard = LauncherSingleInstanceGuard.TryAcquire(out var launcherGuardError);
        if (launcherGuard is null)
        {
            Console.Error.WriteLine(launcherGuardError);
            return 2;
        }

        if (options.ClearStaleRuntime)
        {
            var removed = LauncherRuntimeState.ClearStale();
            Console.WriteLine($"Cleared {removed} stale RenderFarm runtime record(s).");
            if (string.IsNullOrWhiteSpace(options.Role)) return 0;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        var settingsPath = ResolveSettingsPath(options.SettingsPath);
        var settings = LauncherSettings.Load(settingsPath);
        if (options.ShowConfig)
        {
            Console.WriteLine($"RenderFarm role settings: {settingsPath}");
            Console.WriteLine(settings is null ? "No saved role settings found." : JsonSerializer.Serialize(settings, ConsoleJsonOptions));
            if (string.IsNullOrWhiteSpace(options.Role))
            {
                return 0;
            }
        }

        settings = Merge(settings, options);
        if (string.IsNullOrWhiteSpace(settings.Role))
        {
            settings.Role = PromptForRole();
        }

        if (!IsValidRole(settings.Role))
        {
            Console.Error.WriteLine("RenderFarm role must be 'controller' or 'worker'.");
            return 2;
        }

        if (options.SaveRole)
        {
            settings.Save(settingsPath);
            Console.WriteLine($"Saved RenderFarm role settings to {settingsPath}");
        }

        return StartRole(settings);
    }

    public static LauncherSettings Merge(LauncherSettings? stored, LauncherOptions options)
    {
        var merged = stored ?? new LauncherSettings();
        if (!string.IsNullOrWhiteSpace(options.Role)) merged.Role = options.Role;
        if (!string.IsNullOrWhiteSpace(options.HostName)) merged.HostName = options.HostName;
        if (options.Port is not null) merged.Port = options.Port.Value;
        if (!string.IsNullOrWhiteSpace(options.ControllerUrl)) merged.ControllerUrl = options.ControllerUrl;
        if (!string.IsNullOrWhiteSpace(options.WorkerId)) merged.WorkerId = options.WorkerId;
        if (!string.IsNullOrWhiteSpace(options.DisplayName)) merged.DisplayName = options.DisplayName;
        if (!string.IsNullOrWhiteSpace(options.ApiToken)) merged.ApiToken = options.ApiToken;
        if (!string.IsNullOrWhiteSpace(options.UnrealSearchRoot)) merged.UnrealSearchRoot = options.UnrealSearchRoot;
        if (!string.IsNullOrWhiteSpace(options.ProjectPath)) merged.ProjectPath = options.ProjectPath;
        if (!string.IsNullOrWhiteSpace(options.SharedOutputRoot)) merged.SharedOutputRoot = options.SharedOutputRoot;
        if (options.DiscoveryEnabled is not null) merged.DiscoveryEnabled = options.DiscoveryEnabled.Value;
        if (options.LanScanEnabled is not null) merged.LanScanEnabled = options.LanScanEnabled.Value;
        merged.UpdatedUtc = DateTimeOffset.UtcNow;
        return merged;
    }

    public static string ResolveSettingsPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = AppContext.BaseDirectory;
        }

        return Path.Combine(baseDirectory, "RenderFarm", "app-role.json");
    }

    public static bool IsValidRole(string? role) => string.Equals(role, ControllerRole, StringComparison.OrdinalIgnoreCase) || string.Equals(role, WorkerRole, StringComparison.OrdinalIgnoreCase);

    public static string GetDashboardUrl(LauncherSettings settings)
    {
        return TryBuildControllerNetworkUrls(settings, null, out var urls, out _) ? urls.BindUrl : "http://127.0.0.1:9200/";
    }

    public static string GetHealthUrl(string dashboardUrl) => new Uri(new Uri(EnsureTrailingSlash(dashboardUrl)), "health").ToString();

    public static string GetControllerHealthUrl(LauncherSettings settings, string dashboardUrl)
    {
        return TryBuildControllerNetworkUrls(settings, null, out var urls, out _) ? urls.LocalHealthUrl : GetHealthUrl(dashboardUrl);
    }

    public static string GetControllerBrowseUrl(LauncherSettings settings, string dashboardUrl)
    {
        return TryBuildControllerNetworkUrls(settings, null, out var urls, out _) ? urls.LocalDashboardUrl : dashboardUrl;
    }

    internal static bool TryBuildControllerNetworkUrls(LauncherSettings settings, string? lanIpOverride, out ControllerNetworkUrls urls, out string error)
    {
        urls = ControllerNetworkUrls.Empty;
        error = string.Empty;
        var host = string.IsNullOrWhiteSpace(settings.HostName) ? "127.0.0.1" : settings.HostName.Trim();
        var port = settings.Port;

        if (Uri.TryCreate(host, UriKind.Absolute, out var hostUri) && (hostUri.Scheme == Uri.UriSchemeHttp || hostUri.Scheme == Uri.UriSchemeHttps))
        {
            host = hostUri.Host;
            if (port == 9200 && !hostUri.IsDefaultPort)
            {
                port = hostUri.Port;
            }
        }

        if (port is < 1 or > 65535)
        {
            error = "Controller port must be a number between 1 and 65535.";
            return false;
        }

        if (host.Contains('/') || host.Contains('\\'))
        {
            error = "Controller host should be a host name or IP address, not a full path.";
            return false;
        }

        try
        {
            var bindHost = NormalizeHost(host);
            var isWildcard = IsWildcardHost(bindHost);
            var isLoopback = IsLoopbackHost(bindHost);
            var lanHost = isWildcard
                ? FirstNonEmpty(lanIpOverride, GetPrimaryLanIpAddress(), Environment.MachineName)
                : bindHost;

            var bindUrl = EnsureTrailingSlash(new UriBuilder(Uri.UriSchemeHttp, bindHost, port).Uri.ToString());
            var localDashboardUrl = isWildcard
                ? EnsureTrailingSlash(new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port).Uri.ToString())
                : bindUrl;
            var lanWorkerUrl = isLoopback
                ? bindUrl
                : EnsureTrailingSlash(new UriBuilder(Uri.UriSchemeHttp, lanHost, port).Uri.ToString());

            urls = new ControllerNetworkUrls(
                BindUrl: bindUrl,
                LocalDashboardUrl: localDashboardUrl,
                LocalHealthUrl: GetHealthUrl(localDashboardUrl),
                LanWorkerUrl: lanWorkerUrl,
                LanHealthUrl: GetHealthUrl(lanWorkerUrl),
                AdvertisedDiscoveryUrl: lanWorkerUrl,
                BindHost: bindHost,
                LanIp: isLoopback ? null : lanHost,
                IsLanMode: !isLoopback,
                IsWildcardBind: isWildcard);
            return true;
        }
        catch (UriFormatException ex)
        {
            error = $"Controller host/port setting is invalid: {ex.Message}";
            return false;
        }
    }
    public static string EnsureTrailingSlash(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
    }

    public static int StartRole(LauncherSettings settings, bool waitForExit = true)
    {
        var result = StartRoleDetailed(settings, captureOutput: false);
        if (!result.Started)
        {
            Console.Error.WriteLine(result.ErrorMessage);
            return result.LauncherExitCode;
        }

        if (!waitForExit)
        {
            return 0;
        }

        using var process = result.Process;
        process!.WaitForExit();
        return process.ExitCode;
    }

    public static RoleLaunchResult StartRoleDetailed(LauncherSettings settings, bool captureOutput)
    {
        if (!IsValidRole(settings.Role))
        {
            return RoleLaunchResult.Failed(2, "RenderFarm role must be 'controller' or 'worker'.", []);
        }

        var role = settings.Role!.Trim().ToLowerInvariant();
        string? dashboardUrl = null;
        string? healthUrl = null;
        ControllerNetworkUrls? controllerUrls = null;
        if (role == ControllerRole)
        {
            if (!TryValidateControllerDiscoverySettings(settings, out var discoveryError))
            {
                return RoleLaunchResult.Failed(2, discoveryError, []);
            }

            if (!TryBuildControllerNetworkUrls(settings, null, out var urls, out var error))
            {
                return RoleLaunchResult.Failed(2, error, []);
            }

            controllerUrls = urls;
            dashboardUrl = urls.BindUrl;
            healthUrl = urls.LocalHealthUrl;
            if (IsTcpPortInUse(settings.Port))
            {
                return RoleLaunchResult.Failed(5, ExplainBusyPort(settings.Port), [], dashboardUrl: dashboardUrl, healthUrl: healthUrl);
            }
        }

        var candidates = GetRuntimeExecutableCandidates(role);
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            return RoleLaunchResult.Failed(3, BuildExecutableNotFoundMessage(role, candidates), candidates);
        }

        var output = captureOutput ? new ProcessOutputBuffer() : null;
        try
        {
            var startInfo = BuildStartInfo(role, exe, settings, dashboardUrl, captureOutput);
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (captureOutput)
            {
                process.OutputDataReceived += (_, e) => output!.Add(e.Data);
                process.ErrorDataReceived += (_, e) => output!.Add(e.Data);
            }

            if (!process.Start())
            {
                return RoleLaunchResult.Failed(4, $"Windows did not start {exe}.", candidates);
            }

            if (captureOutput)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            LauncherRuntimeState.Register(role, process, exe, settings, dashboardUrl, healthUrl);

            if (role == ControllerRole)
            {
                Console.WriteLine($"Starting RenderFarm controller bind URL: {controllerUrls!.BindUrl.TrimEnd('/')}");
                Console.WriteLine($"Open dashboard on this PC: {controllerUrls.LocalDashboardUrl}");
                Console.WriteLine($"Workers should use: {controllerUrls.LanWorkerUrl}");
                if (settings.DiscoveryEnabled)
                {
                    Console.WriteLine($"Discovery advertises: {controllerUrls.AdvertisedDiscoveryUrl} on UDP {settings.DiscoveryPort}");
                }
            }
            else
            {
                Console.WriteLine($"Starting RenderFarm worker for {GetWorkerControllerLabel(settings)}");
            }

            return new RoleLaunchResult(true, process, exe, dashboardUrl, healthUrl, null, candidates, output, 0);
        }
        catch (Exception ex)
        {
            var reason = ExplainControllerStartupFailure($"Could not start {exe}: {ex.Message}", null);
            return RoleLaunchResult.Failed(4, reason, candidates, exe, dashboardUrl, healthUrl, output);
        }
    }
    public static string? FindRuntimeExecutable(string role) => GetRuntimeExecutableCandidates(role).FirstOrDefault(File.Exists);

    internal static string BuildExecutableNotFoundMessage(string role, IReadOnlyList<string> candidates)
    {
        var expectedLayout = string.Equals(role, ControllerRole, StringComparison.OrdinalIgnoreCase)
            ? "controller\\RenderFarm.Controller.Api.exe"
            : "worker\\RenderFarm.Worker.Agent.exe";
        return $"Could not find the RenderFarm {role} executable. Expected a published package with {expectedLayout} beside RenderFarm.Launcher.exe. Checked:{Environment.NewLine}{string.Join(Environment.NewLine, candidates.Select(path => "  " + path))}";
    }

    public static IReadOnlyList<string> GetRuntimeExecutableCandidates(string role)
    {
        var normalizedRole = string.Equals(role, ControllerRole, StringComparison.OrdinalIgnoreCase) ? ControllerRole : WorkerRole;
        var fileName = normalizedRole == ControllerRole ? ExecutableName("RenderFarm.Controller.Api") : ExecutableName("RenderFarm.Worker.Agent");
        var baseDirectory = AppContext.BaseDirectory;
        return new[]
        {
            Path.GetFullPath(Path.Combine(baseDirectory, normalizedRole, fileName)),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", normalizedRole, fileName)),
            Path.GetFullPath(Path.Combine(baseDirectory, fileName))
        };
    }

    public static async Task<ControllerHealthResult> CheckControllerHealthAsync(HttpClient client, string healthUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(healthUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new ControllerHealthResult(true, $"Controller health check succeeded at {healthUrl}.", (int)response.StatusCode);
            }

            return new ControllerHealthResult(false, $"Controller health check returned HTTP {(int)response.StatusCode} at {healthUrl}.", (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            return new ControllerHealthResult(false, $"Health check timed out or was cancelled at {healthUrl}.");
        }
        catch (HttpRequestException ex)
        {
            return new ControllerHealthResult(false, $"Controller health check could not connect to {healthUrl}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ControllerHealthResult(false, $"Controller health check failed at {healthUrl}: {ex.Message}");
        }
    }

    public static async Task<ControllerHealthResult> CheckControllerIdentityAsync(HttpClient client, string healthUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(healthUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ControllerHealthResult(false, $"Controller identity check returned HTTP {(int)response.StatusCode} at {healthUrl}.", (int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("service", out var service)
                && string.Equals(service.GetString(), "RenderFarm.Controller.Api", StringComparison.OrdinalIgnoreCase))
            {
                return new ControllerHealthResult(true, $"RenderFarm controller identity confirmed at {healthUrl}.", (int)response.StatusCode);
            }

            return new ControllerHealthResult(false, $"A process answered at {healthUrl}, but it did not identify as RenderFarm.Controller.Api.", (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            return new ControllerHealthResult(false, $"Identity check timed out or was cancelled at {healthUrl}.");
        }
        catch (HttpRequestException ex)
        {
            return new ControllerHealthResult(false, $"Controller identity check could not connect to {healthUrl}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return new ControllerHealthResult(false, $"A process answered at {healthUrl}, but its health response was not valid RenderFarm JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ControllerHealthResult(false, $"Controller identity check failed at {healthUrl}: {ex.Message}");
        }
    }
    public static async Task<ControllerHealthResult> WaitForControllerHealthAsync(RoleLaunchResult launch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(launch.HealthUrl))
        {
            return new ControllerHealthResult(false, "Controller health URL was not available.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultControllerStartupTimeout);
        using var client = new HttpClient { Timeout = HealthAttemptTimeout };
        var lastDetail = "No health check response was received.";

        while (!timeout.IsCancellationRequested)
        {
            if (launch.Process is { HasExited: true })
            {
                var exitReason = $"Controller process exited immediately with code {launch.Process.ExitCode}.";
                return new ControllerHealthResult(false, ExplainControllerStartupFailure(exitReason, launch.Output?.GetTail()));
            }

            var result = await CheckControllerHealthAsync(client, launch.HealthUrl, timeout.Token);
            if (result.Succeeded)
            {
                return result;
            }

            lastDetail = result.Detail;
            try
            {
                await Task.Delay(HealthPollInterval, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var timeoutReason = $"Health check timed out waiting for {launch.HealthUrl}. Last result: {lastDetail}";
        return new ControllerHealthResult(false, ExplainControllerStartupFailure(timeoutReason, launch.Output?.GetTail()));
    }

    internal static bool TryBuildDashboardUrl(LauncherSettings settings, out string dashboardUrl, out string error)
    {
        if (TryBuildControllerNetworkUrls(settings, null, out var urls, out error))
        {
            dashboardUrl = urls.BindUrl;
            return true;
        }

        dashboardUrl = string.Empty;
        return false;
    }
    internal static bool TryValidateControllerDiscoverySettings(LauncherSettings settings, out string error)
    {
        error = string.Empty;
        if (!settings.DiscoveryEnabled)
        {
            return true;
        }

        var host = NormalizeHost(settings.HostName);
        if (IsLoopbackHost(host))
        {
            error = "LAN discovery is enabled, but the controller is bound to 127.0.0.1/localhost. Set Host name or IP to 0.0.0.0 or a LAN IP so workers can connect.";
            return false;
        }

        return true;
    }

    internal static string BuildControllerDiscoveryUrl(LauncherSettings settings, string? lanAddressOverride = null)
    {
        return TryBuildControllerNetworkUrls(settings, lanAddressOverride, out var urls, out _)
            ? urls.AdvertisedDiscoveryUrl
            : "http://127.0.0.1:9200/";
    }

    internal static string NormalizeHost(string? host)
    {
        var value = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.Host;
        }

        return value;
    }

    internal static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    internal static bool IsWildcardHost(string host) =>
        string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "+", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "::", StringComparison.OrdinalIgnoreCase);

    internal static string? GetPrimaryLanIpAddress()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    internal static bool IsTcpPortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    internal static string ExplainBusyPort(int port)
    {
        var tracked = LauncherRuntimeState.Inspect().FirstOrDefault(item => string.Equals(item.Record.Role, ControllerRole, StringComparison.OrdinalIgnoreCase) && item.IsRunning);
        if (tracked is not null)
        {
            return $"Controller port {port} is already in use by tracked RenderFarm controller PID {tracked.Record.ProcessId}. Reconnect to the existing dashboard or use Stop RenderFarm Processes from the launcher before starting a new controller.";
        }

        return $"Controller port {port} is already in use. It does not match a tracked RenderFarm controller in the launcher runtime state. Choose another port or stop the owning application; RenderFarm will not kill unrelated processes.";
    }
    internal static string GetDefaultControllerDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(localAppData, "RenderFarm", "Controller");

        return Path.Combine(root, "renderfarm.db");
    }

    internal static string ExplainControllerStartupFailure(string reason, string? outputTail)
    {
        var combined = (reason + Environment.NewLine + outputTail).ToLowerInvariant();
        var likelyCause = "";
        if (combined.Contains("address already in use") || combined.Contains("only one usage of each socket address"))
        {
            likelyCause = "Likely cause: the configured port is already in use. Close the other controller instance or choose a different port.";
        }
        else if (combined.Contains("access is denied") || combined.Contains("permission denied"))
        {
            likelyCause = "Likely cause: Windows denied the bind or file access. Try another port, check firewall policy, or install/start from a writable location.";
        }
        else if (combined.Contains("sqlite error 14") || combined.Contains("unable to open database file"))
        {
            likelyCause = $"Likely cause: the controller could not open its SQLite database. The launcher uses {GetDefaultControllerDatabasePath()} by default; confirm that folder is writable.";
        }
        else if (combined.Contains("configuration") || combined.Contains("appsettings"))
        {
            likelyCause = "Likely cause: controller configuration could not be loaded. Confirm the published controller folder contains the expected appsettings and dependency files.";
        }
        else if (combined.Contains("firewall"))
        {
            likelyCause = "Likely cause: local firewall or endpoint protection blocked the controller.";
        }

        var parts = new List<string> { reason };
        if (!string.IsNullOrWhiteSpace(likelyCause)) parts.Add(likelyCause);
        if (!string.IsNullOrWhiteSpace(outputTail)) parts.Add("Controller output:" + Environment.NewLine + outputTail.Trim());
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    internal static ProcessStartInfo BuildStartInfo(string role, string exe, LauncherSettings settings, string? dashboardUrl, bool captureOutput)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            CreateNoWindow = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };

        if (role == ControllerRole)
        {
            var bindUrl = dashboardUrl!.TrimEnd('/');
            startInfo.ArgumentList.Add("--urls");
            startInfo.ArgumentList.Add(bindUrl);
            startInfo.Environment["ASPNETCORE_URLS"] = bindUrl;
            if (!startInfo.Environment.ContainsKey("RenderFarm__Database__ConnectionString"))
            {
                var databasePath = GetDefaultControllerDatabasePath();
                EnsureDirectoryForFile(databasePath);
                startInfo.Environment["RenderFarm__Database__Path"] = databasePath;
            }

            if (!string.IsNullOrWhiteSpace(settings.ApiToken))
            {
                startInfo.Environment["RenderFarm__Security__ApiToken"] = settings.ApiToken;
            }

            if (settings.DiscoveryEnabled)
            {
                var advertisedUrl = BuildControllerDiscoveryUrl(settings);
                startInfo.Environment["RenderFarm__Discovery__Enabled"] = "true";
                startInfo.Environment["RenderFarm__Discovery__Port"] = settings.DiscoveryPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                startInfo.Environment["RenderFarm__Discovery__ControllerUrl"] = advertisedUrl;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.ControllerUrl)) startInfo.Environment["RenderFarm__ControllerUrl"] = settings.ControllerUrl;
            if (!string.IsNullOrWhiteSpace(settings.WorkerId)) startInfo.Environment["RenderFarm__WorkerId"] = settings.WorkerId;
            if (!string.IsNullOrWhiteSpace(settings.DisplayName)) startInfo.Environment["RenderFarm__DisplayName"] = settings.DisplayName;
            if (!string.IsNullOrWhiteSpace(settings.ApiToken)) startInfo.Environment["RenderFarm__ApiToken"] = settings.ApiToken;
            startInfo.Environment["RenderFarm__DiscoveryEnabled"] = settings.DiscoveryEnabled.ToString();
            startInfo.Environment["RenderFarm__DiscoverySeconds"] = settings.DiscoverySeconds.ToString();
            startInfo.Environment["RenderFarm__DiscoveryPort"] = settings.DiscoveryPort.ToString();
            startInfo.Environment["RenderFarm__ControllerPort"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["RenderFarm__LanScanEnabled"] = settings.LanScanEnabled.ToString();
            startInfo.Environment["RenderFarm__LanScanTimeoutSeconds"] = settings.LanScanTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["RenderFarm__LanScanMaxHosts"] = settings.LanScanMaxHosts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return startInfo;
    }

    private static void EnsureDirectoryForFile(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string GetWorkerControllerLabel(LauncherSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ControllerUrl)) return settings.ControllerUrl;
        if (settings.DiscoveryEnabled && settings.LanScanEnabled) return "LAN discovery, LAN scan, then localhost fallback";
        if (settings.DiscoveryEnabled) return "LAN discovery, then localhost fallback";
        if (settings.LanScanEnabled) return "LAN scan, then localhost fallback";
        return "local fallback http://127.0.0.1:9200";
    }

    private static string PromptForRole()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("No RenderFarm role was supplied or saved. Use --role controller or --role worker.");
            return string.Empty;
        }

        Console.WriteLine("RenderFarm first run");
        Console.WriteLine("  controller: central dashboard, queue, scheduler, and SQLite database");
        Console.WriteLine("  worker: render machine that connects to a controller and runs Unreal jobs");
        Console.Write("Choose this machine role [controller/worker]: ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string ExecutableName(string baseName) => OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;

    private static void PrintHelp()
    {
        Console.WriteLine("RenderFarm Launcher");
        Console.WriteLine("Usage:");
        Console.WriteLine("  RenderFarm.Launcher --role controller --save --host 0.0.0.0 --port 9200 --discovery");
        Console.WriteLine("  RenderFarm.Launcher --role worker --save --discovery --worker-id worker-01");
        Console.WriteLine("  RenderFarm.Launcher --role worker --save --discovery --display-name Render-PC-01");
        Console.WriteLine("  RenderFarm.Launcher --show-config");
        Console.WriteLine("  RenderFarm.Launcher --clear-stale-runtime");
    }
}

internal sealed record ControllerNetworkUrls(
    string BindUrl,
    string LocalDashboardUrl,
    string LocalHealthUrl,
    string LanWorkerUrl,
    string LanHealthUrl,
    string AdvertisedDiscoveryUrl,
    string BindHost,
    string? LanIp,
    bool IsLanMode,
    bool IsWildcardBind)
{
    public static ControllerNetworkUrls Empty { get; } = new(
        "http://127.0.0.1:9200/",
        "http://127.0.0.1:9200/",
        "http://127.0.0.1:9200/health",
        "http://127.0.0.1:9200/",
        "http://127.0.0.1:9200/health",
        "http://127.0.0.1:9200/",
        "127.0.0.1",
        null,
        false,
        false);
}
internal sealed record RoleLaunchResult(
    bool Started,
    Process? Process,
    string? ExecutablePath,
    string? DashboardUrl,
    string? HealthUrl,
    string? ErrorMessage,
    IReadOnlyList<string> CheckedPaths,
    ProcessOutputBuffer? Output,
    int LauncherExitCode)
{
    public static RoleLaunchResult Failed(int launcherExitCode, string errorMessage, IReadOnlyList<string> checkedPaths, string? executablePath = null, string? dashboardUrl = null, string? healthUrl = null, ProcessOutputBuffer? output = null) =>
        new(false, null, executablePath, dashboardUrl, healthUrl, errorMessage, checkedPaths, output, launcherExitCode);
}

internal sealed record ControllerHealthResult(bool Succeeded, string Detail, int? StatusCode = null);

internal sealed class ProcessOutputBuffer
{
    private const int MaximumLines = 100;
    private readonly object gate = new();
    private readonly Queue<string> lines = new();

    public void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (gate)
        {
            lines.Enqueue($"[{DateTimeOffset.Now:HH:mm:ss}] {line}");
            while (lines.Count > MaximumLines)
            {
                lines.Dequeue();
            }
        }
    }

    public string GetTail(int lineCount = 24)
    {
        lock (gate)
        {
            return string.Join(Environment.NewLine, lines.TakeLast(lineCount));
        }
    }
}

internal sealed class LauncherSettings
{
    public string? Role { get; set; }
    public string HostName { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9200;
    public string? ControllerUrl { get; set; }
    public bool DiscoveryEnabled { get; set; }
    public int DiscoverySeconds { get; set; } = 5;
    public int DiscoveryPort { get; set; } = 39200;
    public bool LanScanEnabled { get; set; } = true;
    public int LanScanTimeoutSeconds { get; set; } = 4;
    public int LanScanMaxHosts { get; set; } = 254;
    public string? WorkerId { get; set; }
    public string? DisplayName { get; set; }
    public string? ApiToken { get; set; }
    public string? UnrealSearchRoot { get; set; }
    public string? ProjectPath { get; set; }
    public string? SharedOutputRoot { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public static LauncherSettings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), LauncherJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        UpdatedUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(this, LauncherJson.Options));
    }
}

internal static class LauncherJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
}

internal sealed class LauncherOptions
{
    public string? Role { get; private init; }
    public bool SaveRole { get; private init; }
    public bool ShowConfig { get; private init; }
    public bool ShowHelp { get; private init; }
    public bool ClearStaleRuntime { get; private init; }
    public string? SettingsPath { get; private init; }
    public string? HostName { get; private init; }
    public int? Port { get; private init; }
    public string? ControllerUrl { get; private init; }
    public bool? DiscoveryEnabled { get; private init; }
    public string? WorkerId { get; private init; }
    public string? DisplayName { get; private init; }
    public string? ApiToken { get; private init; }
    public string? UnrealSearchRoot { get; private init; }
    public string? ProjectPath { get; private init; }
    public string? SharedOutputRoot { get; private init; }
    public bool? LanScanEnabled { get; private init; }

    public static LauncherOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var item = args[i];
            if (!item.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = item[2..];
            if (key is "save" or "show-config" or "help" or "discovery" or "no-discovery" or "lan-scan" or "no-lan-scan" or "clear-stale-runtime")
            {
                flags.Add(key);
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[key] = string.Empty;
                continue;
            }

            values[key] = args[++i];
        }

        return new LauncherOptions
        {
            Role = Get(values, "role"),
            SaveRole = flags.Contains("save"),
            ShowConfig = flags.Contains("show-config"),
            ShowHelp = flags.Contains("help"),
            ClearStaleRuntime = flags.Contains("clear-stale-runtime"),
            SettingsPath = Get(values, "settings"),
            HostName = Get(values, "host"),
            Port = int.TryParse(Get(values, "port"), out var port) ? port : null,
            ControllerUrl = Get(values, "controller-url"),
            DiscoveryEnabled = ParseDiscovery(flags, values),
            WorkerId = Get(values, "worker-id"),
            DisplayName = Get(values, "display-name"),
            ApiToken = Get(values, "api-token"),
            UnrealSearchRoot = Get(values, "unreal-search-root"),
            ProjectPath = Get(values, "project-path"),
            SharedOutputRoot = Get(values, "shared-output-root"),
            LanScanEnabled = ParseLanScan(flags, values)
        };
    }

    private static bool? ParseDiscovery(IReadOnlySet<string> flags, IReadOnlyDictionary<string, string?> values)
    {
        if (flags.Contains("discovery")) return true;
        if (flags.Contains("no-discovery")) return false;
        return bool.TryParse(Get(values, "discovery-enabled"), out var enabled) ? enabled : null;
    }

    private static bool? ParseLanScan(IReadOnlySet<string> flags, IReadOnlyDictionary<string, string?> values)
    {
        if (flags.Contains("lan-scan")) return true;
        if (flags.Contains("no-lan-scan")) return false;
        return bool.TryParse(Get(values, "lan-scan-enabled"), out var enabled) ? enabled : null;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) => values.TryGetValue(key, out var value) ? value : null;
}
























