using System.Globalization;
using System.IO.Compression;
using System.Text;

using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The documents and folders of the negative scenarios of AJ-157, AJ-158 and AJ-159, built in memory: a checklist, a build log, release notes and a folder with the three zip assets that satisfy every rule, so that a
/// scenario changes one thing with <see cref="Change"/> and the rule under test is the only one that can fail. The fixtures never read the repository except for the committed run files of the report scenario.
/// </summary>
public static class SubmissionFixtures
{
    private const int GitTimeoutSeconds = 30;
    /// <summary>The regeneration commands of the cut-off rows of the checklist fixture.</summary>
    public const string Regenerate = "`AJ report --in results/ --out docs/report/ --update-readme --render-content` then `AJ report --verify-readme`";

    /// <summary>The first line of the release command block of the checklist fixture.</summary>
    public const string ReleaseCommand = "gh release create v0.1.0 --repo tomas-rampas/agent-joules --target main --title \"Agent Joules v0.1.0\" --notes-file docs/release-notes-v0.1.0.md artifacts/agent-joules-v0.1.0-fdd.zip artifacts/agent-joules-v0.1.0-results.zip artifacts/agent-joules-v0.1.0-report.zip";

    /// <summary>Replaces the single occurrence of a text, failing loudly when the text is absent so that a scenario cannot silently test nothing.</summary>
    /// <param name="text">The document.</param>
    /// <param name="from">The text to replace.</param>
    /// <param name="to">The replacement.</param>
    /// <returns>The changed document.</returns>
    /// <exception cref="InvalidOperationException">The text does not occur.</exception>
    public static string Change(string text, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(from);
        return text.Contains(from, StringComparison.Ordinal) ? text.Replace(from, to, StringComparison.Ordinal) : throw new InvalidOperationException("The fixture does not contain: " + from);
    }

    /// <summary>A checklist that satisfies every AJ-158 rule and the release-command rules of AJ-157.</summary>
    /// <returns>The text.</returns>
    public static string Checklist()
    {
        var text = new StringBuilder();
        text.Append("# Agent Joules: Devpost submission checklist\n\nDeadline: 2026-10-30 10:00 PDT (17:00 UTC)\n\n## Submission order\n\n");
        string[] steps =
        [
            "Project name: Agent Joules (user)",
            "Elevator pitch, 200 characters, text from docs/devpost-pitch.md (user)",
            "About the project, docs/devpost-story.md (user)",
            "Built-with tags, 25 at most (user)",
            "Try it out links, the Demo URL line and the repository (user)",
            "YouTube video link, <user fills> (user)",
            "Track, the user's pick (user)",
            "Team members (user)",
            "Repository URL with the license and the README highlights (user)",
            "Nemotron and Nebius feedback questionnaire, docs/devpost-feedback.md (user)",
            "Submit (user)",
            "After submission keep the repository on the submitted commit and confirm the v0.1.0 tag (user)",
        ];
        for (int index = 0; index < steps.Length; index++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{index + 1}. {steps[index]}\n");
        }

        text.Append("\n## Cut-off plan\n\n| Missing item | What is submitted | Files that change | Regeneration commands | Fallback story | Boxes unchecked |\n|---|---|---|---|---|---|\n");
        text.Append("| cloudgpu tier | row `cloudgpu \\| not run`, pitch A-local, README sentence `None besides Token Factory.` | README.md, docs/report/, content/ | " + Regenerate + " | AJ-129 | 21, 22, 23 |\n");
        text.Append("| local tier | the other tiers | README.md, docs/report/ | " + Regenerate + " | AJ-150 | 1 |\n");
        text.Append("| nvidia tier | the other tiers | README.md, docs/report/ | " + Regenerate + " | AJ-150 | 2 |\n");
        text.Append("| nebius tier | BLOCKED, no claim the files do not support | README.md | " + Regenerate + " | AJ-129 | 3 |\n");
        text.Append("| judge | quality shown as N/A | README.md | " + Regenerate + " | AJ-150 | none |\n");
        text.Append("| Pages not live | the release URL | docs/devpost-checklist.md | none | AJ-157 | none |\n");
        text.Append("| video not ready | the minimal cut of 65 s, shots 1, 3, 6 and 9 | docs/devpost-checklist.md | none | AJ-135 | none |\n");
        text.Append("| feedback placeholders unfilled | the draft | docs/devpost-feedback.md | none | AJ-142 | none |\n");
        text.Append("\n## Timeline\n\n");
        text.Append("- content freeze 2026-10-29 17:00 UTC (10:00 PDT)\n- push 2026-10-29 20:00 UTC (13:00 PDT)\n- Pages and release live 2026-10-29 21:00 UTC (14:00 PDT)\n");
        text.Append("- video public by 2026-10-30 07:00 UTC (00:00 PDT)\n- submission complete by 2026-10-30 14:00 UTC (07:00 PDT)\n- deadline 2026-10-30 17:00 UTC (10:00 PDT)\n\n");
        text.Append("3 hours of buffer remain between the last target and the deadline; in central Europe the local time is UTC+1 (CET) on that date.\n\n");
        text.Append("## User actions\n\nUser action (the builder never runs this):\n\n```\n" + ReleaseCommand + "\n```\n\nVerify:\n\n```\ngh release view v0.1.0 --repo tomas-rampas/agent-joules --json assets --jq '.assets[].name'\n```\n");
        return text.ToString();
    }

