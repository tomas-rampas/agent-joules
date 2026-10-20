using System.Text.RegularExpressions;

namespace AgentJoules.TestSupport;

/// <summary>One step of the <c>steps:</c> list of the workflow, read by plain line logic.</summary>
/// <param name="Line">The 1-based line of the step's dash.</param>
/// <param name="Name">The <c>name:</c> value, or null.</param>
/// <param name="Uses">The <c>uses:</c> value, or null.</param>
/// <param name="Shell">The step's own <c>shell:</c> value, or null when the step inherits the job default.</param>
/// <param name="RunHeader">The block scalar indicator after <c>run:</c> (<c>|</c>, <c>&gt;</c>, ...), or an empty string for a plain scalar or no run.</param>
/// <param name="Run">The lines of the <c>run:</c> value (one for a scalar, one per line for a block scalar); empty for a step without <c>run:</c>.</param>
/// <param name="With">The <c>with:</c> entries as key and value.</param>
/// <param name="Keys">Every key of the step, in file order, whatever it is called.</param>
internal sealed record CiWorkflowStep(
    int Line,
    string? Name,
    string? Uses,
    string? Shell,
    string RunHeader,
    List<string> Run,
    Dictionary<string, string> With,
    List<string> Keys);

/// <summary>
/// AJ-038 (REQ-024): the rules <c>.github/workflows/ci.yml</c> must satisfy, as pure functions of the workflow TEXT so the unit tests and the
/// scenarios run the same code against the real text and against modified copies. There is no YAML package in the repository, so the file is read
/// by indentation and prefix, which is exact for the one shape the workflow has (block mappings, one block scalar for the probe step). Every rule
/// returns the violations it found, one readable line each; an empty list means the rule holds. The rules are deliberately strict: a step may carry
/// only the keys name, uses, with, run and shell, so nothing can switch a step off or soften it (<c>if</c>, <c>continue-on-error</c>, <c>env</c>)
/// without a violation. Nothing here writes a file.
/// </summary>
internal static partial class CiWorkflowRules
{
    /// <summary>The restore command of the story's Definition of Done.</summary>
    internal const string RestoreCommand = "dotnet restore";

    /// <summary>The strict build command: warnings are errors and the build is not incremental (an incremental build hides warnings).</summary>
    internal const string BuildCommand = "dotnet build -warnaserror -tl:off --no-incremental --no-restore";

    /// <summary>The format check; it relies on the eol=lf line of .gitattributes.</summary>
    internal const string FormatCommand = "dotnet format --verify-no-changes --no-restore";

    /// <summary>The test command.</summary>
    internal const string TestCommand = "dotnet test --no-build";

    /// <summary>The first line of the probe step: the JSON goes to a file through tee while the dotnet exit code stays the step's (pipefail).</summary>
    internal const string ProbeCommand = "dotnet run --project src/AgentJoules.Cli --no-build -- probe --json | tee probe.json";

    /// <summary>The second line of the probe step: the hosted runner must report no GPU.</summary>
    internal const string ProbeCheck = "grep -q '\"gpuPresent\": *false' probe.json";

    /// <summary>The last step: a tracked file that holds a key-shaped token fails the job. The class after the hyphen is what keeps this text from matching itself.</summary>
    internal const string KeyScanCommand = "git grep -nE \"nvapi-[A-Za-z0-9_-]{8,}\" && exit 1 || exit 0";

    /// <summary>
    /// The line the secret mutator adds after the job timeout line: a job-level <c>env:</c> flow mapping whose expression is quoted, so the line is
    /// valid YAML (an unquoted <c>${{ }}</c> inside a flow mapping is not). It names a secret, never a value.
    /// </summary>
    internal const string SecretReferenceLine = "    env: { NVIDIA_API_KEY: '${{ secrets.NVIDIA_API_KEY }}' }";

    private const string WorkflowRelativePath = ".github/workflows/ci.yml";
    private const string RepositoryMarker = "global.json";
    private const string TimeoutLine = "timeout-minutes: 20";
    private const string MatrixLine = "os: [windows-latest, ubuntu-latest]";
    private const string RunsOnLine = "runs-on: ${{ matrix.os }}";
    private const string ExpressionStart = "${{";
    private const string BashShell = "bash";

