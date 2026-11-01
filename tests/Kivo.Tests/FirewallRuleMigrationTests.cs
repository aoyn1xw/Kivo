using Kivo.Windows.Services;
using Xunit;

namespace Kivo.Tests;

/// <summary>
/// Exercises the pure rule-analysis and cleanup helpers behind
/// WindowsFirewallService, simulating what an upgraded machine looks like:
/// app-owned rules whose names survived the rebrand but whose programs still
/// point at the previous ShadowPlay.exe path. The rebrand must surface clear
/// repair guidance without ever broadening the old restrictions.
/// </summary>
public sealed class FirewallRuleMigrationTests
{
    private const string KivoPath = @"C:\Program Files\Kivo\Kivo.exe";
    private const string LegacyPath = @"C:\Program Files\ShadowPlay\ShadowPlay.exe";
    private const int Port = 5177;
    private const int HttpsPort = 5178;
    private const int RetiredPort = 5050;

    private static FirewallRuleSnapshot Rule(string name, int[] ports, string path, bool enabled = true) => new(
        name,
        ports,
        path,
        enabled,
        WindowsFirewallService.Inbound,
        WindowsFirewallService.Allow,
        WindowsFirewallService.Tcp,
        WindowsFirewallService.PrivateProfile);

    private static FirewallRuleSnapshot KivoRule(int port, bool enabled = true) =>
        Rule(WindowsFirewallService.RuleNameFromPort(port), [port], KivoPath, enabled);

