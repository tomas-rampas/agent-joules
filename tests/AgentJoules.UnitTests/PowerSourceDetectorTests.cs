using AgentJoules.Cli.Hosting;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Security;
using AgentJoules.Nvml.Platform;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-106 (REQ-021, EDGE-011): the power source of a run. The Linux reader runs over a directory tree of <c>type</c> and <c>online</c> files in a scratch folder, the Windows status mapping through an injected
/// delegate; no test calls the real <c>GetSystemPowerStatus</c> and none reads the <c>/sys</c> of the machine, so the result never depends on whether the machine that runs the tests is on its battery.
/// </summary>
[Trait("Spec", "REQ-021")]
public sealed class PowerSourceDetectorTests
{
    private static readonly string[] KnownValues = ["ac", "battery", "unknown"];
    private static readonly string?[] UnknownValues = [null, string.Empty, "AC", "mains", "Battery", " ac"];

    private static string Supply(TempWorkspace workspace, string name, string? type, string? online)
    {
        if (type is not null)
        {
            _ = workspace.WriteFile(Path.Combine(name, "type"), type + "\n");
        }

        if (online is not null)
        {
            _ = workspace.WriteFile(Path.Combine(name, "online"), online + "\n");
        }

        if (type is null && online is null)
        {
            _ = Directory.CreateDirectory(Path.Combine(workspace.Root, name));
        }

        return name;
    }

    // ---- the vocabulary ----

    [Fact]
    public void The_three_values_are_the_exact_strings_of_the_file()
    {
        Assert.Equal(["ac", "battery", "unknown"], [PowerSources.Ac, PowerSources.Battery, PowerSources.Unknown]);
        Assert.All(KnownValues, static value => Assert.True(PowerSources.IsKnown(value)));
        Assert.All(UnknownValues, static value => Assert.False(PowerSources.IsKnown(value)));
    }

    [Theory]
    [InlineData("ac", "ac")]
    [InlineData("battery", "battery")]
    [InlineData("unknown", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("AC", "unknown")]
    [InlineData("charging", "unknown")]
    public void Normalize_keeps_a_known_value_and_turns_anything_else_into_unknown(string? value, string expected) =>
        Assert.Equal(expected, PowerSources.Normalize(value));

    // ---- Windows: ACLineStatus of GetSystemPowerStatus, through an injected delegate ----

    [Theory]
    [InlineData((byte)1, "ac")]
    [InlineData((byte)0, "battery")]
    [InlineData((byte)255, "unknown")]
    [InlineData((byte)2, "unknown")]
    [InlineData((byte)128, "unknown")]
    public void The_windows_status_maps_1_to_ac_0_to_battery_and_anything_else_to_unknown(byte status, string expected)
    {
        Assert.Equal(expected, PowerSourceDetector.FromAcLineStatus(status));
        Assert.Equal(expected, new PowerSourceDetector(() => status, linuxPowerSupplyRoot: null).Detect());
    }

    [Fact]
    public void A_windows_call_that_returned_false_is_unknown()
    {
        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromAcLineStatus(null));
        Assert.Equal(PowerSources.Unknown, new PowerSourceDetector(() => null, linuxPowerSupplyRoot: null).Detect());
    }

    [Fact]
    public void A_windows_reader_that_throws_is_unknown_and_never_escapes()
    {
        var detector = new PowerSourceDetector(static () => throw new DllNotFoundException("kernel32.dll"), linuxPowerSupplyRoot: null);

        Assert.Equal(PowerSources.Unknown, detector.Detect());
    }

    // ---- Linux: /sys/class/power_supply/*/type and online ----

