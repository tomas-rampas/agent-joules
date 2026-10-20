using System.Text;

namespace AgentJoules.TestSupport;

/// <summary>
/// A <see cref="TextWriter"/> that records every write call as one string, so a test can assert the call boundaries a producer
/// produced (criterion 12 of AJ-034: every call ends with a newline except the final drain). Empty writes are not calls. Behind the redacting writer
/// it only sees what that writer forwards (whole lines); in front of it, as the direct writer of a producer, it sees what the producer wrote.
/// </summary>
public sealed class SpyTextWriter : TextWriter
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>The recorded calls, in order.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <inheritdoc />
    public override void Write(char value) => Record(value.ToString());

    /// <inheritdoc />
    public override void Write(string? value) => Record(value);

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Record(new string(buffer, index, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer) => Record(new string(buffer));

    /// <summary>A <c>WriteLine(string)</c> is one call: the text and the line terminator together, the unit the producers promise.</summary>
    public override void WriteLine(string? value) => Record((value ?? string.Empty) + NewLine);

    /// <inheritdoc />
    public override void WriteLine() => Record(NewLine);

    /// <summary>Everything written so far, as one string.</summary>
    public override string ToString() => string.Concat(Calls);

    private void Record(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _calls.Add(text);
        }
    }
}