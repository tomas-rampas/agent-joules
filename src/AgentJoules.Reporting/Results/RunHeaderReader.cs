using System.Buffers;
using System.Text.Json;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;

namespace AgentJoules.Reporting.Results;

/// <summary>
/// What the selection of a report needs from the <c>run</c> object of a run file (AJ-081, REQ-016): the tier, the status and the flags, as the file states them.
/// </summary>
/// <param name="Tier">The tier of <c>run.tier</c>.</param>
/// <param name="Status">The status of <c>run.status</c>.</param>
/// <param name="Flags">The strings of <c>run.flags</c>, in file order; empty when the member is absent or null. The names are not checked against <c>RunFlagNames</c>: the full read of the selected file does that.</param>
public sealed record RunHeader(Tier Tier, RunStatus Status, IReadOnlyList<string> Flags);

/// <summary>
/// Reads the <c>run</c> header of a run file and nothing else (AJ-081, REQ-016): at most 64 KiB (<see cref="MaxHeaderBytes"/>) plus one probe byte, which tells a header cut off by the limit from a file that ended, are read of
/// any file; a UTF-8 byte order mark is skipped, the top-level members before <c>run</c> are skipped, and the parse ends when the <c>run</c> object closes, so nothing after it is parsed, however large or invalid. The
/// document is never deserialised and no schema or model rule runs: a file this reader accepts may still be refused by <see cref="RunFileValidator"/>. A header that cannot be read throws an
/// <see cref="InvalidDataException"/> whose message is exactly one reason token, never a path, a value or the text of a parser error, and which carries no inner exception: <c>header-not-json</c>, <c>run-missing</c>,
/// <c>header-too-long</c>, <c>tier-missing</c>, <c>tier-unknown</c>, <c>status-missing</c>, <c>status-unknown</c> or <c>flags-not-strings</c>. A path that is not a regular file (a pipe or a device) is never read: a
/// stream that cannot seek is refused after it is opened as an <see cref="IOException"/>, and on Unix, where opening a FIFO blocks, an entry of size zero is not opened at all and is <c>header-not-json</c>.
/// </summary>
public static class RunHeaderReader
{
    /// <summary>The most bytes of a file that are parsed: 64 KiB (one more probe byte is read to tell a cut-off header from a file that ended). A <c>run</c> object that is not closed within them is <c>header-too-long</c>.</summary>
    public const int MaxHeaderBytes = 65536;

    private const string NotJson = "header-not-json";
    private const string RunMissing = "run-missing";
    private const string TooLong = "header-too-long";
    private const string TierMissing = "tier-missing";
    private const string TierUnknown = "tier-unknown";
    private const string StatusMissing = "status-missing";
    private const string StatusUnknown = "status-unknown";
    private const string FlagsNotStrings = "flags-not-strings";
    private const int BomLength = 3;

    private static readonly Tier[] Tiers = [Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius];