    private static FirewallRuleSnapshot LegacyRule(int port, bool enabled = true) =>
        Rule(WindowsFirewallService.RuleNameFromPort(port), [port], LegacyPath, enabled);

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+5177")]
    [InlineData("05177")]
    [InlineData(" 5177")]
    [InlineData("5177 ")]
    [InlineData("5177 diagnostics")]
    [InlineData("diagnostics")]
    [InlineData("5177' ; exit 1")]
    public void Malformed_per_port_names_are_never_owned_or_deleted(string suffix)
    {
        var name = WindowsFirewallService.RuleNamePrefix + suffix;
        Assert.False(WindowsFirewallService.IsAppOwnedRuleName(name));
        Assert.Empty(WindowsFirewallService.CollectRuleNamesToDelete(
            [Rule(name, [RetiredPort], LegacyPath)], [Port]));
        Assert.DoesNotContain($"delete rule name='{name}'", WindowsFirewallService.BuildRuleScript(KivoPath, [Port], [name]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void Boundary_ports_are_supported(int port)
    {
        Assert.True(WindowsFirewallService.IsAppOwnedRuleName(WindowsFirewallService.RuleNameFromPort(port)));
    }

    [Fact]
    public void Rule_name_matcher_accepts_legacy_and_per_port_names_only()
    {
        Assert.True(WindowsFirewallService.IsAppOwnedRuleName(WindowsFirewallService.RuleName));
        Assert.True(WindowsFirewallService.IsAppOwnedRuleName(WindowsFirewallService.RuleNameFromPort(Port)));
        Assert.False(WindowsFirewallService.IsAppOwnedRuleName("ShadowPlay Diagnostics"));
        Assert.False(WindowsFirewallService.IsAppOwnedRuleName("Some Other App"));
        Assert.False(WindowsFirewallService.IsAppOwnedRuleName(null));
        Assert.False(WindowsFirewallService.IsAppOwnedRuleName(string.Empty));
    }

    [Fact]
    public void Ready_status_when_qualified_rules_point_at_kivo()
    {
        var status = WindowsFirewallService.AnalyzeRules(
            KivoPath,
            [Port, HttpsPort],
            [KivoRule(Port), KivoRule(HttpsPort)],
            privateActive: true);

        Assert.Equal(FirewallRuleState.Ready, status.State);
        Assert.False(status.NeedsSetup);
        Assert.Contains("TCP 5177, 5178", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_shadowplay_rules_report_migration_guidance_for_repair()
    {
        var status = WindowsFirewallService.AnalyzeRules(
            KivoPath,
            [Port],
            [LegacyRule(Port)],
            privateActive: true);

        Assert.Equal(FirewallRuleState.Mismatched, status.State);
        Assert.True(status.NeedsSetup);
        Assert.True(status.PrivateNetworkActive);
        Assert.Contains("TCP 5177", status.Detail, StringComparison.Ordinal);
        Assert.Contains("previous ShadowPlay installation", status.Detail, StringComparison.Ordinal);
        // Repairing needs an administrator prompt; the guidance says so, and
        // the status must not pretend the rules were migrated already.
        Assert.Contains("administrator", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("are active", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_rules_on_retired_ports_away_from_requested_ports_are_not_listed()
    {
        // The retired port is not part of the requested listener set; only the
        // uncovered Kivo port gets guidance (the retired-port rule is cleaned
        // up by collect-rule-names-to-delete below).
        var status = WindowsFirewallService.AnalyzeRules(
            KivoPath,
            [Port],
            [LegacyRule(RetiredPort), LegacyRule(Port)],
            privateActive: true);

        Assert.Equal(FirewallRuleState.Mismatched, status.State);
        Assert.Contains("TCP 5177", status.Detail, StringComparison.Ordinal);
        Assert.Contains("previous ShadowPlay installation", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("5050", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_or_misprofiled_rules_are_stale_not_legacy()
    {
        var status = WindowsFirewallService.AnalyzeRules(
            KivoPath,
            [Port],
            [LegacyRule(Port, enabled: false)],
            privateActive: true);

        Assert.Equal(FirewallRuleState.Mismatched, status.State);
        Assert.DoesNotContain("ShadowPlay installation", status.Detail, StringComparison.Ordinal);
        Assert.Contains("Stale rules exist for TCP 5177", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Kivo_rules_taking_over_from_legacy_rules_ready_the_check()
    {
        var status = WindowsFirewallService.AnalyzeRules(
            KivoPath,
            [Port],
            [LegacyRule(Port), KivoRule(Port)],
            privateActive: true);

        Assert.Equal(FirewallRuleState.Ready, status.State);
    }

    [Fact]
    public void Non_private_network_profile_only_blocks_when_all_ports_are_covered()
    {
        var status = WindowsFirewallService.AnalyzeRules(
            KivoPath,
            [Port],
            [KivoRule(Port)],
            privateActive: false);

        Assert.Equal(FirewallRuleState.BlockedByNetworkProfile, status.State);

        var missingSplit = WindowsFirewallService.AnalyzeRules(
            KivoPath,
            [Port],
            [KivoRule(HttpsPort)],
            privateActive: false);

        Assert.Equal(FirewallRuleState.Mismatched, missingSplit.State);
    }

    [Fact]
    public void Cleanup_list_contains_legacy_rules_and_retired_ports_only()
    {
        var rules = new List<FirewallRuleSnapshot>
        {
            LegacyRule(RetiredPort),                         // legacy path, port retired
            Rule(WindowsFirewallService.RuleName, [5170], LegacyPath), // legacy single-port name
            KivoRule(Port),                                  // requested: base script replaces it
            KivoRule(HttpsPort),                             // requested: base script replaces it
        };

        var names = WindowsFirewallService.CollectRuleNamesToDelete(rules, [Port, HttpsPort]);

        Assert.Contains(WindowsFirewallService.RuleNameFromPort(RetiredPort), names);
        // The legacy single-port name is torn down by the base script itself,
        // so the extra delete list stays free of the duplicate.
        Assert.DoesNotContain(WindowsFirewallService.RuleName, names);
        Assert.DoesNotContain(WindowsFirewallService.RuleNameFromPort(Port), names);
        Assert.DoesNotContain(WindowsFirewallService.RuleNameFromPort(HttpsPort), names);
    }

    [Fact]
    public void Cleanup_ignores_rules_not_owned_by_the_app()
    {
        var foreign = Rule("Some Other App (Private)", [Port], LegacyPath);

        var names = WindowsFirewallService.CollectRuleNamesToDelete([foreign], [Port]);

        Assert.Empty(names);
    }
}
