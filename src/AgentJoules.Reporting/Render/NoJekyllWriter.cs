namespace AgentJoules.Reporting.Render;

/// <summary>
/// Makes sure the folder GitHub Pages serves holds an empty <c>.nojekyll</c> file (AJ-085, REQ-030, design 42), so Pages publishes the files as they are and does not run Jekyll over them. The file is created
/// when absent and never rewritten: an existing file is not opened for writing, so its bytes and its time stamp stay as they are.
/// </summary>
public static class NoJekyllWriter
{
    /// <summary>The name of the marker file.</summary>
    public const string FileName = ".nojekyll";

    /// <summary>
    /// Creates an empty <c>.nojekyll</c> in <paramref name="docsDirectory"/> when the folder exists and the file does not. The folder is never created, and a file that exists is never opened for writing; a create
    /// that loses a race to another writer counts as present. A path that is a file instead of a folder is not a folder, so nothing is created and the result is false. A folder at the marker path is not a marker
    /// file: on Windows the create raises <see cref="UnauthorizedAccessException"/>; on Unix it raises an <see cref="IOException"/> and the <c>File.Exists</c> filter (false for a folder) lets it through.
    /// </summary>
    /// <param name="docsDirectory">The folder that GitHub Pages serves (<c>docs</c> under the working directory).</param>
    /// <returns>True when this call created the file; false when the file was already there, the folder does not exist, or <paramref name="docsDirectory"/> is a file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="docsDirectory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="docsDirectory"/> is empty or white space.</exception>
    /// <exception cref="IOException">The file could not be created for a reason other than that it exists (on Unix, including a folder at the marker path).</exception>
    /// <exception cref="UnauthorizedAccessException">The folder does not allow the file to be created (on Windows, including a folder at the marker path).</exception>
    public static bool EnsureExists(string docsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsDirectory);

        if (!Directory.Exists(docsDirectory))
        {
            return false;
        }

        string path = Path.Combine(docsDirectory, FileName);
        try
        {
            // CreateNew fails when the path exists, whatever it is, so an existing marker is never truncated or rewritten.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }
}