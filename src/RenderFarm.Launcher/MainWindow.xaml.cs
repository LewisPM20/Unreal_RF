using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace RenderFarm.Launcher;

public partial class MainWindow : Window
{
    private readonly string settingsPath = RenderFarmLauncher.ResolveSettingsPath(null);
    private RoleLaunchResult? activeControllerLaunch;
    private RoleLaunchResult? activeWorkerLaunch;
    private bool controllerVerified;

    public MainWindow()
    {
        InitializeComponent();
        SettingsPathText.Text = settingsPath;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = LauncherSettings.Load(settingsPath) ?? new LauncherSettings { Role = "controller" };
        ControllerRole.IsChecked = !string.Equals(settings.Role, "worker", StringComparison.OrdinalIgnoreCase);
        WorkerRole.IsChecked = string.Equals(settings.Role, "worker", StringComparison.OrdinalIgnoreCase);
        HostNameBox.Text = settings.HostName;
        PortBox.Text = settings.Port.ToString();
        ControllerUrlBox.Text = settings.ControllerUrl ?? string.Empty;
        DiscoveryEnabledBox.IsChecked = settings.DiscoveryEnabled;
        WorkerIdBox.Text = settings.WorkerId ?? string.Empty;
        DisplayNameBox.Text = settings.DisplayName ?? string.Empty;
        ApiTokenBox.Text = settings.ApiToken ?? string.Empty;
        UnrealSearchRootBox.Text = settings.UnrealSearchRoot ?? string.Empty;
        ProjectPathBox.Text = settings.ProjectPath ?? string.Empty;
        SharedOutputRootBox.Text = settings.SharedOutputRoot ?? string.Empty;
        UpdateRolePanels();
    }

