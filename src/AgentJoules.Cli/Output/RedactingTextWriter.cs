using System.Text;

using AgentJoules.Core.Security;

namespace AgentJoules.Cli.Output;

/// <summary>
/// A <see cref="TextWriter"/> that holds text until a newline and passes each complete line through
/// <see cref="SecretRedactor.Redact(string?)"/> before it reaches the inner writer, so a key split across several
/// writes never reaches the inner writer unmasked. <see cref="Flush"/> forwards complete lines only (they were
/// already forwarded when written) and keeps an unfinished line buffered; <c>Dispose</c> drains the remainder.
/// The wrapper owns and disposes the inner writer unless <c>leaveOpen</c> is true, which AJ-034 uses for
/// Console.Out and Console.Error; the host disposes the wrappers at exit (AJ-034). With leaveOpen, the writer
/// stays usable after Dispose and drains again on every later Dispose, because the AJ-034 console wrappers may
/// receive output after the first drain and before the ProcessExit drain; without leaveOpen any Write or Flush
/// after Dispose throws ObjectDisposedException. Instances are not thread-safe.
/// <see cref="TextWriter.Synchronized(TextWriter)"/> serialises calls, not lines, so every producer must write a
/// whole line per call.
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
        ThrowIfClosed();
        _buffer.Append(value);
        if (value == '\n')
        {
            ForwardCompleteLines();
        }
    }

    /// <inheritdoc />
    public override void Write(string? value) => Write(value.AsSpan());

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer)
    {
        ThrowIfClosed();
        int lastNewline = buffer.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            _buffer.Append(buffer);
            return;
        }

        _buffer.Append(buffer[..(lastNewline + 1)]);
        ForwardCompleteLines();
        _buffer.Append(buffer[(lastNewline + 1)..]);
    }

    /// <inheritdoc />
    public override void Flush()
    {
        ThrowIfClosed();
        _inner.Flush();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && (_leaveOpen || !_disposed))
        {
            _disposed = true;
            try
            {
                Drain();
                _inner.Flush();
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

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_disposed && !_leaveOpen, this);

    private void ForwardCompleteLines()
    {
        string pending = _buffer.ToString();
        int lastNewline = pending.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return;
        }

        _buffer.Remove(0, lastNewline + 1);
        _inner.Write(_redactor.Redact(pending[..(lastNewline + 1)]));
    }

    private void Drain()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        string pending = _buffer.ToString();
        _buffer.Clear();
        _inner.Write(_redactor.Redact(pending));
    }
}