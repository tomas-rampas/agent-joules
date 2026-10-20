using System.Text.Json.Nodes;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Tasks;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-048 scenario (load the fixed task set), handed to the step class by Reqnroll context injection. It holds the folder the
/// loader reads (the committed <c>tasks/</c> folder, or an edited copy of it inside a <see cref="TempWorkspace"/>), what the loader returned, and the
/// failure of a scenario that expects one. The scenarios assert on the <see cref="ConfigurationException"/> the loader throws: its exit code is the
/// process exit code and its message is the output; the <c>run --task</c> wiring is proved end to end by AJ-054 and AJ-056.
/// </summary>
public sealed class TaskSetContext : IDisposable
{
    private const string TasksFolder = "tasks";

    /// <summary>The folder the loader reads, or null before a step chose it.</summary>
    public string? Directory { get; private set; }

    /// <summary>The workspace that holds the edited copy of the committed folder, or null when the scenario reads the committed folder.</summary>
    public TempWorkspace? Workspace { get; private set; }

    /// <summary>The tasks the loader returned, or null before it ran or when it failed.</summary>
    public IReadOnlyList<TaskDefinition>? Tasks { get; set; }

    /// <summary>The failure of the load of a scenario that expects one, or null.</summary>
    public ConfigurationException? Failure { get; set; }

    /// <summary>The folder the loader reads.</summary>
    /// <exception cref="InvalidOperationException">No step chose a folder.</exception>
    public string RequiredDirectory => Directory ?? throw new InvalidOperationException("No tasks folder was chosen.");

    /// <summary>Points the scenario at the committed <c>tasks/</c> folder of the repository.</summary>
    public void UseCommittedFolder() => Directory = Path.Combine(ConfigurationFixtures.RepositoryRoot(), TasksFolder);

    /// <summary>Copies the committed task files into a new workspace and points the scenario at that copy; the original folder is never written.</summary>
    /// <exception cref="InvalidOperationException">A workspace exists already.</exception>
    public void UseEditedCopy()
    {
        if (Workspace is not null)
        {
            throw new InvalidOperationException("The scenario already holds a workspace.");
        }

        Workspace = TempWorkspace.CreateEmpty();
        foreach (string file in CommittedFiles())
        {
            Workspace.WriteFile(Path.Combine(TasksFolder, Path.GetFileName(file)), File.ReadAllText(file));
        }

        Directory = Path.Combine(Workspace.Root, TasksFolder);
    }

    /// <summary>Reads the copied file <paramref name="fileName"/> as a JSON object, applies <paramref name="edit"/> and writes it back.</summary>
    /// <param name="fileName">The file name inside the copied folder.</param>
    /// <param name="edit">The change to the object.</param>
    /// <exception cref="InvalidOperationException">The file is not a JSON object.</exception>
    public void EditFile(string fileName, Action<JsonObject> edit)
    {
        string path = Path.Combine(RequiredDirectory, fileName);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException(fileName + " is not a JSON object.");
        edit(root);

        File.WriteAllText(path, root.ToJsonString());
    }

    /// <summary>The names of the files in the copied folder, in ordinal order.</summary>
    public IReadOnlyList<string> CopiedFileNames() =>
        [.. System.IO.Directory.GetFiles(RequiredDirectory, "*.json").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    private static string[] CommittedFiles() =>
        System.IO.Directory.GetFiles(Path.Combine(ConfigurationFixtures.RepositoryRoot(), TasksFolder), "*.json");

    /// <inheritdoc />
    public void Dispose() => Workspace?.Dispose();
}