using Kivo.Windows.Services;
using Xunit;

namespace Kivo.Tests;

/// <summary>
/// Verifies the generated Windows Firewall netsh script (shelled out through
/// an encoded PowerShell command by the desktop app): one private-profile TCP
/// rule per listener port, replace-or-add semantics, the legacy single-port
/// rule being torn down so migration to per-port rules cannot leave stale open
/// ports behind, and pre-rebrand ShadowPlay rules/retired-port leftovers being
/// deleted in the same elevated run. The produced rules must never broaden the
/// pre-rebrand stance: private profile, TCP, per port, per program only.
/// </summary>
public sealed class FirewallRuleScriptTests
{
    private const string KivoPath = @"C:\Program Files\Kivo\Kivo.exe";

    [Fact]
    public void Rule_names_are_prefixed_and_port_suffixed()
    {
        Assert.Equal(WindowsFirewallService.RuleName, "ShadowPlay LAN API (Private)");
        Assert.StartsWith(WindowsFirewallService.RuleNamePrefix, WindowsFirewallService.RuleNameFromPort(5177));
        Assert.EndsWith("5177", WindowsFirewallService.RuleNameFromPort(5177));
    }

    [Fact]
    public void Script_adds_one_private_tcp_rule_per_port()
    {
        var script = WindowsFirewallService.BuildRuleScript(KivoPath, [5177, 5178]);

        Assert.Contains(
            "add rule name='ShadowPlay LAN API (Private) TCP 5177' dir=in action=allow "
            + "protocol=TCP localport=5177 profile=private "
            + $"program='{KivoPath}' enable=yes",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "add rule name='ShadowPlay LAN API (Private) TCP 5178' dir=in action=allow "
            + "protocol=TCP localport=5178 profile=private "
            + $"program='{KivoPath}' enable=yes",
            script,
            StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(script, "add rule"));
    }

    [Fact]
    public void Script_tears_down_the_legacy_single_port_rule()
    {
        // Migration: the old wide-open rule named without a port suffix must
        // be deleted, or the pre-HTTPS port stays reachable forever.
        var script = WindowsFirewallService.BuildRuleScript(KivoPath, [5177]);

        Assert.Contains($"delete rule name='{WindowsFirewallService.RuleName}'", script);
    }

    [Fact]
    public void Script_deletes_a_port_rule_before_readding_it()
    {
        var script = WindowsFirewallService.BuildRuleScript(KivoPath, [5177]);
        var ruleName = WindowsFirewallService.RuleNameFromPort(5177);

        var deleteIndex = script.IndexOf($"delete rule name='{ruleName}'", StringComparison.Ordinal);
        var addIndex = script.IndexOf($"add rule name='{ruleName}'", StringComparison.Ordinal);

        Assert.True(deleteIndex >= 0, "the per-port rule must be deleted");
        Assert.True(addIndex >= 0, "the per-port rule must be re-added");
        Assert.True(deleteIndex < addIndex, "delete must precede add (replace-or-add)");
    }

    [Fact]
    public void Script_neutralizes_single_quotes_in_the_program_path()
    {
        const string apostrophePath = @"C:\Users\O'Brien\AppData\Local\Kivo.exe";
        var script = WindowsFirewallService.BuildRuleScript(apostrophePath, [5177]);

        Assert.Contains("O''Brien", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Users\O'Brien\", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_ports_do_not_emit_duplicate_rules()
    {
        var script = WindowsFirewallService.BuildRuleScript(KivoPath, [5177, 5177, 5178]);

        Assert.Equal(2, CountOccurrences(script, "add rule"));
        Assert.Equal(1, CountOccurrences(script, "add rule name='ShadowPlay LAN API (Private) TCP 5177'"));
        Assert.Equal(1, CountOccurrences(script, "delete rule name='ShadowPlay LAN API (Private) TCP 5177'"));
    }

    [Fact]
    public void Script_exits_with_the_last_status_code()
    {
        var script = WindowsFirewallService.BuildRuleScript(KivoPath, [5177]);

        Assert.StartsWith("$ErrorActionPreference='Stop'; ", script, StringComparison.Ordinal);
        Assert.EndsWith("exit $LASTEXITCODE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_deletes_stale_rebrand_rules_before_readding_requested_ports()
    {
        var script = WindowsFirewallService.BuildRuleScript(
            KivoPath,
            [5177],
            ["ShadowPlay LAN API (Private) TCP 5050", "ShadowPlay LAN API (Private) TCP 5170"]);

        Assert.Contains(
            "delete rule name='ShadowPlay LAN API (Private) TCP 5050'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "delete rule name='ShadowPlay LAN API (Private) TCP 5170'",
            script,
            StringComparison.Ordinal);

        var firstStaleDelete = script.IndexOf(
            "delete rule name='ShadowPlay LAN API (Private) TCP 5050'",
            StringComparison.Ordinal);
        var addIndex = script.IndexOf("add rule name='ShadowPlay LAN API (Private) TCP 5177'", StringComparison.Ordinal);
        Assert.True(
            firstStaleDelete >= 0 && addIndex > firstStaleDelete,
            "stale pre-rebrand rules must be deleted before the current rules are added");
    }

    [Fact]
    public void Script_rejects_unowned_names_in_extra_deletes()
    {
        var script = WindowsFirewallService.BuildRuleScript(
            KivoPath,
            [5177],
            ["Rule with 'quote'"]);

        Assert.DoesNotContain("Rule with", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_never_broadens_beyond_private_tcp_program_rules()
    {
        var script = WindowsFirewallService.BuildRuleScript(
            KivoPath,
            [5177, 5178],
            ["ShadowPlay LAN API (Private) TCP 5050"]);

        Assert.DoesNotContain("profile=any", script, StringComparison.Ordinal);
        Assert.DoesNotContain("profile=all", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localport=any", script, StringComparison.Ordinal);
        Assert.DoesNotContain("profile=domain", script, StringComparison.Ordinal);
        Assert.DoesNotContain("profile=public", script, StringComparison.Ordinal);
        // Every rule that is added carries the full restriction set.
        Assert.Equal(2, CountOccurrences(script, "profile=private"));
        Assert.Equal(2, CountOccurrences(script, "add rule"));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