    /// <summary>A build log that satisfies every AJ-159 rule.</summary>
    /// <returns>The text.</returns>
    public static string BuildLog()
    {
        var text = new StringBuilder();
        text.Append("# Build log\n\n## Drop order\n\nLevel 1: stories.\n\n| Step | Stories | h | Cum h | Trigger | Consequence | Boxes unticked | Regeneration | Risk |\n|---|---|---|---|---|---|---|---|---|\n");
        string[] steps = ["C1", "C2", "C3", "C4", "C5", "C6", "C7", "C8", "C9", "D1", "D2", "D3"];
        for (int index = 0; index < steps.Length; index++)
        {
            text.Append(CultureInfo.InvariantCulture, $"| {steps[index]} | AJ-{100 + index} | 1.00 | {index + 1}.00 | 2026-10-26 17:00 UTC and S > {index} h | a requirement is reported UNMET | none | none | RK-1 |\n");
        }

        text.Append("\nLevel 3: the cloudgpu tier, decided at 2026-10-26 17:00 UTC. It unticks boxes 21, 22 and 23 (substance). Risk RK-5.\n\n");
        text.Append("## Never dropped\n\nThe minimum submission. None of these items is a step of the drop order:\n\n- the nebius run file and the local run file\n- schema validation, the build, format and test commands\n- the secret scan\n- the README results block\n- the checklist with the push command\n");
        text.Append("- the story (docs/devpost-story.md)\n- the feedback draft and the demo script\n- the peer-review-critic approval\n- the user's video and the user's submission on Devpost\n- a second cloudgpu repeat set is never a drop step; the three LinkedIn variants are never dropped\n\n");
        text.Append("## Buffer\n\n| Target | UTC time | Unallocated hours |\n|---|---|---|\n| content freeze | 2026-10-29 17:00 UTC | 3 |\n| submission complete | 2026-10-30 14:00 UTC | 3 |\n| deadline | 2026-10-30 17:00 UTC | 0 |\n\n");
        text.Append("M6: 55.25 h of estimates against about 18 h of capacity; M7: 38.0 h, see the Estimate section; the gap is closed by the drop order; these are planning figures.\n");
        return text.ToString();
    }