    [Fact]
    public void A_mains_supply_that_is_online_is_ac_even_with_a_battery()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "AC", "Mains", "1");
        _ = Supply(workspace, "BAT0", "Battery", null);

        Assert.Equal(PowerSources.Ac, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
        Assert.Equal(PowerSources.Ac, new PowerSourceDetector(windowsAcLineStatus: null, workspace.Root).Detect());
    }

    [Fact]
    public void A_mains_supply_that_is_offline_is_battery()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "AC", "Mains", "0");
        _ = Supply(workspace, "BAT0", "Battery", null);

        Assert.Equal(PowerSources.Battery, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void Only_battery_supplies_are_battery()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "BAT0", "Battery", null);
        _ = Supply(workspace, "BAT1", "Battery", null);

        Assert.Equal(PowerSources.Battery, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void One_mains_supply_online_among_several_is_ac()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "ADP0", "Mains", "0");
        _ = Supply(workspace, "ADP1", "Mains", "1");

        Assert.Equal(PowerSources.Ac, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void The_type_is_read_without_regard_to_case_or_the_trailing_newline()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("AC", "type"), "mains\r\n");
        _ = workspace.WriteFile(Path.Combine("AC", "online"), " 1 \r\n");

        Assert.Equal(PowerSources.Ac, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_folder_without_supply_directories_is_unknown()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_root_that_does_not_exist_is_unknown()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string missing = Path.Combine(workspace.Root, "no-such-folder");

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(missing));
        Assert.Equal(PowerSources.Unknown, new PowerSourceDetector(windowsAcLineStatus: null, missing).Detect());
    }

    [Fact]
    public void Supplies_that_are_neither_mains_nor_battery_are_unknown()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "ucsi-source-psy-USBC000-001", "USB", "1");
        _ = Supply(workspace, "hid-0003", "UPS", "1");

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_mains_supply_without_an_online_file_and_no_battery_is_unknown()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "AC", "Mains", null);

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("2")]
    public void A_mains_online_value_that_is_not_0_or_1_is_not_read_as_a_state(string online)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "AC", "Mains", online);

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_battery_beside_an_unreadable_mains_state_is_unknown()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "AC", "Mains", "x");
        _ = Supply(workspace, "BAT0", "Battery", null);

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_battery_beside_a_mains_supply_without_an_online_file_is_unknown()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "AC", "Mains", null);
        _ = Supply(workspace, "BAT0", "Battery", null);

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_mains_supply_that_is_offline_with_no_battery_is_unknown()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "AC", "Mains", "0");

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void An_online_mains_supply_wins_over_another_mains_supply_that_cannot_be_read()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "ADP0", "Mains", "x");
        _ = Supply(workspace, "ADP1", "Mains", "1");

        Assert.Equal(PowerSources.Ac, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    // ---- a USB-C source cannot be told from a sink ----

    [Theory]
    [InlineData("USB", "1")]
    [InlineData("USB", "0")]
    [InlineData("USB_C", "1")]
    [InlineData("usb", "1")]
    public void A_usb_supply_without_mains_is_unknown_whatever_it_reports_even_beside_a_battery(string type, string online)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "ucsi-source-psy-USBC000-001", type, online);
        _ = Supply(workspace, "BAT0", "Battery", null);

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_usb_supply_beside_a_mains_supply_does_not_change_the_mains_answer()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "ucsi-source-psy-USBC000-001", "USB", "1");
        _ = Supply(workspace, "AC", "Mains", "0");
        _ = Supply(workspace, "BAT0", "Battery", null);

        Assert.Equal(PowerSources.Battery, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    // ---- a supply of scope Device (a mouse, a headset) is not the machine's ----

    [Theory]
    [InlineData("Device")]
    [InlineData("device")]
    [InlineData(" Device ")]
    public void A_battery_of_scope_device_alone_is_unknown(string scope)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "hid-0003-battery", "Battery", null);
        _ = workspace.WriteFile(Path.Combine("hid-0003-battery", "scope"), scope + "\n");

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_battery_of_scope_device_beside_an_online_mains_supply_is_ac()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "hid-0003-battery", "Battery", null);
        _ = workspace.WriteFile(Path.Combine("hid-0003-battery", "scope"), "Device\n");
        _ = Supply(workspace, "AC", "Mains", "1");

        Assert.Equal(PowerSources.Ac, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_battery_of_scope_device_does_not_make_an_offline_mains_supply_battery()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "hid-0003-battery", "Battery", null);
        _ = workspace.WriteFile(Path.Combine("hid-0003-battery", "scope"), "Device\n");
        _ = Supply(workspace, "AC", "Mains", "0");

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_battery_of_scope_system_is_the_machines_battery()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "BAT0", "Battery", null);
        _ = workspace.WriteFile(Path.Combine("BAT0", "scope"), "System\n");
        _ = Supply(workspace, "hid-0003-battery", "Battery", null);
        _ = workspace.WriteFile(Path.Combine("hid-0003-battery", "scope"), "Device\n");

        Assert.Equal(PowerSources.Battery, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    [Fact]
    public void A_directory_without_a_type_file_is_not_a_supply()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "stray", null, "1");
        _ = Supply(workspace, "empty", null, null);

        Assert.Equal(PowerSources.Unknown, PowerSourceDetector.FromPowerSupplyRoot(workspace.Root));
    }

    // ---- the detector: one platform reader, never a throw ----

    [Fact]
    public void A_detector_without_a_reader_for_the_platform_is_unknown()
    {
        Assert.Equal(PowerSources.Unknown, new PowerSourceDetector(windowsAcLineStatus: null, linuxPowerSupplyRoot: null).Detect());
    }

    [Fact]
    public void The_windows_reader_is_used_before_the_linux_folder_when_both_are_given()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Supply(workspace, "AC", "Mains", "1");

        Assert.Equal(PowerSources.Battery, new PowerSourceDetector(static () => 0, workspace.Root).Detect());
    }

    [Fact]
    public void The_linux_supply_root_is_the_sysfs_power_supply_folder()
    {
        Assert.Equal("/sys/class/power_supply", PowerSourceDetector.LinuxPowerSupplyRoot);
    }

    // ---- the host and the harness (EDGE-011) ----

    [Fact]
    public void The_composition_root_registers_the_production_detector_without_overrides()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        var keys = new KeyRegistry(new SecretRedactor());

        // The production host (null overrides) is built and its detector resolved, never called: calling it would read the power state of the machine that runs the tests. Nothing else of the host is started.
        using IHost host = AgentJoulesHostFactory.Build(workspace.ConfigPath, null, null, keys, TextWriter.Null, TextWriter.Null);

        _ = Assert.IsType<PowerSourceDetector>(host.Services.GetRequiredService<IPowerSourceDetector>());
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void A_host_built_with_plain_test_overrides_resolves_the_unknown_detector_and_reads_nothing()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        var keys = new KeyRegistry(new SecretRedactor());

        using IHost host = AgentJoulesHostFactory.Build(null, null, new Cli.CliHostOverrides(WorkingDirectory: workspace.Root), keys, TextWriter.Null, TextWriter.Null);

        IPowerSourceDetector detector = host.Services.GetRequiredService<IPowerSourceDetector>();
        _ = Assert.IsType<UnknownPowerSourceDetector>(detector);
        Assert.Equal(PowerSources.Unknown, detector.Detect());
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task The_cli_harness_with_default_overrides_resolves_the_unknown_detector()
    {
        IPowerSourceDetector? detector = null;
        var overrides = new Cli.CliHostOverrides(OnHostStarted: host => detector = host.Services.GetRequiredService<IPowerSourceDetector>());

        CliRunResult result = await CliHarness.RunAsync("fake-write", overrides).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        _ = Assert.IsType<UnknownPowerSourceDetector>(detector);
        Assert.Equal(PowerSources.Unknown, detector.Detect());
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void The_unknown_detector_answers_unknown()
    {
        Assert.Equal(PowerSources.Unknown, new UnknownPowerSourceDetector().Detect());
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void Harness_power_source_is_unknown()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        var keys = new KeyRegistry(new SecretRedactor());
        var setup = RunCommandSetup.Create([]);

        using IHost host = AgentJoulesHostFactory.Build(null, null, setup.ToOverrides(workspace.Root), keys, TextWriter.Null, TextWriter.Null);

        // The harness detector is the fake, not the production one: it answers unknown and never reads the host's power state.
        IPowerSourceDetector detector = host.Services.GetRequiredService<IPowerSourceDetector>();
        _ = Assert.IsType<FakePowerSourceDetector>(detector);
        Assert.Equal(PowerSources.Unknown, detector.Detect());
        Assert.Equal(1, setup.PowerSource.Calls);
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void A_test_may_replace_the_harness_detector_with_a_fake_that_returns_battery()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        var keys = new KeyRegistry(new SecretRedactor());
        var setup = RunCommandSetup.Create([]);
        setup.PowerSource.Value = PowerSources.Battery;

        using IHost host = AgentJoulesHostFactory.Build(null, null, setup.ToOverrides(workspace.Root), keys, TextWriter.Null, TextWriter.Null);

        Assert.Equal(PowerSources.Battery, host.Services.GetRequiredService<IPowerSourceDetector>().Detect());
    }
}