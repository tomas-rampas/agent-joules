using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-054 (REQ-017, security review LOW-3 and LOW-4): <see cref="ContentPaths.Resolve"/> keeps a configured path such as <c>Paths:Tasks</c> strictly inside the content root. The table holds the
/// probe cases of the review; every row behaves the same on Windows and on Linux, because a backslash is a separator, a colon is refused and a segment made only of dots (other than
/// <c>.</c>) or a reserved device name is refused before the file system sees the path. A folder on the way that is a link (a junction or a symbolic link) is refused too.
/// </summary>
[Trait("Spec", "REQ-017")]
public sealed class ContentPathsTests
{
    private const string Key = "Paths:Tasks";

    [Theory]
    [InlineData("C:tasks")]
    [InlineData(@"\\server\share\tasks")]
    [InlineData("//server/share/tasks")]
    [InlineData("/tasks")]
    [InlineData(@"\tasks")]
    [InlineData("...")]
    [InlineData(".")]
    [InlineData(". ")]
    [InlineData(".. ")]
    [InlineData(" ..")]
    [InlineData("..")]
    [InlineData(@"tasks\..\..")]
    [InlineData("tasks/../..")]
    [InlineData("tasks/..")]
    [InlineData("NUL")]
    [InlineData("tasks/con")]
    [InlineData("com1.json")]
    [InlineData("COM0")]
    [InlineData("tasks/CONIN$")]
    [InlineData("tasks:stream")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_value_that_is_rooted_climbs_names_a_device_or_resolves_to_the_root_is_refused_naming_only_the_key(string value)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => ContentPaths.Resolve(workspace.Root, value, Key));

        Assert.StartsWith(Key + " ", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.OrdinalIgnoreCase);
        if (value.Trim().Length > 3)
        {
            Assert.DoesNotContain(value.Trim(), failure.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("tasks", "tasks")]
    [InlineData("tasks/", "tasks")]
    [InlineData(@"tasks\", "tasks")]
    [InlineData("./tasks", "tasks")]
    [InlineData(@".\tasks", "tasks")]
    [InlineData("custom/set", "custom/set")]
    [InlineData(@"custom\set", "custom/set")]
    [InlineData("tasks.d", "tasks.d")]
    [InlineData(".tasks", ".tasks")]
    public void A_relative_folder_inside_the_root_is_resolved_under_it(string value, string expectedRelative)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string expected = Path.Combine(workspace.Root, expectedRelative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, ContentPaths.Resolve(workspace.Root, value, Key));
    }

    [Fact]
    public void A_trailing_space_is_accepted_and_stays_under_the_root()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        string resolved = ContentPaths.Resolve(workspace.Root, "tasks ", Key);

        Assert.StartsWith(workspace.Root + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);
        Assert.Equal("tasks", Path.GetFileName(resolved).TrimEnd());
    }

    [WindowsFact]
    public void A_root_given_in_another_letter_case_is_the_same_root_on_Windows()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string otherCase = string.Equals(workspace.Root.ToUpperInvariant(), workspace.Root, StringComparison.Ordinal) ? workspace.Root.ToLowerInvariant() : workspace.Root.ToUpperInvariant();

        string resolved = ContentPaths.Resolve(otherCase, "tasks", Key);

        Assert.Equal(Path.Combine(otherCase, "tasks"), resolved, ignoreCase: true);
    }

    [Fact]
    public void A_sibling_folder_that_shares_the_root_as_a_prefix_is_never_inside_it()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string root = workspace.Root;

        Assert.False(ContentPaths.IsStrictlyInside(root, root + "x"));
        Assert.False(ContentPaths.IsStrictlyInside(root, root + "x" + Path.DirectorySeparatorChar + "tasks"));
        Assert.False(ContentPaths.IsStrictlyInside(root, root));
        Assert.False(ContentPaths.IsStrictlyInside(root, Path.GetDirectoryName(root)!));
        Assert.True(ContentPaths.IsStrictlyInside(root, Path.Combine(root, "x")));
        Assert.True(ContentPaths.IsStrictlyInside(root + Path.DirectorySeparatorChar, Path.Combine(root, "x")));
    }

    [Fact]
    public void A_junction_or_directory_link_in_a_middle_folder_is_refused()
    {
        using TempWorkspace outside = TempWorkspace.CreateEmpty();
        _ = Directory.CreateDirectory(Path.Combine(outside.Root, "tasks"));
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        using IDisposable link = FolderAccess.CreateDirectoryLink(Path.Combine(workspace.Root, "linked"), outside.Root);

        ConfigurationException middle = Assert.Throws<ConfigurationException>(() => ContentPaths.Resolve(workspace.Root, "linked/tasks", Key));
        ConfigurationException last = Assert.Throws<ConfigurationException>(() => ContentPaths.Resolve(workspace.Root, "linked", Key));

        Assert.StartsWith(Key + " ", middle.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(outside.Root, middle.Message + last.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SymbolicLinkFact]
    public void A_symbolic_link_in_a_middle_folder_is_refused()
    {
        using TempWorkspace outside = TempWorkspace.CreateEmpty();
        _ = Directory.CreateDirectory(Path.Combine(outside.Root, "tasks"));
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string linkPath = Path.Combine(workspace.Root, "symlinked");
        _ = Directory.CreateSymbolicLink(linkPath, outside.Root);
        try
        {
            _ = Assert.Throws<ConfigurationException>(() => ContentPaths.Resolve(workspace.Root, "symlinked/tasks", Key));
        }
        finally
        {
            Directory.Delete(linkPath);
        }
    }

    [Fact]
    public void A_folder_that_does_not_exist_yet_is_resolved_and_left_to_its_reader()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        Assert.Equal(Path.Combine(workspace.Root, "missing", "tasks"), ContentPaths.Resolve(workspace.Root, "missing/tasks", Key));
    }
}