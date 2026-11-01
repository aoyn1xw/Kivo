using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Kivo.Windows.Services;

public enum FirewallRuleState
{
    Ready,
    Missing,
    Mismatched,
    BlockedByNetworkProfile,
    Unavailable,
}

public sealed record FirewallStatus(
    FirewallRuleState State,
    string Detail,
    bool PrivateNetworkActive)
{
    public bool NeedsSetup => State is not FirewallRuleState.Ready;
}

/// <summary>
/// A normalized view of one firewall rule owned by this app (its legacy
/// ShadowPlay name or a per-port name), captured from the Windows Firewall COM
/// API so the evaluation logic can also run in unit tests without COM.
/// </summary>
public sealed record FirewallRuleSnapshot(
    string Name,
    IReadOnlyList<int> LocalPorts,
    string ApplicationPath,
    bool Enabled,
    int Direction,
    int Action,
    int Protocol,
    int ProfileMask);

public interface IWindowsFirewallService
{
    FirewallStatus Check(string executablePath, int port);

    Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, int port);

    /// <summary>Checks one private-profile TCP rule per requested port.</summary>
    FirewallStatus Check(string executablePath, IReadOnlyCollection<int> ports);

    /// <summary>
    /// Ensures one private-profile inbound TCP rule per requested port
    /// (e.g. the HTTP listener and the pinned-TLS listener).
    /// </summary>
    Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, IReadOnlyCollection<int> ports);
}

