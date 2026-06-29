using Xunit;

namespace RenderFarm.Tests;

public sealed class LaunchScriptAssetTests
{
    [Fact]
    public void DeveloperLaunchScriptsExistAndUseCSharpEntrypoints()
    {
        var repoRoot = FindRepoRoot();
        var scripts = Path.Combine(repoRoot, "scripts");
        var controller = Path.Combine(scripts, "start_controller.ps1");
        var worker = Path.Combine(scripts, "start_worker.ps1");
        var controllerAlias = Path.Combine(scripts, "start-controller.ps1");
        var workerAlias = Path.Combine(scripts, "start-worker.ps1");
        var roleLauncher = Path.Combine(scripts, "start-renderfarm.ps1");

        Assert.True(File.Exists(controller), $"Controller launch script was not found at {controller}");
        Assert.True(File.Exists(worker), $"Worker launch script was not found at {worker}");
        Assert.True(File.Exists(controllerAlias), $"Controller alias script was not found at {controllerAlias}");
        Assert.True(File.Exists(workerAlias), $"Worker alias script was not found at {workerAlias}");
        Assert.True(File.Exists(roleLauncher), $"Role launcher script was not found at {roleLauncher}");

        var controllerText = File.ReadAllText(controller);
        var workerText = File.ReadAllText(worker);
        var roleText = File.ReadAllText(roleLauncher);

        Assert.Contains("RenderFarm.Controller.Api.csproj", controllerText);
        Assert.Contains("RenderFarm.Worker.Agent.csproj", workerText);
        Assert.Contains("Assert-DotNetCli", controllerText);
        Assert.Contains("Assert-DotNetCli", workerText);
        Assert.Contains("RenderFarm__ControllerUrl", workerText);
        Assert.Contains("RenderFarm__ProjectPaths__", workerText);
        Assert.Contains("RenderFarm__SharedOutputRoots__", workerText);
        Assert.Contains("RenderFarm__UnrealSearchRoots__", workerText);
        Assert.Contains("RenderFarm__Security__ApiToken", controllerText);
        Assert.Contains("RenderFarm__ApiToken", workerText);
        Assert.Contains("app-role.json", roleText);
        Assert.Contains("& $script -HostName $HostName -Port $Port", roleText);
        Assert.Contains("controller", roleText);
        Assert.Contains("worker", roleText);
    }

