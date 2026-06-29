using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;

namespace RenderFarm.Launcher;

public partial class MainWindow : Window
{
    private readonly string settingsPath = RenderFarmLauncher.ResolveSettingsPath(null);
    private RoleLaunchResult? activeControllerLaunch;
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
        controllerVerified = false;
        SetStatus(isWorker
            ? "Worker mode selected."
            : "Controller mode selected.",
            isWorker
                ? "Enter the controller URL or enable LAN discovery, then start this machine as a render worker."
                : "Start the controller dashboard, then open it in your browser when the launcher reports it is ready.");
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
                if (!RenderFarmLauncher.TryBuildDashboardUrl(settings, out var dashboardUrl, out var error))
                {
                    SetStatus("Controller dashboard failed.", error);
                    return;
                }

                SetStatus("Starting controller dashboard...", $"Binding to {dashboardUrl} and using database {RenderFarmLauncher.GetDefaultControllerDatabasePath()}");
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
                    SetStatus("Controller dashboard ready!", $"Open {launch.DashboardUrl}");
                    return;
                }

                controllerVerified = false;
                SetStatus("Controller dashboard failed.", readiness.Detail);
                return;
            }

            SetStatus("Starting worker...", "The worker will appear in the controller dashboard after it heartbeats.");
            var workerLaunch = RenderFarmLauncher.StartRoleDetailed(settings, captureOutput: false);
            if (workerLaunch.Started)
            {
                SetStatus("Worker start requested.", "Watch the controller dashboard for pending approval, heartbeat, and capability status.");
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
                : RenderFarmLauncher.GetDashboardUrl(settings);

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SetStatus("Dashboard opened.", url);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
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

        return RenderFarmLauncher.GetHealthUrl(baseUrl);
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
        AddPowerShellValue(builder, "UnrealSearchRoot", settings.UnrealSearchRoot);
        AddPowerShellValue(builder, "ProjectPath", settings.ProjectPath);
        AddPowerShellValue(builder, "SharedOutputRoot", settings.SharedOutputRoot);
        if (settings.DiscoveryEnabled) builder.Append(" -DiscoveryEnabled");
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

    private void ShowError(string message)
    {
        SetStatus("RenderFarm Launcher needs attention.", message);
        MessageBox.Show(this, message, "RenderFarm Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}


