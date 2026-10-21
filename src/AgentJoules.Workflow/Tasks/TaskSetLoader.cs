using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Tasks;

namespace AgentJoules.Workflow.Tasks;

/// <summary>
/// Loads the fixed task set of a run from a folder of <c>*.json</c> files (REQ-012, design 3.4): one file per task, read in ordinal file-name order, each
/// held to <see cref="TaskDefinition"/> through <see cref="CoreJsonContext"/>. Every failure is a <see cref="ConfigurationException"/> (exit 2) whose message
/// names the file by its name only, with the problem as a fixed phrase: neither the absolute path, nor the content of the file, nor the text of a JSON parser
/// (which can quote the document) reaches a message, and no inner exception is attached (its message carries the path).
/// </summary>
/// <remarks>
/// <para>
/// What the folder may hold: a file whose extension is exactly <c>.json</c> is a task file; a file with another extension is not and is ignored; a file whose
/// extension is <c>.json</c> in another case (<c>.JSON</c>) is refused, because on a case-sensitive host it would be skipped silently. Sub-folders are ignored.
/// Hidden files are loaded like any other. A folder that is a link (a symbolic link or a junction) and a task file that is a link are refused, so a task set
/// cannot be redirected out of the repository. A file is read through a stream and refused above 64 KiB, and must be valid UTF-8 (a leading byte order mark is
/// accepted), so the size of a file and the bytes in it are bounded before any parser sees them.
/// </para>
/// <para>The folder is given by the caller (the <c>run</c> command resolves <c>Paths:Tasks</c>); this class adds no option.</para>
/// </remarks>
public static class TaskSetLoader
{
    private const string TaskFileExtension = ".json";
    private const string MemberName = "name";
    private const string MemberPrompt = "prompt";
    private const string MemberMaxOutputTokens = "maxOutputTokens";
    private const int MaxFileBytes = 64 * 1024;

    private static readonly string[] MemberNames = [MemberName, MemberPrompt, MemberMaxOutputTokens];

    // Strict: a byte sequence that is not UTF-8 throws instead of becoming a replacement character.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Loads every task of <paramref name="directory"/> in file-name order.</summary>
    /// <param name="directory">The folder of the task files.</param>
    /// <returns>The tasks, one per file, in ordinal file-name order, as an array that the caller cannot grow or shrink through the list.</returns>
    /// <exception cref="ConfigurationException">The folder is missing, is a link, cannot be listed or holds no <c>.json</c> file, a file cannot be read or is not a valid task, or two files carry the same name.</exception>
    public static IReadOnlyList<TaskDefinition> Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // A trailing separator makes a host resolve a link on its way (the link target of "tasks/" is null on Unix), so the folder is always checked without one.
        directory = Path.TrimEndingDirectorySeparator(directory);
        string[] fileNames = ListTaskFiles(directory);
        if (fileNames.Length == 0)
        {
            throw new ConfigurationException($"no task files (*{TaskFileExtension}) in the tasks folder");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new List<TaskDefinition>(fileNames.Length);
        foreach (string fileName in fileNames)
        {
            TaskDefinition task = Read(directory, fileName);
            if (!names.Add(task.Name))
            {
                throw Rejected(fileName, "duplicate name");
            }

            tasks.Add(task);
        }

        TaskDefinition[] loaded = [.. tasks];
        return loaded;
    }

    /// <summary>Loads the task set of <paramref name="directory"/> and keeps the one task called <paramref name="taskName"/> (the <c>--task</c> option).</summary>
    /// <param name="directory">The folder of the task files.</param>
    /// <param name="taskName">The name of the task to keep.</param>
    /// <returns>An array of exactly that task. Every file of the folder is still checked, so a bad file is found whichever task is asked for.</returns>
    /// <exception cref="ConfigurationException">The set does not load, or no task has that name (the message lists the available names).</exception>
    public static IReadOnlyList<TaskDefinition> Load(string directory, string taskName)
    {
        ArgumentNullException.ThrowIfNull(taskName);

        TaskDefinition[] selected = [Select(Load(directory), taskName)];
        return selected;
    }

    /// <summary>Returns the task called <paramref name="name"/>, matched exactly.</summary>
    /// <param name="tasks">The loaded task set.</param>
    /// <param name="name">The requested name.</param>
    /// <exception cref="ConfigurationException">No task has that name; the message echoes the name bounded and cleaned of control characters, then lists the available names in file order.</exception>
    public static TaskDefinition Select(IReadOnlyList<TaskDefinition> tasks, string name)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(name);

        foreach (TaskDefinition task in tasks)
        {
            if (string.Equals(task.Name, name, StringComparison.Ordinal))
            {
                return task;
            }
        }

