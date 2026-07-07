using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RenderFarm.Shared;

/// <summary>
/// Shared RenderFarm product and API contract version metadata.
/// </summary>
public static partial class RenderFarmVersion
{
    public const string ProductVersion = "0.16.0";
    public const int ProtocolVersion = 3;
    public const int ApiContractVersion = 3;
    public const string MinimumWorkerProductVersion = "0.16.0";
    public const int MinimumWorkerProtocolVersion = 3;
    public const int MinimumWorkerApiContractVersion = 3;

    public static string BuildId => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? ProductVersion;

    public static VersionCompatibility EvaluateWorker(string? productVersion, int? protocolVersion, int? apiContractVersion)
    {
        var normalizedProductVersion = NormalizeProductVersion(productVersion);
        if (!TryParseVersion(normalizedProductVersion, out var workerVersion))
        {
            return VersionCompatibility.Incompatible(
                normalizedProductVersion,
                protocolVersion,
                apiContractVersion,
                $"Worker did not report a usable product version. Controller requires {MinimumWorkerProductVersion}/protocol {MinimumWorkerProtocolVersion}.");
        }

        if (workerVersion < new Version(MinimumWorkerProductVersion))
        {
            return VersionCompatibility.Incompatible(
                normalizedProductVersion,
                protocolVersion,
                apiContractVersion,
                $"Worker is running {normalizedProductVersion}, but controller requires {MinimumWorkerProductVersion}/protocol {MinimumWorkerProtocolVersion}. Reinstall or update the worker.");
        }

        if ((protocolVersion ?? 0) < MinimumWorkerProtocolVersion)
        {
            return VersionCompatibility.Incompatible(
                normalizedProductVersion,
                protocolVersion,
                apiContractVersion,
                $"Worker protocol {protocolVersion?.ToString() ?? "missing"} is incompatible. Controller requires protocol {MinimumWorkerProtocolVersion}.");
        }

        if ((apiContractVersion ?? 0) < MinimumWorkerApiContractVersion)
        {
            return VersionCompatibility.Incompatible(
                normalizedProductVersion,
                protocolVersion,
                apiContractVersion,
                $"Worker API contract {apiContractVersion?.ToString() ?? "missing"} is incompatible. Controller requires contract {MinimumWorkerApiContractVersion}.");
        }

        return VersionCompatibility.CreateCompatible(normalizedProductVersion, protocolVersion, apiContractVersion);
    }

    public static VersionCompatibility EvaluateWorkerAgent(string? agentVersion)
    {
        var parsed = ParseWorkerAgentVersion(agentVersion);
        return EvaluateWorker(parsed.ProductVersion, parsed.ProtocolVersion, parsed.ApiContractVersion);
    }

    public static WorkerVersionInfo ParseWorkerAgentVersion(string? agentVersion)
    {
        if (string.IsNullOrWhiteSpace(agentVersion))
        {
            return new(null, null, null, null);
        }

        var text = agentVersion.Trim();
        return new(
            NormalizeProductVersion(text),
            TryReadInt(text, "protocol"),
            TryReadInt(text, "contract"),
            TryReadString(text, "build"));
    }

    public static string FormatWorkerAgentVersion(string? productVersion, int? protocolVersion, int? apiContractVersion, string? buildId)
    {
        var parts = new List<string> { NormalizeProductVersion(productVersion) };
        if (protocolVersion is not null)
        {
            parts.Add($"protocol={protocolVersion.Value}");
        }

        if (apiContractVersion is not null)
        {
            parts.Add($"contract={apiContractVersion.Value}");
        }

        if (!string.IsNullOrWhiteSpace(buildId))
        {
            parts.Add($"build={buildId.Trim()}");
        }

        return string.Join("; ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public static string NormalizeProductVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = ProductVersionPattern().Match(value.Trim());
        return match.Success ? match.Value : value.Trim();
    }

    private static bool TryParseVersion(string? value, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Count(ch => ch == '.') == 1 ? value + ".0" : value;
        return Version.TryParse(normalized, out version);
    }

    private static int? TryReadInt(string value, string key)
    {
        var match = Regex.Match(value, $@"(?:^|[;\s,]){Regex.Escape(key)}\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : null;
    }

    private static string? TryReadString(string value, string key)
    {
        var match = Regex.Match(value, $@"(?:^|[;\s,]){Regex.Escape(key)}\s*=\s*([^;,\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex ProductVersionPattern();
}

/// <summary>
/// Parsed version fields reported by a worker agent.
/// </summary>
public sealed record WorkerVersionInfo(string? ProductVersion, int? ProtocolVersion, int? ApiContractVersion, string? BuildId);

/// <summary>
/// Result of checking whether a worker can speak the active controller contract.
/// </summary>
public sealed record VersionCompatibility(
    bool Compatible,
    string? ProductVersion,
    int? ProtocolVersion,
    int? ApiContractVersion,
    string Reason)
{
    public static VersionCompatibility CreateCompatible(string? productVersion, int? protocolVersion, int? apiContractVersion) =>
        new(true, productVersion, protocolVersion, apiContractVersion, "Worker version is compatible.");

    public static VersionCompatibility Incompatible(string? productVersion, int? protocolVersion, int? apiContractVersion, string reason) =>
        new(false, productVersion, protocolVersion, apiContractVersion, reason);
}

