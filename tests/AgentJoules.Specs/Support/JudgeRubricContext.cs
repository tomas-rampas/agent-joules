using AgentJoules.Core.Cli;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-075 scenario (load the judge rubric), handed to the step class by Reqnroll context injection. The Given steps choose the file: the committed
/// <c>config/judge-rubric.md</c> (read, never written) or a file written into a <see cref="TempWorkspace"/> (the repository is never written). The When step runs the real
/// <see cref="RubricLoader"/> on it and keeps the text it returned or the <see cref="ConfigurationException"/> it threw. There is no HTTP, no judge model and no configuration binding.
/// </summary>
public sealed class JudgeRubricContext : IDisposable
{
    /// <summary>The file name of the rubric, in the repository and in the temporary folder.</summary>
    public const string RubricFileName = "judge-rubric.md";

    // A known text with trailing white space and a blank last line: a loader that trims or normalises would change it.
    private const string KnownText = "Score one step.  \n\n1. one\n2. two  \n3. three\n4. four\n5. five\n\n";

    private TempWorkspace? _workspace;

    /// <summary>The path handed to the loader, or null before a Given step chose it.</summary>
    public string? RubricPath { get; private set; }

    /// <summary>The text a Given step wrote, or null when the file is the committed rubric or a missing one.</summary>
    public string? WrittenText { get; private set; }

    /// <summary>The folder that holds a temporary file, or null; a message must not contain it.</summary>
    public string? Folder => _workspace?.Root;

    /// <summary>The text the loader returned, or null before it ran or when it failed.</summary>
    public string? Loaded { get; private set; }

    /// <summary>The failure of the load of a scenario that expects one, or null.</summary>
    public ConfigurationException? Failure { get; private set; }

    /// <summary>Points the loader at the committed rubric of the repository.</summary>
    public void UseCommittedRubric() => RubricPath = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "config", RubricFileName);

    /// <summary>Writes a rubric with a known text, with white space that a trimming loader would change.</summary>
    public void WriteKnownRubric() => Write(KnownText);

    /// <summary>Writes a rubric whose level lines are the given levels, in order.</summary>
    /// <param name="levels">The levels to define, each from 1 to 5.</param>
    public void WriteRubricWithLevels(params int[] levels) =>
        Write("Score one step.\n\n" + string.Concat(levels.Select(level => $"{level}. level {level}\n")));

    /// <summary>Points the loader at a file that is not there.</summary>
    public void UseMissingFile()
    {
        _workspace ??= TempWorkspace.CreateEmpty();
        RubricPath = Path.Combine(_workspace.Root, RubricFileName);
    }

    /// <summary>Loads the rubric, keeping the text or the failure.</summary>
    public void Load()
    {
        try
        {
            Loaded = RubricLoader.Load(RubricPath ?? throw new InvalidOperationException("No rubric file was chosen."));
        }
        catch (ConfigurationException failure)
        {
            Failure = failure;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();

    private void Write(string text)
    {
        _workspace ??= TempWorkspace.CreateEmpty();
        WrittenText = text;
        RubricPath = _workspace.WriteFile(RubricFileName, text);
    }
}