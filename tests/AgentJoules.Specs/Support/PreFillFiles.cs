using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The pre-fill versions of the Devpost texts (AJ-140, AJ-142). They are committed as fixtures under Fixtures/Devpost (embedded resources, so a shallow CI checkout needs no git object) and, when the git object is
/// available, compared with a read-only <c>git show</c> started from the repository root through <see cref="GitRunner"/>, so a fixture cannot drift. The commits are constants, not "the last commit that touched the
/// file", because that moves when the fill itself is committed: each is the last commit that touched the file before the M6 fill, found at f65f308 with <c>git log -1 --format=%H f65f308 -- &lt;file&gt;</c>.
/// Nothing is written.
/// </summary>
public static class PreFillFiles
{
    /// <summary>The commit that holds the story with its nine placeholders (AJ-129).</summary>
    public const string StoryCommit = "bc11dd2671fa1a5aea9a1808693572f6c1fe5d0a";

    /// <summary>The commit that holds the feedback file with its fifteen placeholders.</summary>
    public const string FeedbackCommit = "420adc8410d2f7761ff64380646dff13bed4b204";

    private const string StoryResource = "AgentJoules.Specs.Fixtures.devpost-story.prefill.md";
    private const string FeedbackResource = "AgentJoules.Specs.Fixtures.devpost-feedback.prefill.md";

    /// <summary>The pre-fill story: the committed fixture, equal to the git object when git can read it.</summary>
    /// <returns>The text.</returns>
    public static string Story() => Read(StoryResource, StoryCommit, "docs/devpost-story.md");

    /// <summary>The pre-fill feedback file: the committed fixture, equal to the git object when git can read it.</summary>
    /// <returns>The text.</returns>
    public static string Feedback() => Read(FeedbackResource, FeedbackCommit, "docs/devpost-feedback.md");

    private static string Read(string resource, string commit, string path)
    {
        using Stream stream = typeof(PreFillFiles).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("embedded fixture missing: " + resource);
        using var reader = new StreamReader(stream);
        string fixture = Normalise(reader.ReadToEnd());
        string? fromGit = Show(commit, path);
        if (fromGit is not null)
        {
            Assert.True(string.Equals(fixture, Normalise(fromGit), StringComparison.Ordinal), "the fixture " + resource + " differs from git show " + commit[..12] + ":" + path);
        }

        return fixture;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string? Show(string commit, string path)
    {
        try
        {
            GitResult result = GitRunner.Run(RepositoryRoot.Path, "show", commit + ":" + path);

            return result.ExitCode == 0 ? result.Stdout : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}