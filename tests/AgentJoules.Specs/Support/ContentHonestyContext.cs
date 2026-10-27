using AgentJoules.TestSupport.Docs;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-143 honesty scenario, handed to the step class by Reqnroll context injection: the texts under audit (the committed ones or in-memory copies), the file existence function the source rule asks, the
/// state of the cloudgpu tier and the findings of the last run. The committed files are only read; nothing is written.
/// </summary>
public sealed class ContentHonestyContext
{
    /// <summary>The texts under audit by repository-relative path.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether a repository-relative file exists.</summary>
    public Func<string, bool> Exists { get; set; } = static _ => false;

    /// <summary>The state of the cloudgpu tier the scenario models.</summary>
    public CloudGpuTierState Cloud { get; set; } = new(CloudGpuTaglineRule.Absent, null, null, null);

    /// <summary>The findings of the last audit.</summary>
    public IReadOnlyList<string> Findings { get; set; } = [];
}