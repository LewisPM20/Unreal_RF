using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RenderFarm.Launcher;

internal static class RenderFarmLauncher
{
    private const string ControllerRole = "controller";
    private const string WorkerRole = "worker";

    private static readonly JsonSerializerOptions ConsoleJsonOptions = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        var options = LauncherOptions.Parse(args);
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
        var host = string.IsNullOrWhiteSpace(settings.HostName) ? "127.0.0.1" : settings.HostName.Trim();
        var port = settings.Port is >= 1 and <= 65535 ? settings.Port : 9200;
        return $"http://{host}:{port}/";
    }

    public static int StartRole(LauncherSettings settings, bool waitForExit = true)
    {
        if (!IsValidRole(settings.Role))
        {
            Console.Error.WriteLine("RenderFarm role must be 'controller' or 'worker'.");
            return 2;
        }

        var role = settings.Role!.Trim().ToLowerInvariant();
        var exe = FindRuntimeExecutable(role);
        if (exe is null)
        {
            Console.Error.WriteLine($"Could not find the RenderFarm {role} executable. Expected a published package with '{role}' beside the launcher.");
            return 3;
        }

        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
        };

        if (role == ControllerRole)
        {
            var url = GetDashboardUrl(settings).TrimEnd('/');
            startInfo.ArgumentList.Add("--urls");
            startInfo.ArgumentList.Add(url);
            if (!string.IsNullOrWhiteSpace(settings.ApiToken))
            {
                startInfo.Environment["RenderFarm__Security__ApiToken"] = settings.ApiToken;
            }

            Console.WriteLine($"Starting RenderFarm controller at {url}");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.ControllerUrl)) startInfo.Environment["RenderFarm__ControllerUrl"] = settings.ControllerUrl;
            if (!string.IsNullOrWhiteSpace(settings.WorkerId)) startInfo.Environment["RenderFarm__WorkerId"] = settings.WorkerId;
            if (!string.IsNullOrWhiteSpace(settings.DisplayName)) startInfo.Environment["RenderFarm__DisplayName"] = settings.DisplayName;
            if (!string.IsNullOrWhiteSpace(settings.ApiToken)) startInfo.Environment["RenderFarm__ApiToken"] = settings.ApiToken;
            if (!string.IsNullOrWhiteSpace(settings.ProjectPath)) startInfo.Environment["RenderFarm__ProjectPaths__0"] = settings.ProjectPath;
            if (!string.IsNullOrWhiteSpace(settings.SharedOutputRoot)) startInfo.Environment["RenderFarm__SharedOutputRoots__0"] = settings.SharedOutputRoot;
            if (!string.IsNullOrWhiteSpace(settings.UnrealSearchRoot)) startInfo.Environment["RenderFarm__UnrealSearchRoots__0"] = settings.UnrealSearchRoot;
            startInfo.Environment["RenderFarm__DiscoveryEnabled"] = settings.DiscoveryEnabled.ToString();
            startInfo.Environment["RenderFarm__DiscoverySeconds"] = settings.DiscoverySeconds.ToString();
            startInfo.Environment["RenderFarm__DiscoveryPort"] = settings.DiscoveryPort.ToString();
            Console.WriteLine($"Starting RenderFarm worker for {GetWorkerControllerLabel(settings)}");
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine($"Failed to start {exe}.");
            return 4;
        }

        if (!waitForExit)
        {
            return 0;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    public static string? FindRuntimeExecutable(string role)
    {
        var fileName = role == ControllerRole ? ExecutableName("RenderFarm.Controller.Api") : ExecutableName("RenderFarm.Worker.Agent");
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, role, fileName),
            Path.Combine(baseDirectory, "..", role, fileName),
            Path.Combine(baseDirectory, fileName)
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static string GetWorkerControllerLabel(LauncherSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ControllerUrl)) return settings.ControllerUrl;
        if (settings.DiscoveryEnabled) return "LAN discovery, then local fallback";
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
        Console.WriteLine("  RenderFarm.Launcher --role controller --save --host 127.0.0.1 --port 9200");
        Console.WriteLine("  RenderFarm.Launcher --role worker --save --controller-url http://CONTROLLER_IP:9200 --worker-id worker-01");
        Console.WriteLine("  RenderFarm.Launcher --role worker --save --discovery --unreal-search-root \"C:\\Program Files\\Epic Games\" --project-path D:\\Project\\Project.uproject --shared-output-root \\\\SERVER\\RenderOutput");
        Console.WriteLine("  RenderFarm.Launcher --show-config");
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
        return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), LauncherJson.Options);
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

    public static LauncherOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var item = args[i];
            if (!item.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = item[2..];
            if (key is "save" or "show-config" or "help" or "discovery" or "no-discovery")
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
            SharedOutputRoot = Get(values, "shared-output-root")
        };
    }

    private static bool? ParseDiscovery(IReadOnlySet<string> flags, IReadOnlyDictionary<string, string?> values)
    {
        if (flags.Contains("discovery")) return true;
        if (flags.Contains("no-discovery")) return false;
        return bool.TryParse(Get(values, "discovery-enabled"), out var enabled) ? enabled : null;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) => values.TryGetValue(key, out var value) ? value : null;
}


