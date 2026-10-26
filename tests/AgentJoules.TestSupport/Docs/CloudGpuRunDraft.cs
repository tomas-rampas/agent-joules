using System.Globalization;
using System.Text;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// A scratch <c>docs/cloudgpu-run.md</c> for the contract tests (AJ-128, REQ-031, EDGE-016, EDGE-017): the six sections with a complete default content that passes <see cref="CloudGpuRunDocumentParser"/>. A test changes
/// the section it exercises and renders the text. Nothing here is a real host, address or key.
/// </summary>
public sealed class CloudGpuRunDraft
{
    private readonly Dictionary<string, List<string>> _sections = [];

    /// <summary>Creates a draft with the six sections in the order of the contract.</summary>
    public CloudGpuRunDraft()
    {
        Headings = [.. CloudGpuRunDocumentParser.RequiredHeadings];
        _sections["Instance"] =
        [
            "- Platform: gpu-l40s-a",
            "- Preset: 1gpu-8vcpu-32gb",
            "- Region: eu-north1",
            "- Image: ubuntu24.04-cuda12.0",
            "- Boot disk: 200 GiB",
            "- preemptible: off (user-confirmed)",
            "- Hourly price (console): USD 1.82 per hour",
            "- GPU: NVIDIA L40S, UUID GPU-" + Guid.NewGuid().ToString("D"),
            "- Driver: 570.172.08",
            "- Delivery: self-contained linux-x64 publish copied over scp",
        ];
        _sections["Commands"] =
        [
            "```bash",
            "ssh $CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST",
            "printf '%s' \"$NGC_API_KEY\" | docker login nvcr.io --username '$oauthtoken' --password-stdin",
            "docker logout nvcr.io",
            "```",
        ];
        _sections["Probe"] = ["results/probe-20261101T090000Z-cloudgpu.json: MIG mode disabled, energy counter supported."];
        _sections["Runs"] =
        [
            "- Started: 2026-11-01T09:10:00Z, ended: 2026-11-01T10:40:00Z",
            "- results/20261101T091000Z-cloudgpu.json, exit code: 0",
        ];
        _sections["Cost"] = [.. CostLines("6.12")];
        _sections["Teardown"] = [.. TeardownLines];
    }

    /// <summary>The headings in order; a removed heading is gone from the text.</summary>
    public List<string> Headings { get; }

    /// <summary>The Teardown lines of a confirmed deletion: the word <c>deleted</c>, the deletion time and the final cost.</summary>
    public static IReadOnlyList<string> TeardownLines { get; } =
    [
        "The user deleted the VM and its boot disk at 2026-11-01T11:05:00Z.",
        "Final cost on the billing page: USD 6.40.",
    ];

    /// <summary>The Cost lines of a run that was not stopped by the guard.</summary>
    /// <param name="amount">The observed cost in USD, for example <c>6.12</c>.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> CostLines(string amount) =>
    [
        "Observed cost: USD " + amount + " (billing page, 2026-11-01T10:45:00Z).",
        "Stop reason: no stop triggered.",
    ];

    /// <summary>The lines of a section.</summary>
    /// <param name="heading">The heading.</param>
    /// <returns>The live list of lines of the section.</returns>
    public List<string> Lines(string heading) => _sections[heading];

    /// <summary>Replaces the lines of a section.</summary>
    /// <param name="heading">The heading.</param>
    /// <param name="lines">The new lines.</param>
    /// <returns>This draft.</returns>
    public CloudGpuRunDraft Set(string heading, params string[] lines)
    {
        _sections[heading] = [.. lines];
        return this;
    }

    /// <summary>Appends a line to a section.</summary>
    /// <param name="heading">The heading.</param>
    /// <param name="line">The line.</param>
    /// <returns>This draft.</returns>
    public CloudGpuRunDraft Add(string heading, string line)
    {
        _sections[heading].Add(line);
        return this;
    }

    /// <summary>Removes a heading and its lines.</summary>
    /// <param name="heading">The heading.</param>
    /// <returns>This draft.</returns>
    public CloudGpuRunDraft Remove(string heading)
    {
        _ = Headings.Remove(heading);
        return this;
    }

    /// <summary>Renders the document with LF line ends.</summary>
    /// <returns>The text.</returns>
    public string Render()
    {
        StringBuilder text = new();
        _ = text.Append("# cloudgpu run\n\n");
        foreach (string heading in Headings)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"## {heading}\n\n");
            foreach (string line in _sections.GetValueOrDefault(heading) ?? [])
            {
                _ = text.Append(line).Append('\n');
            }

            _ = text.Append('\n');
        }

        return text.ToString();
    }
}