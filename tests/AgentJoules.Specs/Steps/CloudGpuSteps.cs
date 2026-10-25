using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// The steps of the cloudgpu tier stories (M5). AJ-120 (REQ-031): the account, card and key-pair prerequisites. The chat facts (the funded account, the cost rules) are checked as the text of <c>docs/build-log.md</c>;
/// the key hygiene scenarios run <see cref="KeyHygieneCheck"/> over the tracked files of the repository and over a scratch directory under the temporary folder. No key file is ever read or created in the repository.
/// </summary>
[Binding]
public sealed partial class CloudGpuSteps(IReqnrollOutputHelper output) : IDisposable
{
    private const string CostRulesText = "do not apply to AI Cloud";

    private static readonly string[] SecretVariables = ["CLOUDGPU_SSH_HOST", "CLOUDGPU_SSH_USER"];

    private readonly IReqnrollOutputHelper _output = output;

    private string? _scratch;
    private KeyHygieneResult? _scratchResult;
    private KeyHygieneResult? _trackedResult;
    private bool _gitMissing;

    [GeneratedRegex(@"^- 2026-10-2[5-9].*steps 10 and 11.*confirmed", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ConfirmationLine();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex EmailLikeToken();

    [GeneratedRegex(@"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}\b")]
    private static partial Regex Ipv4Literal();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{1,4}:){1,6}:[0-9A-Fa-f]{1,4}\b|\b(?:[0-9A-Fa-f]{1,4}:){4,7}[0-9A-Fa-f]{1,4}\b")]
    private static partial Regex Ipv6Literal();

    [GeneratedRegex(@"\b(?:project|tenant|serviceaccount|computeinstance|computedisk|vpcnetwork|vpcsubnet)-[a-z0-9]{10,}\b")]
    private static partial Regex NebiusResourceId();

    [GeneratedRegex(@"\b\d{4}[ -]\d{4}[ -]\d{4}[ -]\d{4}\b")]
    private static partial Regex CardLikeGroups();

    public void Dispose()
    {
        if (_scratch is not null && Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static string[] BuildLogLines() => File.ReadAllLines(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "build-log.md"));

    // The 1-based line numbers of the AJ-120 entry of the build log (the heading to the next "### " or "## " heading) that hold an account detail. A value is never returned or echoed.
    private static int[] AccountDetailLines(string[] lines)
    {
        int start = Array.FindIndex(lines, static line => line.StartsWith("### AJ-120", StringComparison.Ordinal));
        if (start < 0)
        {
            return [];
        }

        int end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("### ", StringComparison.Ordinal) && !lines[end].StartsWith("## ", StringComparison.Ordinal))
        {
            end++;
        }

        // A configured value matches on word boundaries only, so a short value such as "user" does not match inside other words.
        Regex[] secrets =
        [
            .. SecretVariables.Select(static name => Environment.GetEnvironmentVariable(name))
                .Where(static value => !string.IsNullOrEmpty(value))
                .Select(static value => new Regex(@"(?<![\w.-])" + Regex.Escape(value!) + @"(?![\w.-])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))),
        ];
        List<int> found = [];
        for (int index = start; index < end; index++)
        {
            string line = lines[index];
            if (EmailLikeToken().IsMatch(line) || Ipv4Literal().IsMatch(line) || Ipv6Literal().IsMatch(line) || NebiusResourceId().IsMatch(line) || CardLikeGroups().IsMatch(line)
                || secrets.Any(secret => secret.IsMatch(line)))
            {
                found.Add(index + 1);
            }
        }

        return [.. found];
    }

    // AJ-120

    [Given(@"^the user has created the AI Cloud account and project and paid the minimum first payment$")]
    [When(@"^the user confirms in the chat$")]
    [Given(@"^the user ran ""ssh-keygen -t ed25519"" and stored the private key outside the repository$")]
    [Given(@"^the user has not added a card or made the first payment$")]
    [Given(@"^the user expects Token Factory credits to pay for the VM$")]
    [When(@"^the builder prepares the VM request$")]
    [When(@"^the builder states the cost rules$")]
    public static void ChatFactPrecondition()
    {
        // The user actions and the chat exchange cannot be driven by a test; the Then steps check what they leave in docs/build-log.md.
    }

    [Then(@"^docs/build-log\.md records a dated confirmation line without account details$")]
    public static void ThenTheBuildLogRecordsTheConfirmation()
    {
        string[] lines = BuildLogLines();
        string text = string.Join('\n', lines);
        Assert.True(ConfirmationLine().IsMatch(text), "docs/build-log.md has no dated line (2026-10-25 to 2026-10-29) that confirms steps 10 and 11.");
        int[] leaking = AccountDetailLines(lines);
        Assert.True(leaking.Length == 0, $"docs/build-log.md AJ-120 entry: account detail (email, IP address, resource ID, card number or the configured SSH host or user) on line(s) {string.Join(", ", leaking)}; the values are not echoed.");
    }

    [When(@"^the builder lists the tracked files$")]
    public void WhenTheBuilderListsTheTrackedFiles()
    {
        if (!GitRunner.Available())
        {
            _gitMissing = true;
            _output.WriteLine("not asserted: git not on PATH");
            return;
        }

        _trackedResult = KeyHygieneCheck.RunTracked(ConfigurationFixtures.RepositoryRoot());
    }

    [Then(@"^no tracked file name contains ""id_ed25519"", ""id_rsa"" or ""\.pem""$")]
    public void ThenNoTrackedFileNameIsAKey()
    {
        if (_gitMissing)
        {
            return;
        }

        KeyHygieneResult result = _trackedResult ?? throw new Xunit.Sdk.XunitException("The tracked files were not listed.");
        Assert.True(result.Passed, result.Message);
    }

    [Then(@"^""git check-ignore"" prints all three ignore patterns$")]
    public void ThenCheckIgnorePrintsAllThree()
    {
        if (_gitMissing)
        {
            return;
        }

        IReadOnlyList<string> printed = KeyHygieneCheck.IgnoredProbeNames(ConfigurationFixtures.RepositoryRoot());
        Assert.All(KeyHygieneCheck.IgnoreProbeNames, name => Assert.Contains(name, printed));
    }

    [Given(@"^a file named ""([^""]+)"" exists in the repository working tree$")]
    public void GivenAFileNamedExists(string name)
    {
        // The working tree is stood in for by a scratch directory under the temporary folder; the repository is never written.
        _scratch = Directory.CreateTempSubdirectory("aj120-").FullName;
        File.WriteAllText(Path.Combine(_scratch, name), "FAKE-PRIVATE-KEY-BODY-NOT-A-KEY");
    }

    [When(@"^the builder runs the key hygiene check$")]
    public void WhenTheBuilderRunsTheKeyHygieneCheck()
    {
        _scratchResult = KeyHygieneCheck.Run(_scratch ?? throw new Xunit.Sdk.XunitException("No scratch directory was prepared."));
    }

    [Then(@"^the check fails and the file is reported without printing its content$")]
    public void ThenTheCheckFailsAndReportsTheFile()
    {
        KeyHygieneResult result = _scratchResult ?? throw new Xunit.Sdk.XunitException("The check did not run.");
        Assert.False(result.Passed);
        Assert.Equal(["id_ed25519"], result.Offenders);
        Assert.Contains("id_ed25519", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE-PRIVATE-KEY-BODY", result.Message, StringComparison.Ordinal);
    }

    [Then(@"^the request is not sent$")]
    public static void ThenTheRequestIsNotSent()
    {
        // The request text is the one recorded in the build log: the one place the cost rules and the caps are told to the user before any VM request.
        string[] request = [.. BuildLogLines().Where(static line => line.Contains(CostRulesText, StringComparison.Ordinal))];
        Assert.NotEmpty(request);
        Assert.Contains(request, static line => line.Contains("USD 25", StringComparison.Ordinal));
        Assert.Contains(request, static line => line.Contains("USD 30", StringComparison.Ordinal));

        // No VM request exists before AJ-125: docs/cloudgpu-run.md is absent, or (after AJ-125) the step-10 confirmation line comes before the first AJ-125 entry.
        string runDocument = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "cloudgpu-run.md");
        if (File.Exists(runDocument))
        {
            string[] lines = BuildLogLines();
            int confirmation = Array.FindIndex(lines, static line => ConfirmationLine().IsMatch(line));
            int firstRequest = Array.FindIndex(lines, static line => line.StartsWith("### AJ-125", StringComparison.Ordinal));
            Assert.True(confirmation >= 0, "docs/cloudgpu-run.md exists but docs/build-log.md has no step-10 confirmation line.");
            Assert.True(firstRequest < 0 || confirmation < firstRequest, $"the step-10 confirmation (line {confirmation + 1}) must come before the first AJ-125 entry (line {firstRequest + 1}).");
        }
    }

    [Then(@"^the final report lists the cloudgpu tier as blocked on step 10$")]
    public void ThenTheFinalReportListsTheTierAsBlocked()
    {
        _output.WriteLine("not asserted: the final report exists only at the milestone exit; carried to the M5 exit check");
    }

    [Then(@"^the user is told that the credits do not apply and that the USD 25 payment is required$")]
    public static void ThenTheUserIsToldTheCostRules()
    {
        string[] told = [.. BuildLogLines().Where(static line => line.Contains(CostRulesText, StringComparison.Ordinal))];
        Assert.True(told.Length >= 1, "docs/build-log.md does not record the cost rules (\"" + CostRulesText + "\").");
        Assert.Contains(told, static line => line.Contains("USD 25", StringComparison.Ordinal));
    }
}