    [Fact]
    public void ProductPackagingAssetsExist()
    {
        var repoRoot = FindRepoRoot();
        var scripts = Path.Combine(repoRoot, "scripts");
        var packaging = Path.Combine(repoRoot, "packaging", "config");
        var installer = Path.Combine(repoRoot, "installer");
        var assets = Path.Combine(repoRoot, "packaging", "assets");
        var launcherRoot = Path.Combine(repoRoot, "src", "RenderFarm.Launcher");
        var launcherProject = Path.Combine(launcherRoot, "RenderFarm.Launcher.csproj");
        var launcherCore = Path.Combine(launcherRoot, "LauncherCore.cs");
        var launcherWindow = Path.Combine(launcherRoot, "MainWindow.xaml");

        Assert.True(File.Exists(launcherProject), $"Launcher project was not found at {launcherProject}");
        Assert.True(File.Exists(launcherCore), $"Launcher core was not found at {launcherCore}");
        Assert.True(File.Exists(Path.Combine(launcherRoot, "App.xaml")));
        Assert.True(File.Exists(launcherWindow));
        Assert.True(File.Exists(Path.Combine(scripts, "install_renderfarm.ps1")));
        Assert.True(File.Exists(Path.Combine(scripts, "uninstall_renderfarm.ps1")));
        Assert.True(File.Exists(Path.Combine(scripts, "install_worker_service.ps1")));
        Assert.True(File.Exists(Path.Combine(scripts, "uninstall_worker_service.ps1")));
        Assert.True(File.Exists(Path.Combine(scripts, "configure_controller_firewall.ps1")));
        Assert.True(File.Exists(Path.Combine(scripts, "verify_upgrade_settings.ps1")));
        Assert.True(File.Exists(Path.Combine(scripts, "build_installer.ps1")));
        Assert.True(File.Exists(Path.Combine(scripts, "sign_release.ps1")));
        Assert.True(File.Exists(Path.Combine(packaging, "controller.appsettings.template.json")));
        Assert.True(File.Exists(Path.Combine(packaging, "worker.appsettings.template.json")));
        Assert.True(File.Exists(Path.Combine(packaging, "app-role.controller.template.json")));
        Assert.True(File.Exists(Path.Combine(packaging, "app-role.worker.template.json")));
        Assert.True(File.Exists(Path.Combine(installer, "RenderFarm.iss")));
        Assert.True(File.Exists(Path.Combine(installer, "redist", "README.md")));
        Assert.True(File.Exists(Path.Combine(assets, "renderfarm.ico")));
        Assert.True(File.Exists(Path.Combine(repoRoot, ".github", "workflows", "package-renderfarm.yml")));
        Assert.True(File.Exists(Path.Combine(repoRoot, "docs", "installer-smoke-checklist.md")));

        var projectText = File.ReadAllText(launcherProject);
        var launcherText = File.ReadAllText(launcherCore);
        var windowText = File.ReadAllText(launcherWindow);
        var publishText = File.ReadAllText(Path.Combine(scripts, "publish_apps.ps1"));
        var serviceText = File.ReadAllText(Path.Combine(scripts, "install_worker_service.ps1"));
        var installerText = File.ReadAllText(Path.Combine(scripts, "build_installer.ps1"));
        var setupText = File.ReadAllText(Path.Combine(installer, "RenderFarm.iss"));
        var firewallText = File.ReadAllText(Path.Combine(scripts, "configure_controller_firewall.ps1"));
        var signingText = File.ReadAllText(Path.Combine(scripts, "sign_release.ps1"));

        Assert.Contains("net8.0-windows", projectText);
        Assert.Contains("<UseWPF>true</UseWPF>", projectText);
        Assert.Contains("ApplicationIcon", projectText);
        Assert.Contains("--role controller", launcherText);
        Assert.Contains("--role worker", launcherText);
        Assert.Contains("unreal-search-root", launcherText);
        Assert.Contains("ProjectPathBox", windowText);
        Assert.Contains("SharedOutputRootBox", windowText);
        Assert.Contains("RenderFarm.Launcher.csproj", publishText);
        Assert.Contains("net8.0-windows", publishText);
        Assert.Contains("framework-dependent", publishText);
        Assert.Contains("DebugType=None", publishText);
        Assert.Contains("DebugSymbols=false", publishText);
        Assert.Contains("install_renderfarm.ps1", publishText);
        Assert.Contains("configure_controller_firewall.ps1", publishText);
        Assert.Contains("sign_release.ps1", publishText);
        Assert.Contains("AllowBuildOutputFallback", publishText);
        Assert.Contains("RenderFarm__ProjectPaths__0", serviceText);
        Assert.Contains("RenderFarm__SharedOutputRoots__0", serviceText);
        Assert.Contains("RenderFarm__UnrealSearchRoots__0", serviceText);
        Assert.Contains("ISCC.exe", installerText);
        Assert.Contains("RenderFarm.iss", installerText);
        Assert.Contains("windowsdesktop-runtime-8.", installerText);
        Assert.Contains("SelfContained=false", installerText);
        Assert.Contains("Microsoft.WindowsDesktop.App", setupText);
        Assert.Contains("/install /quiet /norestart", setupText);
        Assert.Contains("RenderFarm.Launcher.exe", setupText);
        Assert.Contains("desktopicon", setupText);
        Assert.Contains("-Accept", firewallText);
        Assert.Contains("New-NetFirewallRule", firewallText);
        Assert.Contains("signtool.exe", signingText);
        Assert.Contains("CertificateThumbprint", signingText);
    }

    [Fact]
    public void PackagingPlanDocumentsCurrentExecutableDirection()
    {
        var repoRoot = FindRepoRoot();
        var plan = Path.Combine(repoRoot, "docs", "packaging-plan.md");
        Assert.True(File.Exists(plan), $"Packaging plan was not found at {plan}");

        var text = File.ReadAllText(plan);
        Assert.Contains("separate Controller and Worker executables", text);
        Assert.Contains("single role-selecting launcher", text);
        Assert.Contains("WPF launcher", text);
        Assert.Contains("RenderFarm.Launcher", text);
        Assert.Contains("install_renderfarm.ps1", text);
        Assert.Contains("Windows Service", text);
        Assert.Contains("build_installer.ps1", text);
        Assert.Contains("Inno Setup", text);
        Assert.Contains("Product flow coverage", text);
        Assert.Contains("setup EXE", text);
        Assert.Contains("configure_controller_firewall.ps1", text);
        Assert.Contains("verify_upgrade_settings.ps1", text);
        Assert.Contains("installer\\redist\\windowsdesktop-runtime-8.x.x-win-x64.exe", text);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RenderFarm.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root containing RenderFarm.sln.");
    }
}