    /// <summary>The step sequence of criterion 2: the <c>uses:</c> value of an action step, the first <c>run:</c> line of a command step.</summary>
    internal static readonly string[] StepOrder =
    [
        "actions/checkout@v4",
        "actions/setup-dotnet@v6",
        RestoreCommand,
        BuildCommand,
        FormatCommand,
        TestCommand,
        ProbeCommand,
        KeyScanCommand,
    ];

    /// <summary>The trigger names of criterion 4.</summary>
    internal static readonly string[] RequiredTriggers = ["push", "pull_request"];

    /// <summary>The strings of criterion 1 that must appear anywhere in the text.</summary>
    internal static readonly string[] RequiredStrings =
    [
        "windows-latest",
        "ubuntu-latest",
        RunsOnLine,
        "dotnet build -warnaserror -tl:off",
        "dotnet test",
        "probe --json",
    ];

    /// <summary>The only keys a step may carry.</summary>
    internal static readonly string[] AllowedStepKeys = ["name", "uses", "with", "run", "shell"];

    /// <summary>The only keys the job may carry.</summary>
    internal static readonly string[] AllowedJobKeys = ["runs-on", "timeout-minutes", "strategy", "defaults", "steps"];

    /// <summary>The keys that may not appear anywhere in the workflow: each can switch a step off, soften it or inject values.</summary>
    internal static readonly string[] ForbiddenKeys = ["env", "if", "continue-on-error"];

