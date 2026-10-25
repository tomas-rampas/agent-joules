using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// Reads <c>docs/cloudgpu-run.md</c> (AJ-128, REQ-031, EDGE-016, EDGE-017) and judges it. The text is cut into sections by <c>## </c> headings (level 2 only, as in <c>docs/endpoints.md</c> and
/// <c>docs/nim-local-attempt.md</c>; a heading inside a code fence does not count and a <c>###</c> heading stays in the body of the section above it). The contract is a list of small rules, each a public method that returns
/// its problems, so that later stories and the exit checks can reuse a single rule: <see cref="HeadingProblems"/>, <see cref="InstanceProblems"/>, <see cref="CommandsProblems"/>, <see cref="RunsProblems"/>,
/// <see cref="CostProblems"/>, <see cref="TeardownProblems"/>, <see cref="Ipv4Problems"/>, <see cref="KeyProblems"/> and <see cref="SshTargetProblems"/>. A problem names the section or the line number and never
/// repeats an address, a key or a host.
/// </summary>
public sealed partial class CloudGpuRunDocumentParser
{
    /// <summary>The cost cap in USD: the observed cost may equal it and may not exceed it.</summary>
    public const decimal CostCapUsd = 30m;

    /// <summary>The only way the SSH target may be written.</summary>
    public const string SshTarget = "$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST";

    /// <summary>The statement an Instance section must hold.</summary>
    public const string PreemptibleStatement = "preemptible: off (user-confirmed)";

    /// <summary>The pattern of an IPv6 literal (a compressed form with <c>::</c> or a form of five to eight groups); <c>CloudGpuSteps</c> builds its own check from the same pattern.</summary>
    public const string Ipv6Pattern = @"\b(?:[0-9A-Fa-f]{1,4}:){1,6}:[0-9A-Fa-f]{1,4}\b|\b(?:[0-9A-Fa-f]{1,4}:){4,7}[0-9A-Fa-f]{1,4}\b";

    private const string HeadingPrefix = "## ";
    private const string FenceMarker = "```";

    private static readonly string[] KeyVariables = ["NGC_API_KEY", "NVIDIA_API_KEY", "NEBIUS_API_KEY"];
    private static readonly string[] SshVariables = ["CLOUDGPU_SSH_HOST", "CLOUDGPU_SSH_USER"];

    private readonly string[] _lines;
    private readonly Dictionary<string, CloudGpuRunSection> _sections;
    private readonly List<string> _problems = [];

    private CloudGpuRunDocumentParser(string[] lines, Dictionary<string, CloudGpuRunSection> sections)
    {
        _lines = lines;
        _sections = sections;
    }

    /// <summary>The six headings the contract requires, in document order.</summary>
    public static IReadOnlyList<string> RequiredHeadings { get; } = ["Instance", "Commands", "Probe", "Runs", "Cost", "Teardown"];

    /// <summary>The <c>Instance</c> section.</summary>
    public CloudGpuRunSection Instance => SectionNamed("Instance");

    /// <summary>The <c>Commands</c> section.</summary>
    public CloudGpuRunSection Commands => SectionNamed("Commands");

    /// <summary>The <c>Probe</c> section.</summary>
    public CloudGpuRunSection Probe => SectionNamed("Probe");

    /// <summary>The <c>Runs</c> section.</summary>
    public CloudGpuRunSection Runs => SectionNamed("Runs");

    /// <summary>The <c>Cost</c> section.</summary>
    public CloudGpuRunSection Cost => SectionNamed("Cost");

    /// <summary>The <c>Teardown</c> section.</summary>
    public CloudGpuRunSection Teardown => SectionNamed("Teardown");

    /// <summary>What the contract found wrong, in the order of the rules; empty when the document passes.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Whether the document passes the contract.</summary>
    public bool Passed => _problems.Count == 0;

