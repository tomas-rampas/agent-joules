using System.Text;

using AgentJoules.Core.Security;

namespace AgentJoules.Cli.Output;

/// <summary>
/// A <see cref="TextWriter"/> that holds text until a newline or <see cref="Flush"/> and passes it through
/// <see cref="SecretRedactor.Redact(string?)"/> before it reaches the inner writer, so a key split across several
/// writes never reaches the inner writer unmasked. The wrapper owns and disposes the inner writer unless
/// <c>leaveOpen</c> is true, which AJ-034 uses for Console.Out and Console.Error. Instances are not
/// thread-safe; wrap one in <see cref="TextWriter.Synchronized(TextWriter)"/> when several threads write to it.
/// </summary>
public sealed class RedactingTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly SecretRedactor _redactor;
    private readonly StringBuilder _buffer = new();
    private readonly bool _leaveOpen;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedactingTextWriter"/> class.
    /// </summary>
    /// <param name="inner">The writer that receives redacted text.</param>
    /// <param name="redactor">The redactor applied to every line.</param>
    /// <param name="leaveOpen">
    /// True to leave <paramref name="inner"/> open when this instance is disposed; false (the default) to dispose it.
    /// </param>
    public RedactingTextWriter(TextWriter inner, SecretRedactor redactor, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(redactor);

        _inner = inner;
        _redactor = redactor;
        _leaveOpen = leaveOpen;
        NewLine = inner.NewLine;
    }

    /// <inheritdoc />
    public override Encoding Encoding => _inner.Encoding;

    /// <inheritdoc />
    public override IFormatProvider FormatProvider => _inner.FormatProvider;

    /// <inheritdoc />
    public override void Write(char value)
    {
        _buffer.Append(value);
        if (value == '\n')
        {
            WriteBufferThrough();
        }
    }

    /// <inheritdoc />
    public override void Write(string? value) => Write(value.AsSpan());

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer)
    {
        int lastNewline = buffer.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            _buffer.Append(buffer);
            return;
        }

        _buffer.Append(buffer[..(lastNewline + 1)]);
        WriteBufferThrough();
        _buffer.Append(buffer[(lastNewline + 1)..]);
    }

    /// <inheritdoc />
    public override void Flush()
    {
        WriteBufferThrough();
        _inner.Flush();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                Flush();
            }
            finally
            {
                if (!_leaveOpen)
                {
                    _inner.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    private void WriteBufferThrough()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        string redacted = _redactor.Redact(_buffer.ToString());
        _buffer.Clear();
        _inner.Write(redacted);
    }
}