    /// <summary>Release notes that satisfy every notes rule.</summary>
    /// <returns>The text.</returns>
    public static string ReleaseNotes() =>
        "# Agent Joules v0.1.0\n\n## Features\n\nThe local run measured 12.5 J per step, see results/20261025T131930Z-local.json.\n\n## Fixes\n\nNone.\n\n## Breaking changes\n\nNone (first release)\n\n## Migration\n\nNone (first release)\n\n"
        + "## Known limitations\n\nEnergy is GPU-board energy only.\n\n## Honesty rules\n\nThe tiers that ran are local, nvidia and nebius; cloudgpu | not run.\n\n"
        + "## Assets\n\n- agent-joules-v0.1.0-fdd.zip\n- agent-joules-v0.1.0-results.zip\n- agent-joules-v0.1.0-report.zip\n\n## Verification\n\n`gh release view v0.1.0 --repo tomas-rampas/agent-joules --json assets --jq '.assets[].name'`\n";

    /// <summary>
    /// Lays out a folder that satisfies every AJ-157 rule: the notes, one results json, the checklist with the release command, the demo script-free report and the three zips with their expected entries.
    /// </summary>
    /// <param name="workspace">The empty fixture folder.</param>
    public static void WritePackage(TempWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _ = workspace.WriteFile("docs/release-notes-v0.1.0.md", ReleaseNotes());
        _ = workspace.WriteFile("docs/devpost-checklist.md", Checklist());
        _ = workspace.WriteFile("results/20261025T131930Z-local.json", "{}");
        WriteZip(workspace.Root, "fdd", ("AgentJoules.Cli.dll", "binary"));
        WriteZip(workspace.Root, "results", ("results/20261025T131930Z-local.json", "{}"));
        WriteZip(workspace.Root, "report", ("index.html", "<html></html>"), ("summary.md", "# Summary"), ("joules-per-step.svg", "<svg/>"));
    }

    /// <summary>Writes (or replaces) one asset zip of the folder.</summary>
    /// <param name="root">The fixture folder.</param>
    /// <param name="kind">The asset kind: <c>fdd</c>, <c>results</c> or <c>report</c>.</param>
    /// <param name="entries">The entry names with their text.</param>
    public static void WriteZip(string root, string kind, params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(root, "artifacts", "agent-joules-v0.1.0-" + kind + ".zip");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }
    }

    /// <summary>A value shaped like a Nebius or NVIDIA key, assembled at run time so that no literal of the source is key-shaped.</summary>
    /// <returns>The prefix, a hyphen and 16 characters.</returns>
    public static string KeyShapedValue() => string.Concat("nvapi", "-", new string('k', 16));

    /// <summary>Changes one cell text inside the first table row whose first cell is the step id.</summary>
    /// <param name="text">The document.</param>
    /// <param name="id">The step id, for example <c>C4</c>.</param>
    /// <param name="from">The text to replace inside that row.</param>
    /// <param name="to">The replacement.</param>
    /// <returns>The changed document.</returns>
    /// <exception cref="InvalidOperationException">The row or the text does not exist.</exception>
    public static string ChangeRow(string text, string id, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Split('\n');
        int index = Array.FindIndex(lines, line => line.StartsWith("| " + id + " |", StringComparison.Ordinal));
        if (index < 0 || !lines[index].Contains(from, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixture row " + id + " does not contain: " + from);
        }

        lines[index] = lines[index].Replace(from, to, StringComparison.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>Runs git in a folder and fails when it exits non-zero.</summary>
    /// <param name="root">The folder.</param>
    /// <param name="arguments">The arguments.</param>
    /// <exception cref="InvalidOperationException">git could not start or exited non-zero.</exception>
    public static void RunGit(string root, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string variable in new[] { "GIT_DIR", "GIT_INDEX_FILE", "GIT_WORK_TREE" })
        {
            _ = start.Environment.Remove(variable);
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        // The pipes are drained asynchronously so that the timed wait below is reachable when git hangs.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(GitTimeoutSeconds)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("git " + arguments[0] + " timed out");
        }

        _ = outputTask.GetAwaiter().GetResult();
        _ = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("git " + arguments[0] + " exited " + process.ExitCode.ToString(CultureInfo.InvariantCulture));
        }
    }
}