    /// <summary>The full path of the committed <c>docs/cloudgpu-run.md</c>, which may not exist yet.</summary>
    /// <returns>The path.</returns>
    public static string CommittedPath() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "cloudgpu-run.md");

    /// <summary>Reads and judges a document with the key and SSH variables of the process environment.</summary>
    /// <param name="markdown">The text; carriage returns are ignored.</param>
    /// <returns>The reading with its problems.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is null.</exception>
    public static CloudGpuRunDocumentParser Parse(string markdown)
    {
        Dictionary<string, string?> environment = [];
        foreach (string name in KeyVariables.Concat(SshVariables))
        {
            environment[name] = Environment.GetEnvironmentVariable(name);
        }

        return Parse(markdown, environment);
    }

    /// <summary>Reads and judges a document against the given values of the key and SSH variables.</summary>
    /// <param name="markdown">The text; carriage returns are ignored.</param>
    /// <param name="environment">The values of <c>NGC_API_KEY</c>, <c>NVIDIA_API_KEY</c>, <c>NEBIUS_API_KEY</c>, <c>CLOUDGPU_SSH_HOST</c> and <c>CLOUDGPU_SSH_USER</c>; a missing, null or blank entry is ignored (no length floor).</param>
    /// <returns>The reading with its problems.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static CloudGpuRunDocumentParser Parse(string markdown, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(environment);

        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        CloudGpuRunDocumentParser parser = new(lines, ReadSections(lines));
        parser._problems.AddRange(parser.HeadingProblems());
        parser._problems.AddRange(parser.InstanceProblems());
        parser._problems.AddRange(parser.CommandsProblems());
        parser._problems.AddRange(parser.RunsProblems());
        parser._problems.AddRange(parser.CostProblems());
        parser._problems.AddRange(parser.TeardownProblems());
        parser._problems.AddRange(parser.Ipv4Problems());
        parser._problems.AddRange(parser.Ipv6Problems());
        parser._problems.AddRange(parser.KeyProblems(environment));
        parser._problems.AddRange(parser.SshTargetProblems(environment));
        return parser;
    }

    /// <summary>Rule: each of the six headings exists as a level 2 heading.</summary>
    /// <returns>One problem per missing heading.</returns>
    public IEnumerable<string> HeadingProblems() =>
        RequiredHeadings.Where(heading => !SectionNamed(heading).Present).Select(static heading => $"the heading \"{heading}\" is missing (a level 2 \"## {heading}\" heading is required)");

    /// <summary>Rule: the Instance section names the preemptible statement, a platform, a preset, a region and an hourly price in USD.</summary>
    /// <returns>The problems; none when the section is absent (the heading rule reports that).</returns>
    public IEnumerable<string> InstanceProblems()
    {
        CloudGpuRunSection section = Instance;
        if (!section.Present)
        {
            yield break;
        }

        if (!section.Body.Contains(PreemptibleStatement, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"Instance: it must state \"{PreemptibleStatement}\"";
        }

        foreach (string field in new[] { "platform", "preset", "region" })
        {
            if (!Regex.IsMatch(section.Body, $@"\b{field}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                yield return $"Instance: the {field} is not named";
            }
        }

        if (UsdValues(section.Body).Count == 0)
        {
            yield return "Instance: the hourly price from the console in USD is missing";
        }
    }

    /// <summary>Rule: the Commands section holds <c>docker logout</c> and writes the NGC key only as <c>$NGC_API_KEY</c>.</summary>
    /// <returns>The problems; none when the section is absent.</returns>
    public IEnumerable<string> CommandsProblems()
    {
        CloudGpuRunSection section = Commands;
        if (!section.Present)
        {
            yield break;
        }

        if (!DockerLogout().IsMatch(section.Body))
        {
            yield return "Commands: no line contains \"docker logout\"";
        }

        string[] lines = section.Body.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (NgcKeyAssignedToText().IsMatch(lines[index]))
            {
                yield return $"Commands: the key must be shown only as $NGC_API_KEY (line {(section.FirstLine + index).ToString(CultureInfo.InvariantCulture)})";
            }
        }
    }

    /// <summary>Rule: the Runs section holds at least one exit code, and an incident comes with the statement that no retry was made past the box or the cap.</summary>
    /// <returns>The problems; none when the section is absent.</returns>
    public IEnumerable<string> RunsProblems()
    {
        CloudGpuRunSection section = Runs;
        if (!section.Present)
        {
            yield break;
        }

        if (!ExitCode().IsMatch(section.Body))
        {
            yield return "Runs: at least one exit code is required (for example \"exit code: 0\")";
        }

        // "no incident" and "Incidents: none" state that nothing went wrong; they are no incident.
        string withoutNegations = NoIncident().Replace(section.Body, string.Empty);
        if (Incident().IsMatch(withoutNegations) && !section.Body.Contains("no retry", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Runs: an incident is recorded without the statement that no retry was made past the box or the cap";
        }
    }

    /// <summary>Rule: the Cost section holds USD and a value, no value above the 30 USD cap, and the stop reason or <c>no stop triggered</c>.</summary>
    /// <returns>The problems; none when the section is absent.</returns>
    public IEnumerable<string> CostProblems()
    {
        CloudGpuRunSection section = Cost;
        if (!section.Present)
        {
            yield break;
        }

        List<decimal> values = UsdValues(section.Body);
        if (values.Count == 0)
        {
            yield return "Cost: it must contain USD and a value (for example \"USD 6.12\")";
        }
        else if (values.Max() > CostCapUsd)
        {
            yield return $"Cost: USD {values.Max().ToString("0.00", CultureInfo.InvariantCulture)} is above the 30 USD cap";
        }

        if (!StopReason().IsMatch(section.Body))
        {
            yield return "Cost: the stop reason (cost-cap or time-box) or \"no stop triggered\" must be stated";
        }
    }

    /// <summary>Rule: the Teardown section says <c>deleted</c> with the user's deletion time and the final cost in USD; while it says <c>pending user confirmation</c> or lacks <c>deleted</c> the teardown is not confirmed.</summary>
    /// <returns>The problems; none when the section is absent.</returns>
    public IEnumerable<string> TeardownProblems()
    {
        CloudGpuRunSection section = Teardown;
        if (!section.Present)
        {
            yield break;
        }

        bool pending = section.Body.Contains("pending user confirmation", StringComparison.OrdinalIgnoreCase);
        if (!Deleted().IsMatch(NotDeleted().Replace(section.Body, string.Empty)))
        {
            yield return pending
                ? "Teardown: the teardown is not confirmed (pending user confirmation)"
                : "Teardown: the teardown is not confirmed (the word \"deleted\" is missing)";
            yield break;
        }

        if (pending)
        {
            yield return "Teardown: it says \"deleted\" and still says \"pending user confirmation\"";
        }

        if (!DeletionTime().IsMatch(section.Body))
        {
            yield return "Teardown: the user's deletion time is missing (for example 2026-11-01T11:05:00Z)";
        }

        List<decimal> values = UsdValues(section.Body);
        if (values.Count == 0)
        {
            yield return "Teardown: the final cost in USD is missing";
        }
        else if (values.Max() > CostCapUsd)
        {
            yield return $"Teardown: USD {values.Max().ToString("0.00", CultureInfo.InvariantCulture)} is above the 30 USD cap";
        }
    }

    /// <summary>Rule: no IPv4 literal anywhere in the document.</summary>
    /// <returns>One problem per line, with the line number and not the address.</returns>
    public IEnumerable<string> Ipv4Problems() =>
        LinesWhere(static line => Ipv4().IsMatch(line)).Select(static number => $"an IPv4 literal at line {number.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>Rule: no IPv6 literal anywhere in the document.</summary>
    /// <returns>One problem per line, with the line number and not the address.</returns>
    public IEnumerable<string> Ipv6Problems() =>
        LinesWhere(static line => Ipv6().IsMatch(line)).Select(static number => $"an IPv6 literal at line {number.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The key-token rule on a text: an <c>nvapi-</c> prefix followed by at least one key character, or a token of 40 or more base64 characters with upper case, lower case and a digit. A hex digest or a NIM profile id (lower case hex) is not a key.</summary>
    /// <param name="text">The text; each line is judged alone.</param>
    /// <returns>Whether a line holds a key-like token.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static bool HasKeyShape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split('\n').Any(static line => IsKeyShaped(line));
    }

    /// <summary>Rule: no key-like token anywhere (see <see cref="HasKeyShape"/>), and no line holds the value of a key variable as a substring.</summary>
    /// <param name="environment">The values of the key variables.</param>
    /// <returns>One problem per line, with the line number and not the token.</returns>
    public IEnumerable<string> KeyProblems(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string[] values = ValuesOf(environment, KeyVariables);
        return LinesWhere(line => IsKeyShaped(line) || values.Any(value => line.Contains(value, StringComparison.Ordinal)))
            .Select(static number => $"a key-like token at line {number.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Rule: an SSH target (<c>name@name</c>) appears only as <c>$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST</c>, and the values of the two SSH variables do not appear.</summary>
    /// <param name="environment">The values of <c>CLOUDGPU_SSH_HOST</c> and <c>CLOUDGPU_SSH_USER</c>.</param>
    /// <returns>One problem per line, with the line number and not the target.</returns>
    public IEnumerable<string> SshTargetProblems(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string[] values = ValuesOf(environment, SshVariables);
        IEnumerable<int> otherTargets = LinesWhere(static line => TargetShape().Matches(line).Any(static match => match.Value.TrimEnd('.') != SshTarget));
        IEnumerable<int> valueHits = LinesWhere(line => values.Any(value => ContainsWord(line, value)));

        return otherTargets.Select(static number => $"an SSH target other than {SshTarget} at line {number.ToString(CultureInfo.InvariantCulture)}")
            .Concat(valueHits.Select(static number => $"the value of CLOUDGPU_SSH_HOST or CLOUDGPU_SSH_USER at line {number.ToString(CultureInfo.InvariantCulture)}"));
    }

    // The USD values of a text in any order and spelling: "USD 6.12", "6.12 USD", "$6.12", "US$6.12"; the thousands separator is read ("USD 1,031.20").
    private static List<decimal> UsdValues(string text) =>
    [
        .. UsdAmount().Matches(text).Select(static match => decimal.Parse(match.Groups["n"].Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture)),
    ];

    private static bool IsKeyShaped(string line) => NvapiKey().IsMatch(line) || HasLongToken(line);

    private static bool HasLongToken(string line) =>
        LongToken().Matches(line).Any(static match => match.Value.Any(char.IsUpper) && match.Value.Any(char.IsLower) && match.Value.Any(char.IsDigit));

    private static string[] ValuesOf(IReadOnlyDictionary<string, string?> environment, string[] names) =>
        [.. names.Select(name => environment.GetValueOrDefault(name)).OfType<string>().Where(static value => !string.IsNullOrWhiteSpace(value))];

    // An ordinal hit on word boundaries: the characters around it are not letters, digits, underscores or hyphens.
    private static bool ContainsWord(string line, string value)
    {
        for (int start = line.IndexOf(value, StringComparison.Ordinal); start >= 0; start = line.IndexOf(value, start + 1, StringComparison.Ordinal))
        {
            int end = start + value.Length;
            if ((start == 0 || !IsWordCharacter(line[start - 1])) && (end == line.Length || !IsWordCharacter(line[end])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character is '_' or '-';

    private static Dictionary<string, CloudGpuRunSection> ReadSections(string[] lines)
    {
        Dictionary<string, CloudGpuRunSection> sections = new(StringComparer.Ordinal);
        string? current = null;
        int firstLine = 0;
        List<string> body = [];
        bool inFence = false;

        void Close()
        {
            if (current is not null)
            {
                _ = sections.TryAdd(current, new CloudGpuRunSection(current, true, string.Join('\n', body), firstLine));
            }
        }

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.TrimStart().StartsWith(FenceMarker, StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence && line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            {
                Close();
                current = line[HeadingPrefix.Length..].Trim();
                firstLine = index + 2;
                body = [];
            }
            else if (current is not null)
            {
                body.Add(line);
            }
        }

        Close();
        return sections;
    }

    private CloudGpuRunSection SectionNamed(string name) => _sections.GetValueOrDefault(name) ?? new CloudGpuRunSection(name, false, string.Empty, 0);

    // The 1-based numbers of the lines that satisfy the test.
    private IEnumerable<int> LinesWhere(Func<string, bool> test) =>
        _lines.Select(static (line, index) => (line, number: index + 1)).Where(pair => test(pair.line)).Select(static pair => pair.number);

    // An amount is not the tail of a date, a time, a version or a longer number: no digit, colon, dot, comma or hyphen comes before it.
    [GeneratedRegex(@"(?<![A-Za-z])(?:USD|US\$)\s*(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)|(?<![A-Za-z0-9])\$\s*(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)|(?<![\d:.,-])(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*USD(?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex UsdAmount();

    [GeneratedRegex(@"(?<![A-Za-z\d.])\d{1,3}(\.\d{1,3}){3}\b(?!\.\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();

    [GeneratedRegex(Ipv6Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"nvapi-[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NvapiKey();

    [GeneratedRegex(@"(?<![A-Za-z0-9+/_=])[A-Za-z0-9+/_]{40,}={0,2}(?![A-Za-z0-9+/_])", RegexOptions.CultureInvariant)]
    private static partial Regex LongToken();

    // name@name, except an image reference by digest (name@sha256:<hex>), which docker prints and which is no SSH target.
    [GeneratedRegex(@"[A-Za-z0-9_.${}-]+@(?!sha256:[0-9A-Fa-f]{8,})[A-Za-z0-9_.${}-]+", RegexOptions.CultureInvariant)]
    private static partial Regex TargetShape();

    [GeneratedRegex(@"docker\s+logout", RegexOptions.CultureInvariant)]
    private static partial Regex DockerLogout();

    // NGC_API_KEY= followed by anything that is not a variable reference.
    [GeneratedRegex(@"NGC_API_KEY\s*=\s*[""']?(?!\$)(?=[^\s""'])", RegexOptions.CultureInvariant)]
    private static partial Regex NgcKeyAssignedToText();

    [GeneratedRegex(@"\bexit(?:\s+codes?)?\s*[:=]?\s*\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExitCode();

    [GeneratedRegex(@"\b(?:lost|broken|incident)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Incident();

    [GeneratedRegex(@"\bno\s+incidents?\b|\bincidents?\s*:\s*none\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoIncident();

    [GeneratedRegex(@"no stop triggered|\bstop\s*reason\W{0,4}(?:cost-cap|time-box)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StopReason();

    [GeneratedRegex(@"\bdeleted\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Deleted();

    [GeneratedRegex(@"\bnot\s+deleted\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotDeleted();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex DeletionTime();
}