        throw new ConfigurationException($"unknown task {TextEcho.ForMessage(name)}; available: {string.Join(", ", tasks.Select(task => task.Name))}");
    }

    private static string[] ListTaskFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new ConfigurationException("the tasks folder does not exist");
        }

        try
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                throw new ConfigurationException("the tasks folder is a link");
            }

            // A candidate is any file whose extension is .json in any case; Read refuses the ones that are not exactly .json.
            return [.. Directory.EnumerateFiles(directory)
                .Select(path => Path.GetFileName(path))
                .Where(fileName => string.Equals(Path.GetExtension(fileName), TaskFileExtension, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // IOException covers a folder that vanished or a path that is too long. Not attached as the inner exception: its message carries the absolute path.
            throw new ConfigurationException("the tasks folder cannot be read");
        }
    }

    private static TaskDefinition Read(string directory, string fileName)
    {
        if (!string.Equals(Path.GetExtension(fileName), TaskFileExtension, StringComparison.Ordinal))
        {
            throw Rejected(fileName, $"file extension must be lowercase {TaskFileExtension}");
        }

        string text = Decode(fileName, ReadBounded(Path.Combine(directory, fileName), fileName));

        try
        {
            return JsonSerializer.Deserialize(text, CoreJsonContext.Default.TaskDefinition) ?? throw Rejected(fileName, "not a JSON object");
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string with a lone surrogate escape cannot be decoded.
            throw Rejected(fileName, Diagnose(text));
        }
        catch (ArgumentException failure)
        {
            throw Rejected(fileName, Describe(failure is TaskDefinitionException rule ? rule.Problem : null));
        }
    }

    private static byte[] ReadBounded(string path, string fileName)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw Rejected(fileName, "is a link");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // One byte more than the cap: reading it proves the file is larger, and no more of a large file is ever read.
            byte[] buffer = new byte[MaxFileBytes + 1];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            return read > MaxFileBytes ? throw Rejected(fileName, "larger than 64 KiB") : buffer.AsSpan(0, read).ToArray();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Not attached as the inner exception: its message carries the absolute path.
            throw Rejected(fileName, "cannot be read");
        }
    }

    private static string Decode(string fileName, byte[] bytes)
    {
        ReadOnlySpan<byte> content = bytes;
        if (content.StartsWith(Encoding.UTF8.Preamble))
        {
            content = content[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw Rejected(fileName, "not valid UTF-8");
        }
    }

    // The deserializer says that the document is wrong, in words that can quote it. This finds which of the fixed phrases is true, from names and kinds only.
    // Reading a string can itself fail (a lone surrogate escape), so the whole analysis answers "malformed JSON" when it does.
    private static string Diagnose(string text)
    {
        try
        {
            return Classify(text);
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or ArgumentException)
        {
            return "malformed JSON";
        }
    }

    private static string Classify(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            return ParsesWithRepeats(text) ? "duplicate member" : "malformed JSON";
        }

        if (root is not JsonObject members)
        {
            return "not a JSON object";
        }

        if (members.Any(member => !MemberNames.Contains(member.Key, StringComparer.Ordinal)))
        {
            return "unknown member";
        }

        string? missing = MemberNames.FirstOrDefault(name => !members.ContainsKey(name));
        if (missing is not null)
        {
            return "missing " + missing;
        }

        // Every member is present; the first one of the wrong kind (a string for the two texts, an int for the limit) is the problem. A right kind with a wrong value is a rule of the record, not found here.
        return !IsText(members[MemberName]) ? "invalid name"
            : !IsText(members[MemberPrompt]) ? "invalid prompt"
            : !IsInteger(members[MemberMaxOutputTokens]) ? "invalid " + MemberMaxOutputTokens
            : "invalid value";
    }

    private static bool IsText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);

    private static bool IsInteger(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out _);

    private static bool ParsesWithRepeats(string text)
    {
        try
        {
            _ = JsonNode.Parse(text, nodeOptions: null, new JsonDocumentOptions { AllowDuplicateProperties = true });
            return true;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            return false;
        }
    }

    // The numbers are the ceilings of TaskDefinition (MaxPromptLength, MaxOutputTokensLimit); the tests pin both.
    private static string Describe(TaskProblem? problem) => problem switch
    {
        TaskProblem.InvalidName => "invalid name",
        TaskProblem.InvalidPrompt => "invalid prompt",
        TaskProblem.PromptTooLong => "prompt longer than 16384 characters",
        TaskProblem.OutputLimitTooSmall => MemberMaxOutputTokens + " must be at least 1",
        TaskProblem.OutputLimitTooLarge => MemberMaxOutputTokens + " must be at most 32768",
        _ => "invalid value",
    };

    private static ConfigurationException Rejected(string fileName, string problem) =>
        new($"tasks file {TextEcho.ForMessage(fileName)}: {problem}");
}