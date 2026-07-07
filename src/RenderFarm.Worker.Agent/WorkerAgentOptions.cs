namespace RenderFarm.Worker.Agent;

/// <summary>
/// Configuration for the C# worker agent shell.
/// </summary>
public sealed class WorkerAgentOptions
{
    public string? WorkerId { get; set; }
    public string? DisplayName { get; set; }
    public string? IdentityFilePath { get; set; }
    public bool DiscoveryEnabled { get; set; } = false;
    public int DiscoverySeconds { get; set; } = 5;
    public int DiscoveryPort { get; set; } = 39200;
    public int ControllerPort { get; set; } = 9200;
    public bool LanScanEnabled { get; set; } = true;
    public int LanScanTimeoutSeconds { get; set; } = 4;
    public int LanScanMaxHosts { get; set; } = 254;
    public string? LastKnownControllerPath { get; set; }
    public string ControllerUrl { get; set; } = string.Empty;
    public string? ApiToken { get; set; }
    public string ServiceUrl { get; set; } = "http://127.0.0.1:9100";
    public int HeartbeatSeconds { get; set; } = 5;
    public int JobPollingSeconds { get; set; } = 5;
    public int LeaseRenewalSeconds { get; set; } = 30;
    public int RenderTimeoutSeconds { get; set; } = 0;
    public string[] ProjectPaths { get; set; } = [];
    public string[] SharedOutputRoots { get; set; } = [];
    public string[] UnrealSearchRoots { get; set; } = [];
    public string? AttemptLogRoot { get; set; }
    public string? WorkerStateFilePath { get; set; }
    public bool AllowDuplicateWorkerInstance { get; set; } = false;
}


