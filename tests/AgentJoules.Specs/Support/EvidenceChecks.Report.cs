using System.Globalization;

namespace AgentJoules.Specs.Support;

/// <summary>The rules of AJ-152 over the generated outputs: the report page, the rendered posts and the shot list of the demo script.</summary>
public static partial class EvidenceChecks
{
    private const string VariantPrefix = "## Variant: ";

    /// <summary>The page rules: <c>docs/report/index.html</c> exists, holds no <c>&lt;script</c>, links <c>joules-per-step.svg</c> and states <c>GPU-board energy only</c>; <c>docs/.nojekyll</c> exists.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> PageViolations(string root)
    {
        var violations = new List<string>();
        string? page = ReadText(root, "docs/report/index.html");
        if (page is null)
        {
            violations.Add("missing docs/report/index.html");
        }
        else
        {
            if (page.Contains("<script", StringComparison.Ordinal))
            {
                violations.Add("script element in docs/report/index.html");
            }

            if (!page.Contains("joules-per-step.svg", StringComparison.Ordinal))
            {
                violations.Add("missing chart link: joules-per-step.svg");
            }

            if (!page.Contains("GPU-board energy only", StringComparison.Ordinal))
            {
                violations.Add("missing statement: GPU-board energy only");
            }
        }

        if (!File.Exists(Path.Combine(root, "docs", ".nojekyll")))
        {
            violations.Add("missing docs/.nojekyll");
        }

        return violations;
    }

    /// <summary>
    /// The post rules: three <c>## Variant: </c> headings, no <c>{{results.</c> text, a <c>Sources:</c> line whose named files exist in <c>results/</c>, and no variant above 1 300 characters
    /// (the text under its heading up to the next <c>## Variant: </c> heading or the end of the file, trimmed).
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> PostsViolations(string root)
    {
        string? text = ReadText(root, "content/linkedin-posts.md");
        if (text is null)
        {
            return ["missing content/linkedin-posts.md"];
        }

        var violations = new List<string>();
        if (text.Contains("{{results.", StringComparison.Ordinal))
        {
            violations.Add("unresolved placeholder in content/linkedin-posts.md");
        }

        string[] lines = DocumentChecks.Lines(text);
        int[] variants = [.. lines.Select(static (line, index) => (line, index)).Where(static pair => pair.line.StartsWith(VariantPrefix, StringComparison.Ordinal)).Select(static pair => pair.index)];
        if (variants.Length != 3)
        {
            violations.Add($"content/linkedin-posts.md has {variants.Length.ToString(CultureInfo.InvariantCulture)} variants, expected 3");
        }

        for (int i = 0; i < variants.Length; i++)
        {
            int end = i + 1 < variants.Length ? variants[i + 1] : lines.Length;
            string body = string.Join('\n', lines[(variants[i] + 1)..end]).Trim();
            if (body.Length > VariantCharacterLimit)
            {
                violations.Add($"variant {lines[variants[i]][VariantPrefix.Length..].Trim()} has {body.Length.ToString(CultureInfo.InvariantCulture)} characters, limit {VariantCharacterLimit.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        string[] sourceLines = [.. lines.Where(static line => line.StartsWith("Sources:", StringComparison.Ordinal))];
        if (sourceLines.Length == 0)
        {
            violations.Add("no Sources line in content/linkedin-posts.md");
        }

        string results = Path.GetFullPath(ResultsPath(root));
        foreach (string sources in sourceLines)
        {
            string[] names = sources["Sources:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0)
            {
                violations.Add("empty Sources line in content/linkedin-posts.md");
            }

            foreach (string name in names)
            {
                // A name that resolves outside results/ (absolute, or with ..) is not probed.
                string full = Path.GetFullPath(Path.Combine(results, name));
                if (!full.StartsWith(results + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    violations.Add("unresolved source outside results/: " + name);
                }
                else if (!File.Exists(full))
                {
                    violations.Add("missing source file: results/" + name);
                }
            }
        }

        return violations;
    }

    /// <summary>The sum of the duration column of the shot list: the fourth <c>|</c> field of the rows that start <c>| </c> and a digit, as <c>awk -F'|'</c> reads it.</summary>
    /// <param name="script">The text of the demo script.</param>
    /// <returns>The sum in seconds.</returns>
    public static int ShotListSeconds(string script) =>
        DocumentChecks.Lines(script)
            .Where(static line => line.Length > 2 && line.StartsWith("| ", StringComparison.Ordinal) && char.IsAsciiDigit(line[2]))
            .Sum(static line => int.TryParse(line.Split('|')[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ? seconds : 0);

    /// <summary>The shot-list rule: the durations sum to 170 seconds or less.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ShotListViolations(string root)
    {
        string? script = ReadText(root, "content/demo-script.md");
        if (script is null)
        {
            return ["missing content/demo-script.md"];
        }

        int total = ShotListSeconds(script);
        return total > ShotListLimitSeconds
            ? [string.Create(CultureInfo.InvariantCulture, $"shot list totals {total} s, limit {ShotListLimitSeconds} s")]
            : [];
    }

    /// <summary>Every AJ-152 rule over a folder: the page, the posts and the shot list.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ReportEvidence(string root) =>
        [.. PageViolations(root), .. PostsViolations(root), .. ShotListViolations(root)];
}