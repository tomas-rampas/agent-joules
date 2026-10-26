namespace AgentJoules.Specs.Support;

/// <summary>The state of one AJ-130 scenario (the README structure), handed to the step class by Reqnroll context injection. All texts are in memory; the committed files are only read, never changed.</summary>
public sealed class ReadmeStructureContext
{
    /// <summary>The README text under test, possibly a mutated copy of the committed one.</summary>
    public string Readme { get; set; } = string.Empty;

    /// <summary>The text of <c>docs/devpost-pitch.md</c>.</summary>
    public string Pitch { get; set; } = string.Empty;

    /// <summary>The build log text a scenario supplies.</summary>
    public string BuildLog { get; set; } = string.Empty;

    /// <summary>Whether a completed or partial cloudgpu run file exists.</summary>
    public bool CloudGpuRan { get; set; } = true;

    /// <summary>The block found by the When step, or null before it.</summary>
    public MermaidBlockResult? Block { get; set; }

    /// <summary>The failures of the last check.</summary>
    public IReadOnlyList<string> Failures { get; set; } = [];
}