    /// <summary>Reads the header of a run file.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The tier, the status and the flags of <c>run</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidDataException">The header cannot be read; the message is one reason token (see the type).</exception>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static RunHeader Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] buffer = RentBuffer();
        try
        {
            return Read(path, buffer);
        }
        finally
        {
            ReturnBuffer(buffer);
        }
    }

    /// <summary>A buffer for <see cref="Read(string, byte[])"/>; a caller that reads many files rents one and returns it with <see cref="ReturnBuffer"/>, and never shares it between threads.</summary>
    internal static byte[] RentBuffer() => ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);

    /// <summary>Returns a buffer of <see cref="RentBuffer"/>, cleared: it held the text of run files.</summary>
    internal static void ReturnBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer, clearArray: true);

    /// <summary>Reads the header of a run file into a caller-owned buffer.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="buffer">A buffer from <see cref="RentBuffer"/>.</param>
    /// <returns>The tier, the status and the flags of <c>run</c>.</returns>
    internal static RunHeader Read(string path, byte[] buffer)
    {
        RefuseWithoutOpening(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

        return Read(stream, buffer);
    }

    /// <summary>Reads the header from a stream; no more than <see cref="MaxHeaderBytes"/> plus one byte is ever requested of it.</summary>
    /// <param name="stream">A readable stream that can seek.</param>
    /// <returns>The tier, the status and the flags of <c>run</c>.</returns>
    /// <exception cref="IOException">The stream cannot seek: a pipe or a device is not a run file.</exception>
    internal static RunHeader Read(Stream stream)
    {
        byte[] buffer = RentBuffer();
        try
        {
            return Read(stream, buffer);
        }
        finally
        {
            ReturnBuffer(buffer);
        }
    }

    private static RunHeader Read(Stream stream, byte[] buffer)
    {
        // A pipe can block a read for ever and the null device opens as an empty file; neither is a run file.
        if (!stream.CanSeek)
        {
            throw new IOException("The path is not a regular file.");
        }

        int length = 0;
        int read;
        while (length < MaxHeaderBytes && (read = stream.Read(buffer, length, MaxHeaderBytes - length)) > 0)
        {
            length += read;
        }

        bool more = length == MaxHeaderBytes && stream.ReadByte() >= 0;
        int start = length >= BomLength && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? BomLength : 0;

        return Parse(buffer.AsSpan(start, length - start), more);
    }

    // Opening a FIFO for reading blocks until a writer appears, and .NET has no file type on Unix; a FIFO, a character device and an empty file all report the size zero,
    // and none of them holds a header, so none is opened. Windows has no such block: a pipe or a device opens at once and is refused by its stream.
    private static void RefuseWithoutOpening(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new FileInfo(path);
        FileSystemInfo target = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        if (target is FileInfo { Exists: true, Length: 0 })
        {
            throw new InvalidDataException(NotJson);
        }
    }

    private static RunHeader Parse(ReadOnlySpan<byte> bytes, bool more)
    {
        try
        {
            return ParseHeader(bytes, more);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A syntax error, a depth overrun and a string that is not valid UTF-8 all mean the same to the caller; none of their texts may reach it.
            throw new InvalidDataException(NotJson);
        }
    }

    // `more` is true when the file goes on after these bytes: a header that ends inside them is then too long, otherwise the file ended inside the header and is not JSON.
    private static RunHeader ParseHeader(ReadOnlySpan<byte> bytes, bool more)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, default);
        string endOfData = more ? TooLong : NotJson;

        if (!reader.Read())
        {
            throw new InvalidDataException(NotJson);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException(NotJson);
        }

        while (true)
        {
            if (!reader.Read())
            {
                throw new InvalidDataException(endOfData);
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                throw new InvalidDataException(RunMissing);
            }

            bool isRun = reader.ValueTextEquals("run"u8);
            if (!reader.Read())
            {
                throw new InvalidDataException(endOfData);
            }

            if (isRun)
            {
                return ParseRun(ref reader, endOfData);
            }

            if (!reader.TrySkip())
            {
                throw new InvalidDataException(endOfData);
            }
        }
    }

    private static RunHeader ParseRun(ref Utf8JsonReader reader, string endOfData)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException(RunMissing);
        }

        Tier? tier = null;
        RunStatus? status = null;
        List<string> flags = [];

        while (true)
        {
            if (!reader.Read())
            {
                throw new InvalidDataException(endOfData);
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            bool isTier = reader.ValueTextEquals("tier"u8);
            bool isStatus = reader.ValueTextEquals("status"u8);
            bool isFlags = reader.ValueTextEquals("flags"u8);
            if (!reader.Read())
            {
                throw new InvalidDataException(endOfData);
            }

            if (isTier)
            {
                tier = ReadTier(ref reader);
            }
            else if (isStatus)
            {
                status = ReadStatus(ref reader);
            }
            else if (isFlags)
            {
                flags = ReadFlags(ref reader, endOfData);
            }
            else if (!reader.TrySkip())
            {
                throw new InvalidDataException(endOfData);
            }
        }

        return new RunHeader(
            tier ?? throw new InvalidDataException(TierMissing),
            status ?? throw new InvalidDataException(StatusMissing),
            flags.AsReadOnly());
    }

    private static Tier ReadTier(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new InvalidDataException(TierUnknown);
        }

        for (int index = 0; index < Tiers.Length; index++)
        {
            if (reader.ValueTextEquals(TierOptions.NameOf(Tiers[index])))
            {
                return Tiers[index];
            }
        }

        throw new InvalidDataException(TierUnknown);
    }

    private static RunStatus ReadStatus(ref Utf8JsonReader reader) =>
        reader.TokenType != JsonTokenType.String ? throw new InvalidDataException(StatusUnknown)
        : reader.ValueTextEquals("running"u8) ? RunStatus.Running
        : reader.ValueTextEquals("completed"u8) ? RunStatus.Completed
        : reader.ValueTextEquals("failed"u8) ? RunStatus.Failed
        : reader.ValueTextEquals("partial"u8) ? RunStatus.Partial
        : reader.ValueTextEquals("incomplete"u8) ? RunStatus.Incomplete
        : throw new InvalidDataException(StatusUnknown);

    private static List<string> ReadFlags(ref Utf8JsonReader reader, string endOfData)
    {
        List<string> flags = [];
        if (reader.TokenType == JsonTokenType.Null)
        {
            return flags;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new InvalidDataException(FlagsNotStrings);
        }

        while (true)
        {
            if (!reader.Read())
            {
                throw new InvalidDataException(endOfData);
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return flags;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new InvalidDataException(FlagsNotStrings);
            }

            flags.Add(reader.GetString()!);
        }
    }
}