    /// <summary>
    /// Reads <c>.github/workflows/ci.yml</c> with LF line endings; the repository root is the nearest parent of the test assembly that holds
    /// <c>global.json</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No parent folder holds global.json.</exception>
    /// <exception cref="FileNotFoundException">The workflow file does not exist.</exception>
    internal static string ReadWorkflowText()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepositoryMarker)))
            {
                return File.ReadAllText(Path.Combine(directory.FullName, WorkflowRelativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        throw new InvalidOperationException(RepositoryMarker + " was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Splits the text into lines, accepting LF and CRLF; a text that ends with a newline yields a final empty line.</summary>
    internal static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>The 1-based number of the first line that equals <paramref name="line"/> exactly, or 0 when there is none.</summary>
    internal static int LineOf(string text, string line) => Array.IndexOf(Lines(text), line) + 1;

    /// <summary>
    /// Emulates <c>grep -n</c> with a fixed string: one entry per line that contains <paramref name="needle"/>, as the 1-based line number, a colon
    /// and the line, the format grep prints. An empty result is grep's exit code 1; a non-empty one is exit code 0.
    /// </summary>
    internal static List<string> Grep(string text, string needle)
    {
        string[] lines = Lines(text);
        List<string> hits = [];
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains(needle, StringComparison.Ordinal))
            {
                hits.Add($"{index + 1}:{lines[index]}");
            }
        }

        return hits;
    }

    /// <summary>Emulates <c>grep -n 'secrets\.'</c>: every line that references the secrets context.</summary>
    internal static List<string> SecretReferences(string text) => Grep(text, "secrets.");

    /// <summary>
    /// Every line that has the word <c>secrets</c> in any case, as <c>grep -niw secrets</c> would: the spellings <c>Secrets.X</c>,
    /// <c>secrets['X']</c> and <c>toJSON(secrets)</c> do not contain the fixed string <c>secrets.</c> but are the same context.
    /// </summary>
    internal static List<string> SecretContextReferences(string text)
    {
        string[] lines = Lines(text);
        List<string> hits = [];
        for (int index = 0; index < lines.Length; index++)
        {
            if (SecretsWord().IsMatch(lines[index]))
            {
                hits.Add($"{index + 1}:{lines[index]}");
            }
        }

        return hits;
    }

    /// <summary>Every line that mentions the <c>pull_request_target</c> trigger, which would run fork code with repository privileges.</summary>
    internal static List<string> PullRequestTargetReferences(string text) => Grep(text, "pull_request_target");

    /// <summary>One violation for each string of <paramref name="required"/> that the text does not contain.</summary>
    internal static List<string> Missing(string text, params string[] required)
    {
        List<string> violations = [];
        foreach (string needle in required)
        {
            if (!text.Contains(needle, StringComparison.Ordinal))
            {
                violations.Add("the workflow does not contain \"" + needle + "\"");
            }
        }

        return violations;
    }

    /// <summary>Reads the steps of the first <c>steps:</c> list; empty when there is none.</summary>
    internal static List<CiWorkflowStep> Steps(string text)
    {
        string[] lines = Lines(text);
        List<CiWorkflowStep> steps = [];
        int start = Array.FindIndex(lines, candidate => candidate.Trim() == "steps:");
        if (start < 0)
        {
            return steps;
        }

        int stepsIndent = Indent(lines[start]);
        int itemIndent = -1;
        int blockLine = 0;
        List<(int Indent, string Content)>? block = null;
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int indent = Indent(line);

            // A comment is skipped until the first step has fixed the item indent, whatever its own indent; afterwards a comment at or left of the
            // item indent is skipped and a deeper one is kept (it may be script text inside a block scalar).
            if (trimmed[0] == '#' && (itemIndent < 0 || indent <= itemIndent))
            {
                continue;
            }

            bool isDash = trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal);
            if (indent < stepsIndent || (indent == stepsIndent && !isDash))
            {
                break;
            }

            if (itemIndent < 0)
            {
                itemIndent = indent;
            }

            if (isDash && indent == itemIndent)
            {
                if (block is not null)
                {
                    steps.Add(ParseStep(blockLine, block));
                }

                block = [];
                blockLine = index + 1;
                string rest = trimmed[1..].TrimStart();
                if (rest.Length > 0)
                {
                    block.Add((itemIndent + (trimmed.Length - rest.Length), rest));
                }
            }
            else
            {
                block?.Add((indent, trimmed));
            }
        }

        if (block is not null)
        {
            steps.Add(ParseStep(blockLine, block));
        }

        return steps;
    }

    /// <summary>The step that runs <c>probe --json</c>, or null when there is none.</summary>
    internal static CiWorkflowStep? ProbeStep(string text) =>
        Steps(text).FirstOrDefault(candidate => candidate.Run.Any(line => line.Contains("probe --json", StringComparison.Ordinal)));

    /// <summary>The restore, build and test lines must each be exactly the Definition of Done string, and each must exist.</summary>
    internal static List<string> LocalCommandViolations(string text)
    {
        List<string> violations = [];
        violations.AddRange(CommandViolations(text, "dotnet restore", RestoreCommand));
        violations.AddRange(CommandViolations(text, "dotnet build", BuildCommand));
        violations.AddRange(CommandViolations(text, "dotnet test", TestCommand));
        return violations;
    }

    /// <summary>
    /// The build step must be the strict build command: any run line that starts with <c>dotnet build</c> but is not exactly
    /// <see cref="BuildCommand"/> (for instance one without <c>-warnaserror</c>) is reported with its text.
    /// </summary>
    internal static List<string> BuildStepViolations(string text) => CommandViolations(text, "dotnet build", BuildCommand);

    /// <summary>The steps, by their <c>uses:</c> value or first <c>run:</c> line, must be exactly <see cref="StepOrder"/>.</summary>
    internal static List<string> StepOrderViolations(string text)
    {
        List<string> actual = [.. Steps(text).Select(DescribeStep)];
        return actual.SequenceEqual(StepOrder, StringComparer.Ordinal)
            ? []
            : ["the steps are [" + string.Join(" | ", actual) + "], expected [" + string.Join(" | ", StepOrder) + "]"];
    }

    /// <summary>The setup-dotnet step must read the SDK version from global.json.</summary>
    internal static List<string> SetupDotnetViolations(string text)
    {
        CiWorkflowStep? setup = Steps(text).FirstOrDefault(step => step.Uses == "actions/setup-dotnet@v6");
        return setup is null
            ? ["no step uses actions/setup-dotnet@v6"]
            : setup.With.GetValueOrDefault("global-json-file") == "global.json"
                ? []
                : ["the setup-dotnet step does not set global-json-file: global.json"];
    }

    /// <summary>The checkout step must not leave the job token in <c>.git/config</c> for the later steps that run repository and NuGet code.</summary>
    internal static List<string> CheckoutViolations(string text)
    {
        CiWorkflowStep? checkout = Steps(text).FirstOrDefault(step => step.Uses == "actions/checkout@v4");
        return checkout is null
            ? ["no step uses actions/checkout@v4"]
            : checkout.With.GetValueOrDefault("persist-credentials") == "false"
                ? []
                : ["the checkout step does not set persist-credentials: false"];
    }

    /// <summary>
    /// The probe step must declare <c>shell: bash</c> itself and its run lines must be exactly <see cref="ProbeCommand"/> then
    /// <see cref="ProbeCheck"/>: nothing between them can swallow the exit code.
    /// </summary>
    internal static List<string> ProbeStepViolations(string text)
    {
        CiWorkflowStep? probe = ProbeStep(text);
        if (probe is null)
        {
            return ["no step runs probe --json"];
        }

        List<string> violations = [];
        if (probe.Shell != BashShell)
        {
            violations.Add($"line {probe.Line}: the probe step does not declare shell: bash");
        }

        string[] expected = [ProbeCommand, ProbeCheck];
        if (!probe.Run.SequenceEqual(expected, StringComparer.Ordinal))
        {
            violations.Add($"line {probe.Line}: the probe step runs [" + string.Join(" | ", probe.Run) + "], expected [" + string.Join(" | ", expected) + "]");
        }

        return violations;
    }

    /// <summary>Every step with a <c>run:</c> must run under bash, through its own <c>shell:</c> or the job's <c>defaults: run: shell:</c>.</summary>
    internal static List<string> ShellViolations(string text)
    {
        string? jobShell = DefaultShell(text);
        List<string> violations = [];
        foreach (CiWorkflowStep step in Steps(text).Where(candidate => candidate.Run.Count > 0))
        {
            if ((step.Shell ?? jobShell) != BashShell)
            {
                violations.Add($"line {step.Line}: the run step \"{step.Name}\" does not run under shell: bash");
            }
        }

        return violations;
    }

    /// <summary>Every <c>shell:</c> line anywhere in the file (a step, a job default, another job) must read <c>shell: bash</c>.</summary>
    internal static List<string> ShellValueViolations(string text)
    {
        string[] lines = Lines(text);
        List<string> violations = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string content = StripDash(lines[index].Trim());
            if (content.StartsWith("shell:", StringComparison.Ordinal) && content != "shell: bash")
            {
                violations.Add($"a shell other than bash at {index + 1}:{lines[index]}");
            }
        }

        return violations;
    }

    /// <summary>A step may carry only the keys name, uses, with, run and shell; any other key is reported with the step and the key.</summary>
    internal static List<string> StepKeyViolations(string text)
    {
        List<string> violations = [];
        foreach (CiWorkflowStep step in Steps(text))
        {
            violations.AddRange(step.Keys
                .Where(key => !AllowedStepKeys.Contains(key))
                .Select(key => $"line {step.Line}: the step \"{step.Name}\" has the key \"{key}\""));
        }

        return violations;
    }

    /// <summary>
    /// Only a <c>|</c> block scalar is accepted for <c>run:</c>; a folded scalar (<c>&gt;</c>) joins its lines into one command. Every step except the
    /// probe step has exactly one command line.
    /// </summary>
    internal static List<string> RunStyleViolations(string text)
    {
        int probeLine = ProbeStep(text)?.Line ?? 0;
        List<string> violations = [];
        foreach (CiWorkflowStep step in Steps(text).Where(candidate => candidate.Run.Count > 0 || candidate.RunHeader.Length > 0))
        {
            if (step.RunHeader.Length > 0 && step.RunHeader != "|")
            {
                violations.Add($"line {step.Line}: the step \"{step.Name}\" has a \"{step.RunHeader}\" run scalar, only | is accepted");
            }

            if (step.Line != probeLine && step.Run.Count != 1)
            {
                violations.Add($"line {step.Line}: the step \"{step.Name}\" has {step.Run.Count} run lines, expected exactly 1");
            }
        }

        return violations;
    }

    /// <summary>
    /// The file must hold exactly one <c>permissions:</c> line, at column 0, granting <c>contents: read</c> and no write permission: a job-level
    /// block would be a second line.
    /// </summary>
    internal static List<string> PermissionsViolations(string text)
    {
        string[] lines = Lines(text);
        List<string> violations = [];
        int count = lines.Count(line => line.Trim().StartsWith("permissions:", StringComparison.Ordinal));
        if (count != 1)
        {
            violations.Add($"the workflow has {count} permissions: lines, expected exactly 1");
        }

        int start = Array.FindIndex(lines, line => line == "permissions:");
        if (start < 0)
        {
            violations.Add("the workflow has no workflow-level permissions: block");
            return violations;
        }

        List<string> block = IndentedBlock(lines, start);
        if (!block.Contains("contents: read"))
        {
            violations.Add("the workflow-level permissions: block does not grant contents: read");
        }

        violations.AddRange(block.Where(entry => entry.Contains(": write", StringComparison.Ordinal)).Select(entry => "the workflow grants " + entry));
        return violations;
    }

    /// <summary>The names under the top-level <c>on:</c>, in block form (<c>push:</c>) or flow form (<c>[push, pull_request]</c>).</summary>
    internal static List<string> Triggers(string text)
    {
        string[] lines = Lines(text);
        int start = Array.FindIndex(lines, candidate => candidate == "on:" || candidate.StartsWith("on: ", StringComparison.Ordinal));
        List<string> names = [];
        if (start < 0)
        {
            return names;
        }

        string rest = lines[start][3..].Trim();
        if (rest.Length > 0)
        {
            names.AddRange(rest.Trim('[', ']').Split(',').Select(name => name.Trim()).Where(name => name.Length > 0));
            return names;
        }

        int childIndent = -1;
        for (int index = start + 1; index < lines.Length; index++)
        {
            string trimmed = lines[index].Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            int indent = Indent(lines[index]);
            if (indent == 0)
            {
                break;
            }

            childIndent = childIndent < 0 ? indent : childIndent;
            if (indent == childIndent)
            {
                int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
                names.Add(colon < 0 ? trimmed : trimmed[..colon]);
            }
        }

        return names;
    }

    /// <summary>The workflow must trigger on <c>push</c> and <c>pull_request</c>.</summary>
    internal static List<string> TriggerViolations(string text)
    {
        List<string> triggers = Triggers(text);
        List<string> violations = [];
        foreach (string required in RequiredTriggers)
        {
            if (!triggers.Contains(required))
            {
                violations.Add("the workflow does not trigger on " + required);
            }
        }

        return violations;
    }

    /// <summary>The workflow must declare exactly one job with exactly one <c>steps:</c> list, so every rule above reads all of the work.</summary>
    internal static List<string> JobViolations(string text)
    {
        List<(int Indent, string Text)> entries = Entries(Lines(text));
        int jobs = entries.FindIndex(entry => entry.Indent == 0 && entry.Text == "jobs:");
        if (jobs < 0)
        {
            return ["the workflow has no jobs: block"];
        }

        List<string> violations = [];
        int jobCount = Children(entries, jobs).Count;
        if (jobCount != 1)
        {
            violations.Add($"the workflow declares {jobCount} jobs, expected exactly 1");
        }

        int stepsLists = entries.Count(entry => entry.Text == "steps:");
        if (stepsLists != 1)
        {
            violations.Add($"the workflow has {stepsLists} steps: lists, expected exactly 1");
        }

        return violations;
    }

    /// <summary>
    /// The job may carry only the keys runs-on, timeout-minutes, strategy, defaults and steps (the first job; a second job is a violation of
    /// <see cref="JobViolations"/>). A quoted key such as <c>"if"</c> is not in the set either.
    /// </summary>
    internal static List<string> JobKeyViolations(string text)
    {
        List<(int Indent, string Text)> entries = Entries(Lines(text));
        int jobs = entries.FindIndex(entry => entry.Indent == 0 && entry.Text == "jobs:");
        List<int> names = jobs < 0 ? [] : Children(entries, jobs);
        if (names.Count == 0)
        {
            return [];
        }

        string jobName = entries[names[0]].Text.TrimEnd(':');
        List<string> violations = [];
        foreach (int property in Children(entries, names[0]))
        {
            string entryText = entries[property].Text;
            int colon = entryText.IndexOf(':', StringComparison.Ordinal);
            string key = colon < 0 ? entryText : entryText[..colon];
            if (!AllowedJobKeys.Contains(key))
            {
                violations.Add($"the job \"{jobName}\" has the key \"{key}\"");
            }
        }

        return violations;
    }

    /// <summary>The job must carry <c>timeout-minutes: 20</c> as a property of its own, at the job's indent (a comment or a deeper line does not count).</summary>
    internal static List<string> TimeoutViolations(string text)
    {
        List<(int Indent, string Text)> entries = Entries(Lines(text));
        return FirstJobProperties(entries).Any(index => entries[index].Text == TimeoutLine)
            ? []
            : ["the job does not declare " + TimeoutLine + " at its own indent"];
    }

    /// <summary>The job's <c>strategy: matrix:</c> must hold exactly one entry, the line <c>os: [windows-latest, ubuntu-latest]</c>: an <c>exclude</c> or <c>include</c> sibling is a violation.</summary>
    internal static List<string> MatrixViolations(string text)
    {
        List<(int Indent, string Text)> entries = Entries(Lines(text));
        List<int> properties = FirstJobProperties(entries);
        int strategy = properties.FirstOrDefault(index => entries[index].Text == "strategy:", -1);
        int matrix = strategy < 0 ? -1 : Children(entries, strategy).FirstOrDefault(index => entries[index].Text == "matrix:", -1);
        List<int> entriesOfMatrix = matrix < 0 ? [] : Children(entries, matrix);
        bool exact = entriesOfMatrix.Count == 1 && entries[entriesOfMatrix[0]].Text == MatrixLine;
        return exact ? [] : ["the job's strategy: matrix: is not exactly " + MatrixLine];
    }

    /// <summary>
    /// The text <c>${{</c> must occur exactly once, on the <c>runs-on</c> line: an expression anywhere else could read the secrets context, the
    /// event payload or another input.
    /// </summary>
    internal static List<string> ExpressionViolations(string text)
    {
        string[] lines = Lines(text);
        List<string> violations = [];
        int count = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(ExpressionStart, StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            if (lines[index].Trim() != RunsOnLine)
            {
                violations.Add($"{index + 1}:{lines[index]}: an expression outside the runs-on line");
            }
        }

        if (count != 1)
        {
            violations.Add($"the workflow has {count} expressions, expected exactly 1 (the runs-on line)");
        }

        return violations;
    }

    /// <summary>The keys <c>env</c>, <c>if</c> and <c>continue-on-error</c> may not appear anywhere (workflow, job or step level, block or flow form).</summary>
    internal static List<string> ForbiddenKeyViolations(string text)
    {
        string[] lines = Lines(text);
        List<string> violations = [];
        foreach (string key in ForbiddenKeys)
        {
            for (int index = 0; index < lines.Length; index++)
            {
                if (StripQuotes(StripDash(lines[index].Trim())).StartsWith(key + ":", StringComparison.Ordinal))
                {
                    violations.Add($"the forbidden key \"{key}:\" at {index + 1}:{lines[index]}");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Every rule at once, the verdict of <c>CiWorkflowTests</c> on a workflow text: empty when the text satisfies criteria 1 to 4, otherwise one
    /// line per violation, in the order of the rules below.
    /// </summary>
    internal static List<string> Violations(string text)
    {
        List<string> violations = [];
        violations.AddRange(Missing(text, RequiredStrings));
        violations.AddRange(SecretReferences(text).Select(hit => "secrets reference at " + hit));
        violations.AddRange(SecretContextReferences(text).Select(hit => "secrets context at " + hit));
        violations.AddRange(PullRequestTargetReferences(text).Select(hit => "pull_request_target at " + hit));
        violations.AddRange(LocalCommandViolations(text));
        violations.AddRange(StepOrderViolations(text));
        violations.AddRange(SetupDotnetViolations(text));
        violations.AddRange(CheckoutViolations(text));
        violations.AddRange(ProbeStepViolations(text));
        violations.AddRange(ShellViolations(text));
        violations.AddRange(ShellValueViolations(text));
        violations.AddRange(StepKeyViolations(text));
        violations.AddRange(RunStyleViolations(text));
        violations.AddRange(PermissionsViolations(text));
        violations.AddRange(TriggerViolations(text));
        violations.AddRange(JobViolations(text));
        violations.AddRange(JobKeyViolations(text));
        violations.AddRange(TimeoutViolations(text));
        violations.AddRange(MatrixViolations(text));
        violations.AddRange(ExpressionViolations(text));
        violations.AddRange(ForbiddenKeyViolations(text));
        return violations;
    }

    /// <summary>A copy of <paramref name="text"/> with a secret reference added after the job timeout line (scenario 4); the original is untouched.</summary>
    internal static string WithSecretReference(string text) => InsertAfter(text, TimeoutLine, SecretReferenceLine);

    /// <summary>A copy of <paramref name="text"/> whose build step has lost <c>-warnaserror</c> (scenario 5).</summary>
    /// <exception cref="InvalidOperationException">The text has no strict build line to change.</exception>
    internal static string WithoutWarningsAsErrors(string text)
    {
        string changed = text.Replace(BuildCommand, "dotnet build -tl:off --no-incremental --no-restore", StringComparison.Ordinal);
        return changed == text ? throw new InvalidOperationException("the text has no line \"" + BuildCommand + "\"") : changed;
    }

    /// <summary>A copy of <paramref name="text"/> with a <c>pull_request_target</c> trigger added under <c>on:</c> (scenario 6).</summary>
    internal static string WithPullRequestTarget(string text) => InsertAfter(text, "pull_request:", "  pull_request_target:");

    [GeneratedRegex("(?i)\\bsecrets\\b", RegexOptions.CultureInvariant)]
    private static partial Regex SecretsWord();

    private static string InsertAfter(string text, string anchor, string added)
    {
        List<string> lines = [.. Lines(text)];
        int index = lines.FindIndex(line => line.Trim() == anchor);
        if (index < 0)
        {
            throw new InvalidOperationException("the text has no line \"" + anchor + "\"");
        }

        lines.Insert(index + 1, added);
        return string.Join('\n', lines);
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    // A quoted key reads as the bare key: "if": false becomes if: false, so a quote cannot hide a forbidden key.
    private static string StripQuotes(string content)
    {
        if (content.Length < 3 || (content[0] != '"' && content[0] != '\''))
        {
            return content;
        }

        int close = content.IndexOf(content[0], 1);
        return close > 0 && close + 1 < content.Length && content[close + 1] == ':' ? content[1..close] + content[(close + 1)..] : content;
    }

    private static string StripDash(string trimmed) => trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..].TrimStart() : trimmed;

    private static string DescribeStep(CiWorkflowStep step) => step.Uses ?? (step.Run.Count > 0 ? step.Run[0] : "(no uses and no run)");

    private static List<string> CommandViolations(string text, string prefix, string expected)
    {
        List<string> runLines = [.. Steps(text).SelectMany(step => step.Run).Where(line => line.StartsWith(prefix, StringComparison.Ordinal))];
        return runLines.Count == 0
            ? ["no step runs " + expected]
            : [.. runLines.Where(line => line != expected).Select(line => "a step runs \"" + line + "\" instead of \"" + expected + "\"")];
    }

    private static string? DefaultShell(string text)
    {
        string[] lines = Lines(text);
        int start = Array.FindIndex(lines, line => line.Trim() == "defaults:");
        return start < 0
            ? null
            : IndentedBlock(lines, start)
                .Where(entry => entry.StartsWith("shell:", StringComparison.Ordinal))
                .Select(entry => entry["shell:".Length..].Trim())
                .FirstOrDefault();
    }

    // The trimmed, non-blank, non-comment lines below the line at start that are indented deeper than it.
    private static List<string> IndentedBlock(string[] lines, int start)
    {
        int indent = Indent(lines[start]);
        List<string> block = [];
        for (int index = start + 1; index < lines.Length; index++)
        {
            string trimmed = lines[index].Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (Indent(lines[index]) <= indent)
            {
                break;
            }

            block.Add(trimmed);
        }

        return block;
    }

    // The non-blank, non-comment lines as their indent and trimmed text.
    private static List<(int Indent, string Text)> Entries(string[] lines)
    {
        List<(int Indent, string Text)> entries = [];
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed[0] != '#')
            {
                entries.Add((Indent(line), trimmed));
            }
        }

        return entries;
    }

    // The indexes of the direct children of the entry at parent: the lines below it, deeper than it, at the indent of the first of them.
    private static List<int> Children(List<(int Indent, string Text)> entries, int parent)
    {
        List<int> children = [];
        int childIndent = -1;
        for (int index = parent + 1; index < entries.Count && entries[index].Indent > entries[parent].Indent; index++)
        {
            childIndent = childIndent < 0 ? entries[index].Indent : childIndent;
            if (entries[index].Indent == childIndent)
            {
                children.Add(index);
            }
        }

        return children;
    }

    // The indexes of the properties of the first job; empty when there is no jobs: block or no job.
    private static List<int> FirstJobProperties(List<(int Indent, string Text)> entries)
    {
        int jobs = entries.FindIndex(entry => entry.Indent == 0 && entry.Text == "jobs:");
        if (jobs < 0)
        {
            return [];
        }

        List<int> names = Children(entries, jobs);
        return names.Count == 0 ? [] : Children(entries, names[0]);
    }

    private static CiWorkflowStep ParseStep(int line, List<(int Indent, string Content)> block)
    {
        string? name = null;
        string? uses = null;
        string? shell = null;
        string runHeader = string.Empty;
        List<string> run = [];
        List<string> keys = [];
        Dictionary<string, string> inputs = new(StringComparer.Ordinal);
        int propertyIndent = block.Count > 0 ? block[0].Indent : 0;
        for (int index = 0; index < block.Count; index++)
        {
            (int indent, string content) = block[index];
            int colon = content.IndexOf(':', StringComparison.Ordinal);
            if (indent != propertyIndent || content[0] == '#' || colon <= 0)
            {
                continue;
            }

            string key = content[..colon];
            string value = content[(colon + 1)..].Trim();
            keys.Add(key);
            List<string> children = [.. block.Skip(index + 1).TakeWhile(entry => entry.Indent > propertyIndent).Select(entry => entry.Content)];
            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "uses":
                    uses = value;
                    break;
                case "shell":
                    shell = value;
                    break;
                case "run":
                    if (value.Length > 0 && (value[0] == '|' || value[0] == '>'))
                    {
                        runHeader = value;
                        run.AddRange(children);
                    }
                    else if (value.Length > 0 || children.Count > 0)
                    {
                        // A plain scalar continues on the deeper lines below it, and YAML joins them with single spaces into one command.
                        run.Add(string.Join(' ', children.Prepend(value).Where(part => part.Length > 0)));
                    }

                    break;
                case "with":
                    foreach (string child in children)
                    {
                        int childColon = child.IndexOf(':', StringComparison.Ordinal);
                        if (childColon > 0)
                        {
                            inputs[child[..childColon]] = child[(childColon + 1)..].Trim();
                        }
                    }

                    break;
            }
        }

        return new CiWorkflowStep(line, name, uses, shell, runHeader, run, inputs, keys);
    }
}