    private LauncherSettings ReadSettingsFromUi()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            throw new InvalidOperationException("Controller port must be a number between 1 and 65535.");
        }

        var role = WorkerRole.IsChecked == true ? "worker" : "controller";
        return new LauncherSettings
        {
            Role = role,
            HostName = TrimOrDefault(HostNameBox.Text, "127.0.0.1"),
            Port = port,
            ControllerUrl = TrimOrNull(ControllerUrlBox.Text),
            DiscoveryEnabled = DiscoveryEnabledBox.IsChecked == true,
            WorkerId = TrimOrNull(WorkerIdBox.Text),
            DisplayName = TrimOrNull(DisplayNameBox.Text),
            ApiToken = TrimOrNull(ApiTokenBox.Text),
            UnrealSearchRoot = TrimOrNull(UnrealSearchRootBox.Text),
            ProjectPath = TrimOrNull(ProjectPathBox.Text),
            SharedOutputRoot = TrimOrNull(SharedOutputRootBox.Text),
            UpdatedUtc = DateTimeOffset.UtcNow
        };
    }

    private void RoleChanged(object sender, RoutedEventArgs e) => UpdateRolePanels();

    private void UpdateRolePanels()
    {
        if (ControllerPanel is null || WorkerPanel is null || StatusHeadlineText is null)
        {
            return;
        }

        var isWorker = WorkerRole.IsChecked == true;
        ControllerPanel.Visibility = isWorker ? Visibility.Collapsed : Visibility.Visible;
        WorkerPanel.Visibility = isWorker ? Visibility.Visible : Visibility.Collapsed;
        if (DiscoveryModeHelpText is not null)
        {
            DiscoveryModeHelpText.Text = isWorker
                ? "Worker mode: find a LAN controller when Controller URL is blank. Manual Controller URL still has priority."
                : "Controller mode: advertise this controller on the LAN for workers. Use host 0.0.0.0 or this PC's LAN IP.";
        }

        controllerVerified = false;
        SetStatus(isWorker
            ? "Worker mode selected."
            : "Controller mode selected.",
            isWorker
                ? "Leave Controller URL blank to use LAN discovery, or enter a manual controller URL to bypass discovery."
                : "For LAN discovery, set Host name or IP to 0.0.0.0 or a LAN IP, then enable LAN discovery.");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            settings.Save(settingsPath);
            SetStatus("Settings saved.", settingsPath);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void StartRole_Click(object sender, RoutedEventArgs e)
    {
        StartRoleButton.IsEnabled = false;
        try
        {
            var settings = ReadSettingsFromUi();
            settings.Save(settingsPath);
            var previousControllerProcess = activeControllerLaunch?.Process;
            if (previousControllerProcess is { HasExited: true })
            {
                previousControllerProcess.Dispose();
                activeControllerLaunch = null;
            }

            var isController = string.Equals(settings.Role, "controller", StringComparison.OrdinalIgnoreCase);
            if (isController)
            {
                if (!RenderFarmLauncher.TryValidateControllerDiscoverySettings(settings, out var discoveryError))
                {
                    SetStatus("Controller LAN discovery needs attention.", discoveryError);
                    return;
                }

                if (!RenderFarmLauncher.TryBuildControllerNetworkUrls(settings, null, out var urls, out var error))
                {
                    SetStatus("Controller dashboard failed.", error);
                    return;
                }

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                {
                    var local = await RenderFarmLauncher.CheckControllerIdentityAsync(client, urls.LocalHealthUrl, CancellationToken.None);
                    if (local.Succeeded && activeControllerLaunch?.Process is not { HasExited: false })
                    {
                        if (urls.IsLanMode)
                        {
                            var lan = await RenderFarmLauncher.CheckControllerIdentityAsync(client, urls.LanHealthUrl, CancellationToken.None);
                            if (!lan.Succeeded)
                            {
                                SetStatus(
                                    "Controller is local-only or blocked from LAN.",
                                    "A controller is running locally on port " + settings.Port + ", but it is not reachable at " + urls.LanHealthUrl + ". It may be bound to 127.0.0.1 or blocked by Windows Firewall. Stop it and restart in LAN mode, or open TCP " + settings.Port + " inbound.");
                                return;
                            }
                        }

                        var reuse = MessageBox.Show(
                            this,
                            "A RenderFarm controller is already responding for the requested mode. Reuse the existing controller? Choose No to cancel startup.",
                            "Controller already running",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);
                        if (reuse == MessageBoxResult.Yes)
                        {
                            controllerVerified = true;
                            SetStatus("Controller dashboard already running.", BuildControllerStartDetails(settings, urls));
                            return;
                        }

                        SetStatus("Controller startup cancelled.", "Existing controller remains running and was not touched by this launcher instance.");
                        return;
                    }
                }

                StopOwnedLaunch(activeControllerLaunch, "previous controller", TimeSpan.FromSeconds(4));
                activeControllerLaunch = null;
                SetStatus("Starting controller dashboard...", BuildControllerStartDetails(settings, urls));
                var launch = RenderFarmLauncher.StartRoleDetailed(settings, captureOutput: true);
                if (!launch.Started)
                {
                    SetStatus("Controller dashboard failed.", launch.ErrorMessage ?? "Controller process could not be started.");
                    return;
                }

                activeControllerLaunch = launch;
                var readiness = await RenderFarmLauncher.WaitForControllerHealthAsync(launch, CancellationToken.None);
                if (readiness.Succeeded)
                {
                    controllerVerified = true;
                    SetStatus("Controller dashboard ready!", BuildControllerStartDetails(settings, urls));
                    return;
                }

                controllerVerified = false;
                SetStatus("Controller dashboard failed.", readiness.Detail);
                return;
            }
            SetStatus("Starting worker...", "The worker will appear in the controller dashboard after it heartbeats.");
            StopOwnedLaunch(activeWorkerLaunch, "previous worker", TimeSpan.FromSeconds(4));
            activeWorkerLaunch = null;
            var workerLaunch = RenderFarmLauncher.StartRoleDetailed(settings, captureOutput: false);
            if (workerLaunch.Started)
            {
                activeWorkerLaunch = workerLaunch;
                SetStatus("Worker started.", "Closing the launcher will stop this worker process unless it was installed as a Windows Service.");
            }
            else
            {
                SetStatus("Worker start failed.", workerLaunch.ErrorMessage ?? "Worker process could not be started.");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            StartRoleButton.IsEnabled = true;
        }
    }

    private async void CheckController_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            var healthUrl = BuildHealthUrl(settings);
            if (healthUrl is null)
            {
                SetStatus("Controller URL required.", "LAN discovery cannot be verified from the launcher. Enter a Controller URL to check it directly.");
                return;
            }

            SetStatus("Checking controller...", healthUrl);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var result = await RenderFarmLauncher.CheckControllerHealthAsync(client, healthUrl, CancellationToken.None);
            controllerVerified = result.Succeeded;
            SetStatus(result.Succeeded ? "Controller responded successfully." : "Controller check failed.", result.Detail);
        }
        catch (Exception ex)
        {
            controllerVerified = false;
            SetStatus("Controller check failed.", ex.Message);
        }
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            var url = settings.Role == "worker" && !string.IsNullOrWhiteSpace(settings.ControllerUrl)
                ? RenderFarmLauncher.EnsureTrailingSlash(settings.ControllerUrl)
                : RenderFarmLauncher.GetControllerBrowseUrl(settings, RenderFarmLauncher.GetDashboardUrl(settings));

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SetStatus("Dashboard opened.", url);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void TestLanSetup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            if (!string.Equals(settings.Role, "controller", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Switch to Controller mode.", "The LAN setup test checks the controller bind URL, local dashboard URL, and worker LAN URL.");
                return;
            }

            if (!RenderFarmLauncher.TryBuildControllerNetworkUrls(settings, null, out var urls, out var error))
            {
                SetStatus("LAN setup test could not run.", error);
                return;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var local = await RenderFarmLauncher.CheckControllerIdentityAsync(client, urls.LocalHealthUrl, CancellationToken.None);
            var lan = urls.IsLanMode
                ? await RenderFarmLauncher.CheckControllerIdentityAsync(client, urls.LanHealthUrl, CancellationToken.None)
                : local;
            var portState = RenderFarmLauncher.IsTcpPortInUse(settings.Port)
                ? $"TCP {settings.Port} is listening on this PC."
                : $"TCP {settings.Port} is not currently listening. Start the controller before expecting workers to connect.";

            var details = new List<string>
            {
                $"Controller bind URL: {urls.BindUrl}",
                $"Open dashboard on this PC: {urls.LocalDashboardUrl}",
                $"Workers should use: {urls.LanWorkerUrl}",
                $"Discovery advertises: {urls.AdvertisedDiscoveryUrl}",
                portState,
                local.Succeeded ? "Controller is running locally." : local.Detail,
                lan.Succeeded ? "Controller is reachable on the LAN URL." : $"LAN health failed: {lan.Detail}",
                "If LAN health fails while local health works, check controller bind mode, inbound TCP " + settings.Port + ", and the Windows network profile. Public WiFi or AP isolation can block workers."
            };

            SetStatus(lan.Succeeded || !urls.IsLanMode ? "LAN setup test completed." : "LAN setup needs attention.", string.Join(Environment.NewLine, details));
        }
        catch (Exception ex)
        {
            SetStatus("LAN setup test failed.", ex.Message);
        }
    }

    private async void TestWorkerConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            if (!string.Equals(settings.Role, "worker", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Switch to Worker mode.", "The worker connection test checks the URL path this worker will use.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(settings.ControllerUrl) && !LooksLikePlaceholderControllerUrl(settings.ControllerUrl))
            {
                var baseUrl = RenderFarmLauncher.EnsureTrailingSlash(settings.ControllerUrl);
                var healthUrl = RenderFarmLauncher.GetHealthUrl(baseUrl);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var result = await RenderFarmLauncher.CheckControllerIdentityAsync(client, healthUrl, CancellationToken.None);
                SetStatus(result.Succeeded ? "Manual controller URL is reachable." : "Manual controller URL failed.", result.Detail + Environment.NewLine + "Worker will use manual URL: " + baseUrl);
                return;
            }

            var path = settings.DiscoveryEnabled
                ? $"Worker will use UDP discovery on {settings.DiscoveryPort}, then LAN scan on controller port {settings.Port}, then localhost fallback."
                : $"Discovery is disabled. Worker will use LAN scan on controller port {settings.Port} if enabled, then localhost fallback.";
            SetStatus("Worker connection path.", path + Environment.NewLine + "Leave Controller URL blank for discovery/scan, or enter http://<controller-lan-ip>:9200/ for manual fallback.");
        }
        catch (Exception ex)
        {
            SetStatus("Worker connection test failed.", ex.Message);
        }
    }

    private void ConfigureControllerFirewall_Click(object sender, RoutedEventArgs e) => LaunchFirewallHelper("Controller");

    private void ConfigureWorkerFirewall_Click(object sender, RoutedEventArgs e) => LaunchFirewallHelper("Worker");

    private void LaunchFirewallHelper(string role)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            var script = FindInstallerScript("configure_controller_firewall.ps1");
            if (script is null)
            {
                ShowError("Firewall helper script was not found in the installed package or repository scripts folder.");
                return;
            }

            var args = $"-NoProfile -ExecutionPolicy Bypass -File {QuotePowerShell(script)} -Role {role} -Port {settings.Port} -DiscoveryPort {settings.DiscoveryPort} -Accept";
            Process.Start(new ProcessStartInfo("powershell.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            SetStatus($"{role} firewall helper requested.", "Approve the Windows elevation prompt to create or verify RenderFarm firewall rules.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private static bool LooksLikePlaceholderControllerUrl(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        if (string.Equals(trimmed, "CONTROLLER_IP", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "CONTROLLER_IP", StringComparison.OrdinalIgnoreCase);
    }
    private void InstallWorkerService_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            if (!string.Equals(settings.Role, "worker", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Switch to Worker mode before installing the worker service.");
                return;
            }

            if (!controllerVerified && !string.IsNullOrWhiteSpace(settings.ControllerUrl))
            {
                var proceed = MessageBox.Show(
                    "The controller has not been verified in this launcher session. Continue with service installation?",
                    "Install worker service",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            settings.Save(settingsPath);
            var script = FindInstallerScript("install_worker_service.ps1");
            if (script is null)
            {
                ShowError("The worker service installer was not found. Publish or install RenderFarm first, then open the launcher from the package root.");
                return;
            }

            var arguments = BuildWorkerServiceArguments(script, settings);
            Process.Start(new ProcessStartInfo("powershell.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            SetStatus("Worker service installer requested.", "Approve the Windows elevation prompt to continue.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        StopOwnedLaunch(activeWorkerLaunch, "worker", TimeSpan.FromSeconds(4));
        StopOwnedLaunch(activeControllerLaunch, "controller", TimeSpan.FromSeconds(4));
        activeWorkerLaunch = null;
        activeControllerLaunch = null;
    }

    private static void StopOwnedLaunch(RoleLaunchResult? launch, string label, TimeSpan timeout)
    {
        var process = launch?.Process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                LauncherRuntimeState.Remove(process.Id);
                process.Dispose();
                return;
            }

            LogLauncherEvent($"Stopping owned {label} process {process.Id}.");
            try
            {
                process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
            }

            if (!process.WaitForExit(timeout))
            {
                LogLauncherEvent($"Owned {label} process {process.Id} did not exit gracefully; killing process tree.");
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)TimeSpan.FromSeconds(2).TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogLauncherEvent($"Could not stop owned {label} process: {ex.Message}");
        }
        finally
        {
            LauncherRuntimeState.Remove(process.Id);
            process.Dispose();
        }
    }

    private static void LogLauncherEvent(string message)
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppContext.BaseDirectory;
            }

            var directory = Path.Combine(root, "RenderFarm");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "launcher.log"), $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
    private static string? BuildHealthUrl(LauncherSettings settings)
    {
        string? baseUrl = null;
        if (string.Equals(settings.Role, "worker", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = settings.ControllerUrl;
        }
        else if (RenderFarmLauncher.TryBuildDashboardUrl(settings, out var dashboardUrl, out _))
        {
            baseUrl = dashboardUrl;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return string.Equals(settings.Role, "controller", StringComparison.OrdinalIgnoreCase)
            ? RenderFarmLauncher.GetControllerHealthUrl(settings, baseUrl)
            : RenderFarmLauncher.GetHealthUrl(baseUrl);
    }

    private static string BuildWorkerServiceArguments(string scriptPath, LauncherSettings settings)
    {
        var builder = new StringBuilder();
        builder.Append("-NoProfile -ExecutionPolicy Bypass -File ").Append(QuotePowerShell(scriptPath));
        builder.Append(" -InstallRoot ").Append(QuotePowerShell(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        AddPowerShellValue(builder, "ControllerUrl", settings.ControllerUrl);
        AddPowerShellValue(builder, "WorkerId", settings.WorkerId);
        AddPowerShellValue(builder, "DisplayName", settings.DisplayName);
        AddPowerShellValue(builder, "ApiToken", settings.ApiToken);
        if (settings.DiscoveryEnabled) builder.Append(" -DiscoveryEnabled");
        if (settings.LanScanEnabled) builder.Append(" -LanScanEnabled");
        builder.Append(" -DiscoverySeconds ").Append(settings.DiscoverySeconds);
        builder.Append(" -DiscoveryPort ").Append(settings.DiscoveryPort);
        builder.Append(" -ControllerPort ").Append(settings.Port);
        builder.Append(" -LanScanTimeoutSeconds ").Append(settings.LanScanTimeoutSeconds);
        builder.Append(" -LanScanMaxHosts ").Append(settings.LanScanMaxHosts);
        AddPowerShellValue(builder, "UnrealSearchRoot", settings.UnrealSearchRoot);
        AddPowerShellValue(builder, "ProjectPath", settings.ProjectPath);
        AddPowerShellValue(builder, "SharedOutputRoot", settings.SharedOutputRoot);
        builder.Append(" -Start");
        return builder.ToString();
    }

    private static void AddPowerShellValue(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        builder.Append(" -").Append(name).Append(' ').Append(QuotePowerShell(value));
    }

    private static string QuotePowerShell(string value) => "\"" + value.Replace("\"", "`\"") + "\"";

    private static string? FindInstallerScript(string fileName)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "installer", fileName),
            Path.Combine(baseDirectory, "..", "installer", fileName),
            Path.Combine(baseDirectory, fileName)
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static string BuildControllerStartDetails(LauncherSettings settings, ControllerNetworkUrls urls)
    {
        var parts = new List<string>
        {
            $"Controller bind URL: {urls.BindUrl}",
            $"Open dashboard on this PC: {urls.LocalDashboardUrl}",
            $"Workers should use: {urls.LanWorkerUrl}",
            $"Database: {RenderFarmLauncher.GetDefaultControllerDatabasePath()}"
        };
        if (settings.DiscoveryEnabled)
        {
            parts.Add($"Discovery advertises: {urls.AdvertisedDiscoveryUrl}");
            parts.Add($"UDP discovery port: {settings.DiscoveryPort}");
        }
        if (urls.IsWildcardBind)
        {
            parts.Add("Bind host is 0.0.0.0, so the local dashboard URL and worker LAN URL are intentionally different.");
        }

        return string.Join(Environment.NewLine, parts);
    }
    private async void TestJobDispatch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            var baseUrl = settings.Role == "worker" && !string.IsNullOrWhiteSpace(settings.ControllerUrl) && !LooksLikePlaceholderControllerUrl(settings.ControllerUrl)
                ? RenderFarmLauncher.EnsureTrailingSlash(settings.ControllerUrl)
                : RenderFarmLauncher.GetControllerBrowseUrl(settings, RenderFarmLauncher.GetDashboardUrl(settings));
            baseUrl = RenderFarmLauncher.EnsureTrailingSlash(baseUrl);
            var endpoint = new Uri(new Uri(baseUrl), "api/diagnostics/dispatch");
            SetStatus("Checking job dispatch...", endpoint.ToString());

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(endpoint, CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                SetStatus("Dispatch diagnostics failed.", $"HTTP {(int)response.StatusCode} from {endpoint}: {TrimForStatus(body)}");
                return;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var queuedCount = GetArrayLength(root, "queuedJobs");
            var workerCount = GetArrayLength(root, "workers");
            var incompatibleCount = CountIncompatibleWorkers(root);
            var warnings = ReadStringArray(root, "configWarnings");
            var latestDecision = ReadLatestDecision(root);
            var details = new List<string>
            {
                $"Endpoint: {endpoint}",
                $"Queued jobs: {queuedCount}",
                $"Workers: {workerCount}",
                $"Incompatible workers: {incompatibleCount}",
                latestDecision is null ? "Latest scheduler decision: none recorded since controller start" : $"Latest scheduler decision: {latestDecision}"
            };
            details.AddRange(warnings.Select(warning => "Warning: " + warning));

            var headline = queuedCount > 0 && workerCount == 0
                ? "Dispatch blocked: no workers."
                : incompatibleCount > 0
                    ? "Dispatch blocked by worker version."
                    : warnings.Count > 0
                        ? "Dispatch diagnostics need attention."
                        : "Dispatch diagnostics look usable.";
            SetStatus(headline, string.Join(Environment.NewLine, details));
        }
        catch (Exception ex)
        {
            SetStatus("Dispatch diagnostics failed.", ex.Message);
        }
    }
    private void StopTrackedProcesses_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "Stop tracked RenderFarm controller and worker processes launched by this app? Unrelated Unreal, dotnet, and editor processes will not be touched.",
            "Stop RenderFarm processes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var stopped = LauncherRuntimeState.StopTrackedProcesses(TimeSpan.FromSeconds(4));
        activeControllerLaunch = null;
        activeWorkerLaunch = null;
        SetStatus("Tracked RenderFarm processes stopped.", $"Stopped {stopped} tracked process(es). Any unrelated processes were left alone.");
    }

    private void ClearStaleRuntime_Click(object sender, RoutedEventArgs e)
    {
        var removed = LauncherRuntimeState.ClearStale();
        SetStatus("Stale runtime state cleared.", $"Removed {removed} stale runtime record(s). Running tracked processes remain registered.");
    }
    private void SetStatus(string headline, string? details = null)
    {
        if (StatusHeadlineText is not null)
        {
            StatusHeadlineText.Text = headline;
        }

        if (StatusDetailsText is not null)
        {
            StatusDetailsText.Text = details ?? string.Empty;
        }
    }

    private static string TrimOrDefault(string value, string fallback)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    private static string? TrimOrNull(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static int GetArrayLength(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    private static int CountIncompatibleWorkers(JsonElement root)
    {
        if (!root.TryGetProperty("workers", out var workers) || workers.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var count = 0;
        foreach (var worker in workers.EnumerateArray())
        {
            if (worker.TryGetProperty("compatibility", out var compatibility)
                && compatibility.TryGetProperty("compatible", out var compatible)
                && compatible.ValueKind == JsonValueKind.False)
            {
                count++;
            }
        }

        return count;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray();
    }

    private static string? ReadLatestDecision(JsonElement root)
    {
        if (!root.TryGetProperty("recentDecisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var latest = decisions.EnumerateArray().FirstOrDefault();
        if (latest.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var decision = latest.TryGetProperty("decision", out var decisionValue) ? decisionValue.GetString() : null;
        var reason = latest.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
        return string.Join(" - ", new[] { decision, reason }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string TrimForStatus(string? value, int maxCharacters = 1200)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxCharacters ? value : value[..maxCharacters] + "...";
    }
    private void ShowError(string message)
    {
        SetStatus("RenderFarm Launcher needs attention.", message);
        MessageBox.Show(this, message, "RenderFarm Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}



















