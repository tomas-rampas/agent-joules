using AgentJoules.Cli;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentJoules.TestSupport;

/// <summary>
/// What the <c>probe --save</c> tests (AJ-037) share: the fixed fake instant and the file names it produces, the overrides that point the content
/// root at a <see cref="TempWorkspace"/> and replace the host <see cref="TimeProvider"/>, and the readers of the workspace <c>results</c> folder.
/// </summary>
public static class ProbeSaveFixtures
{
    /// <summary>The name of the results folder under the content root.</summary>
    public const string ResultsFolder = "results";

    /// <summary>The file name <c>--save</c> produces at <see cref="FixedInstant"/> without a suffix.</summary>
    public const string FixedName = "probe-20261019T093015Z.json";

    /// <summary>The file name <c>--save --suffix cloudgpu</c> produces at <see cref="FixedInstant"/>.</summary>
    public const string FixedSuffixedName = "probe-20261019T093015Z-cloudgpu.json";

    /// <summary>The instant of the fake clock of every save test: 2026-10-19T09:30:15Z.</summary>
    public static DateTimeOffset FixedInstant { get; } = new(2026, 10, 19, 9, 30, 15, TimeSpan.Zero);

    /// <summary>
    /// The overrides of one save run: <paramref name="api"/> behind the real GPU factory, <paramref name="clock"/> as the host
    /// <see cref="TimeProvider"/> (it replaces the default registration, since the overrides run last), and <paramref name="workspace"/> as the
    /// working directory, which is the content root, so <c>results/</c> resolves under it.
    /// </summary>
    public static CliHostOverrides Overrides(
        ScriptedNvmlApi api,
        TempWorkspace workspace,
        TimeProvider clock,
        IEnumerable<(string Key, string? Value)>? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(clock);

        CliHostOverrides scripted = ScriptedGpuOverrides.Create(api, configuration);
        return scripted with
        {
            WorkingDirectory = workspace.Root,
            Services = services =>
            {
                scripted.Services?.Invoke(services);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            },
        };
    }

    /// <summary>The absolute path of the results folder of <paramref name="workspace"/>; the folder may not exist.</summary>
    public static string ResultsPath(TempWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Path.Combine(workspace.Root, ResultsFolder);
    }

    /// <summary>The absolute path of the file <paramref name="name"/> in the results folder of <paramref name="workspace"/>.</summary>
    public static string FilePath(TempWorkspace workspace, string name) => Path.Combine(ResultsPath(workspace), name);

    /// <summary>The names of the files in the results folder in ordinal order; empty when the folder does not exist.</summary>
    public static string[] SavedNames(TempWorkspace workspace)
    {
        string folder = ResultsPath(workspace);
        return Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The names of the entries (files and folders) directly under the workspace root, in ordinal order.</summary>
    public static string[] RootEntries(TempWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return [.. Directory.GetFileSystemEntries(workspace.Root).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
    }
}