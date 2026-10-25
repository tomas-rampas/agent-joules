using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-125 (REQ-031): create the VM when asked, deliver the CLI and probe the GPU over SSH. The scenarios follow the AJ-023 convention of <c>@integration</c> scenarios: with the gate AGENTJOULES_INTEGRATION closed, or with the
/// committed evidence absent, a step writes <c>not asserted: &lt;reason&gt;</c> through <see cref="IReqnrollOutputHelper"/> and passes; otherwise it asserts the committed evidence (the newest committed cloudgpu probe, the publish
/// folder, the sections of <c>docs/cloudgpu-run.md</c>). The steps that describe what the builder does on the VM (publish, scp, the probe) are never run by a test. A scenario about an incident (a fallback preset, a refused GPU,
/// MIG enabled, a missing Docker) asserts only when the run document records that incident. No host, address or user value is read into a message. Every step is scoped to the tag <c>AJ-S125</c>.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string VmTag = "AJ-S125";


    private string _vmCommand = string.Empty;

    [GeneratedRegex(@"\bexit(?:\s+code)?\s*[:=]?\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RecordedExit();

    // Asserts when the gate is open and the evidence exists; otherwise writes the reason and passes (AJ-023 convention).
    private void AssertEvidence(Func<string?> absentReason, Action assert)
    {
        string? reason = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName)) ?? absentReason();
        if (reason is not null)
        {
            _output.WriteLine("not asserted: " + reason);
            return;
        }

        assert();
    }

    // Asserts over a section of docs/cloudgpu-run.md; the section is absent, or the scenario does not apply, is a not-asserted line.
    private void AssertSection(string heading, Action<string> assert, Func<string, string?>? inapplicable = null)
    {
        string text = string.Empty;
        AssertEvidence(
            () =>
            {
                if (CloudGpuEvidence.RunDocumentText() is not { } document)
                {
                    return "docs/cloudgpu-run.md does not exist yet";
                }

                if (CloudGpuEvidence.Section(document, heading) is not { } section)
                {
                    return $"docs/cloudgpu-run.md has no ## {heading} section yet";
                }

                text = section;
                return inapplicable?.Invoke(section);
            },
            () => assert(text));
    }

    // Asserts over the whole run document.
    private void AssertDocument(Action<string> assert, Func<string, string?>? inapplicable = null)
    {
        string text = string.Empty;
        AssertEvidence(
            () =>
            {
                if (CloudGpuEvidence.RunDocumentText() is not { } document)
                {
                    return "docs/cloudgpu-run.md does not exist yet";
                }

                text = document;
                return inapplicable?.Invoke(document);
            },
            () => assert(text));
    }

    // The exit codes the document records after each occurrence of a command, up to the next command line.
    private static List<int> RecordedExitCodes(string text, string command)
    {
        string[] lines = text.Split('\n');
        List<int> codes = [];
        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(command, StringComparison.Ordinal))
            {
                continue;
            }

            for (int next = index; next < Math.Min(lines.Length, index + 12); next++)
            {
                if (next > index && lines[next].Contains("AgentJoules.Cli", StringComparison.Ordinal))
                {
                    break;
                }

                foreach (Match match in RecordedExit().Matches(lines[next]))
                {
                    codes.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            }
        }

        return codes;
    }

    private static string? NoRecordedCommand(string text, string command) =>
        text.Contains(command, StringComparison.Ordinal) ? null : $"docs/cloudgpu-run.md does not record \"{command}\" yet";

    private static string? NoIncident(string text, string trigger, string incident) =>
        text.Contains(trigger, StringComparison.OrdinalIgnoreCase) ? null : $"no {incident} is recorded, so the stop procedure was not exercised";

    // AJ-125

    [Given(@"^the local Definition of Done commit exists$")]
    [Scope(Tag = VmTag)]
    public void GivenTheLocalDefinitionOfDoneCommitExists() =>
        AssertEvidence(
            static () => GitRunner.Available() ? null : "git not on PATH",
            () =>
            {
                GitResult log = GitRunner.Run(ConfigurationFixtures.RepositoryRoot(), "log", "-1", "--format=%h", "--grep=^AJ-115", "HEAD");
                Assert.True(log.ExitCode == 0 && log.Stdout.Trim().Length > 0, "no commit whose message starts with AJ-115 is reachable from HEAD.");
            });

    [When(@"^I run ""([^""]*)""$")]
    [Scope(Tag = VmTag)]
    public void WhenIRunAJ125(string command)
    {
        _vmCommand = command;
        _output.WriteLine("not asserted: a test never runs \"" + command + "\" (the builder runs it); the Then steps check what it left");
    }

    [When(@"^I run ""([^""]*)"" on the VM$")]
    [Scope(Tag = VmTag)]
    public void WhenIRunOnTheVmAJ125(string command)
    {
        _vmCommand = command;
        _output.WriteLine("not asserted: a test never reaches the VM (the builder runs \"" + command + "\" there); the Then steps check what it left");
    }

    [Then(@"^the exit code is (\d+)$")]
    [Scope(Tag = VmTag)]
    public void ThenTheExitCodeIsAJ125(int expected)
    {
        if (_vmCommand.StartsWith("dotnet publish", StringComparison.Ordinal))
        {
            string folder = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "artifacts", "linux-x64");
            AssertEvidence(
                () => Directory.Exists(folder) ? null : "artifacts/linux-x64 does not exist (AJ-125-T2 publishes it)",
                () => Assert.True(File.Exists(Path.Combine(folder, "AgentJoules.Cli")), $"the publish folder holds no AgentJoules.Cli, so the publish (expected exit {expected}) did not complete."));
            return;
        }

        // The dry-run record is in the Commands section; the phrase "exit <n>" elsewhere (the Runs or Cost sections) does not make the scenario apply.
        AssertSection(
            "Commands",
            commands => Assert.True(RecordedExitCodes(commands, _vmCommand).Contains(expected), $"the Commands section records no exit code {expected.ToString(CultureInfo.InvariantCulture)} for \"{_vmCommand}\"."),
            commands => NoIncident(commands, "exit " + expected.ToString(CultureInfo.InvariantCulture), "dry-run with exit " + expected.ToString(CultureInfo.InvariantCulture)) ?? NoRecordedCommand(commands, _vmCommand));
    }

    [Then(@"^""git check-ignore ([^""]*)"" prints the path$")]
    [Scope(Tag = VmTag)]
    public void ThenCheckIgnorePrintsThePath(string path) =>
        AssertEvidence(
            static () => GitRunner.Available() ? null : "git not on PATH",
            () =>
            {
                GitResult result = GitRunner.Run(ConfigurationFixtures.RepositoryRoot(), "check-ignore", path);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(path, result.Stdout.Trim());
            });

    [Given(@"^the user created the VM as requested and set CLOUDGPU_SSH_HOST and CLOUDGPU_SSH_USER$")]
    [Given(@"^the preferred preset is unavailable and the user created preset ""(?:[^""]*)"" on the same platform$")]
    [Given(@"^nvidia-smi on the VM reports a GPU that is not an L40S$")]
    [Given(@"^df on the VM reports a boot disk below 150 GB$")]
    [Given(@"^the VM probe reports migMode ""enabled""$")]
    [Given(@"^CLOUDGPU_SSH_HOST is unset on the laptop$")]
    [Given(@"^""docker --version"" fails on the VM$")]
    [When(@"^the builder copies the CLI and runs ""(?:[^""]*)"" on the VM$")]
    [When(@"^the builder records the instance$")]
    [When(@"^the builder checks the instance$")]
    [When(@"^the builder tries to copy the CLI$")]
    [When(@"^the builder prepares the container runtime$")]
    [Scope(Tag = VmTag)]
    public static void VmUserActionOrBuilderProcedure()
    {
        // The user actions (creating the VM, setting the variables) and the builder commands over SSH cannot be driven by a test; the Then steps check what they leave in the committed files and in docs/cloudgpu-run.md.
    }

    [Then(@"^the probe file has an L40S name, a gpuUuid, migMode ""disabled"" or ""not-supported"" and a boolean energyCounterSupported$")]
    [Scope(Tag = VmTag)]
    public void ThenTheProbeFileHasTheFields() =>
        AssertEvidence(
            () => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Probe),
            () =>
            {
                string path = CloudGpuEvidence.NewestProbe()!;
                string[] failures = [.. CloudGpuEvidence.ProbeFailures(Path.GetFileName(path), File.ReadAllText(path)).Where(static failure => !failure.StartsWith("$defs/probe", StringComparison.Ordinal))];
                Assert.True(failures.Length == 0, Path.GetFileName(path) + ":\n" + string.Join('\n', failures));
            });

    [Then(@"^the file validates against the probe schema definition$")]
    [Scope(Tag = VmTag)]
    public void ThenTheFileValidatesAgainstTheProbeDefinition() =>
        AssertEvidence(
            () => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Probe),
            () =>
            {
                string path = CloudGpuEvidence.NewestProbe()!;
                string[] failures = [.. CloudGpuEvidence.ProbeFailures(Path.GetFileName(path), File.ReadAllText(path)).Where(static failure => failure.StartsWith("$defs/probe", StringComparison.Ordinal))];
                Assert.True(failures.Length == 0, Path.GetFileName(path) + ":\n" + string.Join('\n', failures));
            });

    [Then(@"^the Instance section names the preset used and why the fallback was needed$")]
    [Scope(Tag = VmTag)]
    public void ThenTheInstanceSectionNamesThePreset() =>
        AssertSection(
            "Instance",
            section =>
            {
                Assert.True(Regex.IsMatch(section, @"1gpu-\d+vcpu-\d+gb", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "the Instance section names no preset of the form 1gpu-<n>vcpu-<n>gb.");
                Assert.True(Regex.IsMatch(section, @"fallback|unavailable|materially|price|cost", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "the Instance section does not say why the fallback preset was needed.");
            },
            static section => section.Contains("1gpu-16vcpu-64gb", StringComparison.Ordinal) && !section.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                ? "the preferred preset 1gpu-16vcpu-64gb was used, so there is no fallback to record"
                : null);

    [Then(@"^the builder stops and asks the user for a new decision$")]
    [Scope(Tag = VmTag)]
    public void ThenTheBuilderStopsOnAGpuOtherThanL40S() =>
        AssertEvidence(
            () => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Probe),
            () =>
            {
                // A stop leaves no committed probe of another GPU class: every committed cloudgpu probe names an L40S.
                string[] offenders = [.. CloudGpuEvidence.CommittedProbeFiles().Where(static file => CloudGpuEvidence.ProbeFailures(Path.GetFileName(file), File.ReadAllText(file)).Any(static failure => failure.StartsWith("name:", StringComparison.Ordinal))).Select(static file => Path.GetFileName(file))];
                Assert.True(offenders.Length == 0, "a committed cloudgpu probe names a GPU that is not an L40S: " + string.Join(", ", offenders));
            });

    [Then(@"^the builder stops and asks the user to recreate the VM with a larger disk$")]
    [Scope(Tag = VmTag)]
    public void ThenTheBuilderStopsOnASmallDisk() =>
        AssertSection(
            "Instance",
            section =>
            {
                Match disk = Regex.Match(section, @"(?im)^.*boot disk.*?(\d+(?:\.\d+)?)\s*(T|G)i?B?\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                Assert.True(disk.Success, "the Instance section records no boot disk size (\"boot disk ... <n> G\").");
                double gigabytes = double.Parse(disk.Groups[1].Value, CultureInfo.InvariantCulture) * (disk.Groups[2].Value.Equals("T", StringComparison.OrdinalIgnoreCase) ? 1000 : 1);
                Assert.True(gigabytes >= 150, $"the recorded boot disk is {gigabytes.ToString(CultureInfo.InvariantCulture)} GB, below 150 GB: the story stops.");
            });

    [Then(@"^the builder asks the user to disable MIG or recreate the VM$")]
    [Scope(Tag = VmTag)]
    public void ThenTheBuilderAsksAboutMig() =>
        AssertEvidence(
            () => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Probe),
            () =>
            {
                // MIG enabled stops the story, so no committed cloudgpu probe reports it: the probe check refuses every migMode but disabled and not-supported, whatever the spacing of the file.
                string[] offenders = [.. CloudGpuEvidence.CommittedProbeFiles().Where(static file => CloudGpuEvidence.ProbeFailures(Path.GetFileName(file), File.ReadAllText(file)).Any(static failure => failure.StartsWith("migMode:", StringComparison.Ordinal))).Select(static file => Path.GetFileName(file))];
                Assert.True(offenders.Length == 0, "a committed cloudgpu probe reports MIG enabled or no usable migMode: " + string.Join(", ", offenders));
            });

    [Then(@"^no scp command runs and the user is asked to set the variable$")]
    [Scope(Tag = VmTag)]
    public void ThenNoScpRunsWithoutTheVariables() =>
        AssertDocument(
            document =>
            {
                // Commands in the documents show the variable names only, and no configured value is in a tracked or untracked file (the values are never echoed, and never put in an argument list).
                int[] unnamedScp = [.. document.Split('\n').Select(static (line, index) => (line, number: index + 1)).Where(static pair => pair.line.Contains("scp ", StringComparison.Ordinal) && !pair.line.Contains("$CLOUDGPU_SSH_HOST", StringComparison.Ordinal)).Select(static pair => pair.number)];
                Assert.True(unnamedScp.Length == 0, "docs/cloudgpu-run.md has scp lines that do not name $CLOUDGPU_SSH_HOST, at lines " + string.Join(", ", unnamedScp));
                foreach (string variable in SecretVariables)
                {
                    string? value = Environment.GetEnvironmentVariable(variable);
                    if (!string.IsNullOrWhiteSpace(value) && GitRunner.Available())
                    {
                        Assert.False(CloudGpuEvidence.RepositoryHoldsSshValue(ConfigurationFixtures.RepositoryRoot(), variable, value), $"git grep finds the value of {variable} in a file of the repository (the value is not echoed).");
                    }
                }
            });

    [Then(@"^the install commands are recorded in the Commands section and counted in the box$")]
    [Scope(Tag = VmTag)]
    public void ThenTheDockerInstallIsRecorded() =>
        AssertSection(
            "Commands",
            commands => Assert.True(Regex.IsMatch(commands, @"apt(-get)?\s+install|get\.docker|docker-ce|docker\.io|nvidia-container-toolkit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "the Commands section records no install command for the container runtime."),
            static commands => commands.Contains("docker --version", StringComparison.Ordinal) && Regex.IsMatch(commands, @"(?i)docker.{0,40}(not found|no such file|failed)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                ? null
                : "docker --version did not fail on the VM, so there are no install commands to record");
}