/// <summary>
/// Checks and explicitly provisions one inbound TCP rule per port for the current
/// Kivo executable. Rules are private-profile-only; this service never enables
/// Windows Firewall or opens a public-profile port.
/// </summary>
public sealed class WindowsFirewallService(ILogger<WindowsFirewallService>? logger = null)
    : IWindowsFirewallService
{
    /// <summary>Legacy single-port rule name (kept so upgrades replace it).</summary>
    public const string RuleName = "ShadowPlay LAN API (Private)";

    /// <summary>Per-port rules are named `ShadowPlay LAN API (Private) TCP {port}`.</summary>
    public const string RuleNamePrefix = "ShadowPlay LAN API (Private) TCP ";

    internal const int Inbound = 1;
    internal const int Tcp = 6;
    internal const int Allow = 1;
    internal const int PrivateProfile = 2;

    public FirewallStatus Check(string executablePath, int port)
        => Check(executablePath, [port]);

    public FirewallStatus Check(string executablePath, IReadOnlyCollection<int> ports)
    {
        if (ports.Count == 0)
        {
            return new(
                FirewallRuleState.Unavailable,
                "No listener ports are configured for the sharing service.",
                false);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new(
                FirewallRuleState.Unavailable,
                "Windows Firewall checks are only available on Windows.",
                false);
        }

        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            dynamic policy = Activator.CreateInstance(policyType)!;
            try
            {
                // CurrentProfileTypes is a bitmask across every connected adapter.
                // A disconnected Private VPN/Tailscale adapter must not make a
                // Public Wi-Fi profile look safe for a Private-only rule.
                var privateActive = IsActivePrivateNetworkProfile();
                return AnalyzeRules(
                    NormalizePath(executablePath),
                    ports,
                    CollectRuleSnapshots(policy.Rules),
                    privateActive);
            }
            finally
            {
                ReleaseCom(policy);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger?.LogWarning(ex, "Unable to inspect the Windows Firewall rule");
            return new(
                FirewallRuleState.Unavailable,
                "Windows Firewall status could not be inspected. Run Kivo as administrator or check the rule manually.",
                false);
        }
    }

    public Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, int port)
        => EnsurePrivateRuleAsync(executablePath, [port]);

    public async Task<FirewallStatus> EnsurePrivateRuleAsync(string executablePath, IReadOnlyCollection<int> ports)
    {
        if (ports.Count == 0)
        {
            return new(
                FirewallRuleState.Unavailable,
                "No listener ports are configured for the sharing service.",
                false);
        }

        if (!OperatingSystem.IsWindows())
        {
            return Check(executablePath, ports);
        }

        try
        {
            // Rules owned by this app (the shared ShadowPlay-named rules) that
            // no longer serve the current listeners - typically pre-rebrand
            // rules still pointing at ShadowPlay.exe - are removed by the same
            // elevated script that recreates the needed rules, so the rebrand
            // never leaves stale allow entries behind.
            var staleRuleNames = TryCollectStaleRuleNames(ports);
            var script = BuildRuleScript(NormalizePath(executablePath), ports, staleRuleNames);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return new(FirewallRuleState.Unavailable, "Windows Firewall setup could not be started.", false);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new(
                    FirewallRuleState.Unavailable,
                    "Windows denied firewall setup. Approve the administrator prompt or add the private rule manually.",
                    false);
            }

            return Check(executablePath, ports);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger?.LogWarning(ex, "Windows Firewall setup was cancelled or failed");
            return new(
                FirewallRuleState.Unavailable,
                "Windows denied firewall setup. Approve the administrator prompt or add the private rule manually.",
                false);
        }
    }

    internal static string BuildRuleScript(
        string executablePath,
        IReadOnlyCollection<int> ports,
        IReadOnlyCollection<string>? extraRuleNamesToDelete = null)
    {
        var safePath = executablePath.Replace("'", "''", StringComparison.Ordinal);
        var builder = new StringBuilder();
        builder.Append("$ErrorActionPreference='Stop'; ");
#pragma warning disable CA1305
        // Replace-or-add per port, and clear the legacy single-port rule so the
        // old port is not left open after the HTTPS listener exists.
        builder.Append($"netsh advfirewall firewall delete rule name='{RuleName}' | Out-Null; ");
        if (extraRuleNamesToDelete is not null)
        {
            foreach (var staleName in extraRuleNamesToDelete.Where(IsAppOwnedRuleName).Distinct(StringComparer.Ordinal))
            {
                var safeName = staleName.Replace("'", "''", StringComparison.Ordinal);
                builder.Append($"netsh advfirewall firewall delete rule name='{safeName}' | Out-Null; ");
            }
        }

        foreach (var port in ports.Distinct())
        {
            var safePort = port.ToString(CultureInfo.InvariantCulture);
            var ruleName = RuleNameFromPort(port);
            builder.Append($"netsh advfirewall firewall delete rule name='{ruleName}' | Out-Null; ");
            builder.Append(
                $"netsh advfirewall firewall add rule name='{ruleName}' dir=in action=allow "
                + $"protocol=TCP localport={safePort} profile=private program='{safePath}' enable=yes | Out-Null; ");
        }

        builder.Append("exit $LASTEXITCODE");
#pragma warning restore CA1305
        return builder.ToString();
    }

    internal static string RuleNameFromPort(int port) =>
        $"{RuleNamePrefix}{port.ToString(CultureInfo.InvariantCulture)}";

    private bool IsActivePrivateNetworkProfile()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"@("
                    + "Get-NetConnectionProfile | Where-Object { "
                    + "$_.NetworkCategory -eq 'Private' -and "
                    + "$_.IPv4Connectivity -ne 'NoTraffic' "
                    + "}).Count -gt 0\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });

            if (process is null || !process.WaitForExit(3000))
            {
                try
                {
                    process?.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort timeout cleanup; the status remains unavailable.
                }

                return false;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0
                && output.Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger?.LogWarning(ex, "Unable to inspect the active Windows network profile");
            return false;
        }
    }

    /// <summary>True for the legacy single-port name and every per-port name.</summary>
    internal static bool IsAppOwnedRuleName(string? name)
    {
        if (string.Equals(name, RuleName, StringComparison.Ordinal))
        {
            return true;
        }

        if (name is null || !name.StartsWith(RuleNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name.AsSpan(RuleNamePrefix.Length);
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535
            && string.Equals(name, RuleNameFromPort(port), StringComparison.Ordinal);
    }

    /// <summary>
    /// Pure evaluation of the requested listener ports against app-owned rules.
    /// A rule that satisfies every restriction except the program path is a
    /// pre-rebrand leftover: it still points at the previous ShadowPlay.exe and
    /// is reported with explicit upgrade guidance until it is migrated.
    /// </summary>
    internal static FirewallStatus AnalyzeRules(
        string expectedPath,
        IReadOnlyCollection<int> ports,
        IReadOnlyList<FirewallRuleSnapshot> rules,
        bool privateActive)
    {
        var matched = new HashSet<int>();
        var legacyPaths = new HashSet<int>();
        var stale = new HashSet<int>();

        foreach (var port in ports)
        {
            foreach (var rule in rules)
            {
                if (!rule.LocalPorts.Contains(port))
                {
                    continue;
                }

                if (IsQualifiedRule(rule))
                {
                    if (SameProgramPath(rule.ApplicationPath, expectedPath))
                    {
                        matched.Add(port);
                    }
                    else
                    {
                        legacyPaths.Add(port);
                    }
                }
                else
                {
                    stale.Add(port);
                }
            }
        }

        var missing = ports.Where(port => !matched.Contains(port)).ToList();
        if (missing.Count == 0)
        {
            var portList = string.Join(", ", ports);
            if (privateActive)
            {
                return new(
                    FirewallRuleState.Ready,
                    $"Private firewall rules are active for TCP {portList}.",
                    true);
            }

            return new(
                FirewallRuleState.BlockedByNetworkProfile,
                "The rules are private-only, but the active Wi-Fi network is not marked Private.",
                false);
        }

        var detail = new StringBuilder();
        detail.Append(CultureInfo.InvariantCulture, $"No private-only Kivo firewall rules cover TCP {string.Join(", ", missing)}.");
        if (legacyPaths.Count > 0)
        {
            detail.Append(
                CultureInfo.InvariantCulture,
                $" Rules for TCP {string.Join(", ", legacyPaths)} still point at the previous ShadowPlay installation; click the button and approve the administrator prompt to update them for Kivo.");
        }

        var otherStale = stale.Except(legacyPaths).ToList();
        if (otherStale.Count > 0)
        {
            detail.Append(
                CultureInfo.InvariantCulture,
                $" Stale rules exist for TCP {string.Join(", ", otherStale)}.");
        }

        return new(FirewallRuleState.Mismatched, detail.ToString(), privateActive);
    }

    /// <summary>
    /// Names of app-owned rules that are not part of the requested per-port set
    /// and should be deleted by the same script: legacy ShadowPlay-path rules
    /// and rules for retired ports. The legacy base name is always deleted by
    /// the base script, so it is not repeated here.
    /// </summary>
    internal static IReadOnlyList<string> CollectRuleNamesToDelete(
        IReadOnlyList<FirewallRuleSnapshot> rules,
        IReadOnlyCollection<int> ports)
    {
        var reAdded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in ports.Distinct())
        {
            reAdded.Add(RuleNameFromPort(port));
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            // The base script always tears the legacy single-port rule down, so
            // it must not be repeated in the extra list.
            if (!IsAppOwnedRuleName(rule.Name)
                || string.Equals(rule.Name, RuleName, StringComparison.Ordinal)
                || reAdded.Contains(rule.Name))
            {
                continue;
            }

            names.Add(rule.Name);
        }

        return names.ToList();
    }

    private IReadOnlyCollection<string>? TryCollectStaleRuleNames(IReadOnlyCollection<int> ports)
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            dynamic policy = Activator.CreateInstance(policyType)!;
            try
            {
                return CollectRuleNamesToDelete(CollectRuleSnapshots(policy.Rules), ports);
            }
            finally
            {
                ReleaseCom(policy);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Enumeration is advisory: the base replace script still recreates
            // the rules needed by the current listeners.
            logger?.LogDebug(ex, "Enumerating stale firewall rules failed; continuing with the base replace script");
            return null;
        }
    }

    private static List<FirewallRuleSnapshot> CollectRuleSnapshots(dynamic ruleCollection)
    {
        var snapshots = new List<FirewallRuleSnapshot>();
        foreach (dynamic rule in ruleCollection)
        {
            var name = (string?)rule.Name;
            if (!IsAppOwnedRuleName(name))
            {
                continue;
            }

            var localPorts = new List<int>();
            foreach (var value in (((string?)rule.LocalPorts) ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                    && !localPorts.Contains(port))
                {
                    localPorts.Add(port);
                }
            }

            snapshots.Add(new FirewallRuleSnapshot(
                name!,
                localPorts,
                NormalizePath((string?)rule.ApplicationName ?? string.Empty),
                Convert.ToInt32(rule.Enabled, CultureInfo.InvariantCulture) != 0,
                Convert.ToInt32(rule.Direction, CultureInfo.InvariantCulture),
                Convert.ToInt32(rule.Action, CultureInfo.InvariantCulture),
                Convert.ToInt32(rule.Protocol, CultureInfo.InvariantCulture),
                Convert.ToInt32(rule.Profiles, CultureInfo.InvariantCulture)));
        }

        return snapshots;
    }

    private static bool IsQualifiedRule(FirewallRuleSnapshot rule) =>
        rule.Enabled
        && rule.Direction == Inbound
        && rule.Action == Allow
        && rule.Protocol == Tcp
        && rule.ProfileMask == PrivateProfile;

    private static bool SameProgramPath(string actual, string expected) =>
        string.Equals(NormalizePath(actual), expected, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static void ReleaseCom(object value)
    {
        if (Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
