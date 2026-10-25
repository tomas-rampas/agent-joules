using Xunit;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// A fact that is skipped, with its reason, when git is not on PATH or the pre-drafted story cannot be read at <see cref="CloudGpuTaglineRule.PreDraftedStoryCommit"/> (a shallow clone may lack the commit).
/// The skip names the commit by its short form and no path of the machine.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PreDraftedStoryFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute; <see cref="FactAttribute.Skip"/> is set when the pre-drafted story cannot be read.</summary>
    public PreDraftedStoryFactAttribute()
    {
        if (!GitRunner.Available())
        {
            Skip = "git is not on PATH, so the pre-drafted story cannot be read";
        }
        else if (CloudGpuTaglineRule.PreDraftedStory(ConfigurationFixtures.RepositoryRoot()) is null)
        {
            Skip = "the pre-drafted docs/devpost-story.md cannot be read at commit " + CloudGpuTaglineRule.PreDraftedStoryCommit[..12] + " (a shallow clone may lack it)";
        }
    }
}