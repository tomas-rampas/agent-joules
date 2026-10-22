using System.Text;

namespace AgentJoules.TestSupport;

/// <summary>
/// A temporary directory that stands in for the working directory of a CLI run: relative paths (the default <c>config/appsettings.json</c>,
/// <c>--config</c>, <c>--out</c>) resolve against it, so no test reads or writes the repository. Deleted on dispose.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private TempWorkspace(string root)
    {
        Root = root;
    }

    /// <summary>The absolute path of the directory.</summary>
    public string Root { get; }

    /// <summary>The path of <c>config/appsettings.json</c> inside the workspace; the file exists after <see cref="Create"/>.</summary>
    public string ConfigPath => Path.Combine(Root, "config", "appsettings.json");

    /// <summary>
    /// Creates a workspace that holds a copy of the shipped <c>config/appsettings.json</c>, the file the CLI reads by default, and of the committed <c>config/prices.json</c>,
    /// the default of <c>Paths:Prices</c>, which the <c>run</c> command loads after the tier selection and the task load and before the key check (AJ-071).
    /// </summary>
    public static TempWorkspace Create()
    {
        TempWorkspace workspace = CreateEmpty();
        workspace.WriteFile(Path.Combine("config", "appsettings.json"), File.ReadAllText(ConfigurationFixtures.AppSettingsPath()));
        workspace.WriteFile(Path.Combine("config", "prices.json"), File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "config", "prices.json")));
        return workspace;
    }

    /// <summary>Creates an empty workspace.</summary>
    public static TempWorkspace CreateEmpty()
    {
        string root = Path.Combine(Path.GetTempPath(), "agentjoules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TempWorkspace(root);
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="relativePath"/> (UTF-8 without a byte order mark), creating folders, and returns the full path.</summary>
    public string WriteFile(string relativePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8WithoutBom);
        return path;
    }

    /// <summary>Replaces the workspace configuration file with <paramref name="json"/>.</summary>
    public void WriteConfig(string json) => WriteFile(Path.Combine("config", "appsettings.json"), json);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder that cannot be removed (a file still open) must not fail the test that used it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}