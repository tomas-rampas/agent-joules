using AgentJoules.Cli.Output;
using AgentJoules.Core.Security;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

[Binding]
public sealed class ConfigurationSteps : IDisposable
{
    private const string KeyPlaceholder = "<key>";

    private SecretRedactor _redactor = new();
    private StringWriter? _inner;
    private RedactingTextWriter? _writer;
    private string _opaqueKey = string.Empty;
    private string _input = string.Empty;
    private string? _result;
    private string? _secondResult;
    private Exception? _thrown;

    public void Dispose()
    {
        _writer?.Dispose();
        _inner?.Dispose();
    }

    [Given(@"a redactor registered with a runtime-built opaque key")]
    public void GivenARedactorRegisteredWithARuntimeBuiltOpaqueKey()
    {
        _opaqueKey = Guid.NewGuid().ToString("N");
        _redactor = new SecretRedactor();
        _redactor.Register(_opaqueKey);
    }

    [Given(@"a redactor with no registered values")]
    public void GivenARedactorWithNoRegisteredValues()
    {
        _redactor = new SecretRedactor();
    }

    [Given(@"a redactor registered with null, empty and whitespace values")]
    public void GivenARedactorRegisteredWithNullEmptyAndWhitespaceValues()
    {
        _redactor = new SecretRedactor();
        _redactor.Register(null);
        _redactor.Register(string.Empty);
        _redactor.Register("   ");
    }

    [Given(@"a redactor registered with the 5-character value ""([^""]*)""")]
    public void GivenARedactorRegisteredWithTheFiveCharacterValue(string value)
    {
        Assert.Equal(5, value.Length);
        _redactor = new SecretRedactor();
        _redactor.Register(value);
    }

    [Given(@"a redactor")]
    public void GivenARedactor()
    {
        _redactor = new SecretRedactor();
    }

    [Given(@"the text ""([^""]*)""")]
    public void GivenTheText(string text)
    {
        _redactor = new SecretRedactor();
        _input = text;
    }

    [Given(@"a RedactingTextWriter over a StringWriter")]
    public void GivenARedactingTextWriterOverAStringWriter()
    {
        _redactor = new SecretRedactor();
        _inner = new StringWriter();
        _writer = new RedactingTextWriter(_inner, _redactor);
    }

    [When(@"I redact ""([^""]*)""")]
    public void WhenIRedactText(string text)
    {
        _input = text.Replace(KeyPlaceholder, _opaqueKey, StringComparison.Ordinal);
        _thrown = Record.Exception(() => _result = _redactor.Redact(_input));
    }

    [When(@"I redact a runtime-built ""([^""]*)"" plus 16 characters")]
    public void WhenIRedactARuntimeBuiltValuePlus16Characters(string prefix)
    {
        _input = string.Concat(prefix, "abcdef", "1234567890");
        Assert.Equal(prefix.Length + 16, _input.Length);
        _thrown = Record.Exception(() => _result = _redactor.Redact(_input));
    }

    [When(@"I redact null")]
    public void WhenIRedactNull()
    {
        _thrown = Record.Exception(() => _result = _redactor.Redact(null));
    }

    [When(@"I redact it twice")]
    public void WhenIRedactItTwice()
    {
        _thrown = Record.Exception(() =>
        {
            _result = _redactor.Redact(_input);
            _secondResult = _redactor.Redact(_result);
        });
    }

    [When(@"""([^""]*)"" and ""([^""]*)"" are written separately and then a newline")]
    public void WhenTwoPartsAreWrittenSeparatelyAndThenANewline(string first, string second)
    {
        Assert.NotNull(_writer);
        _writer.Write(first);
        _writer.Write(second);
        _writer.Write("\n");
    }

    [Then(@"both occurrences become ""([^""]*)"" and no character of the key remains")]
    public void ThenBothOccurrencesBecomeTheMaskAndNoCharacterOfTheKeyRemains(string mask)
    {
        string expected = _input.Replace(_opaqueKey, mask, StringComparison.Ordinal);
        Assert.Null(_thrown);
        Assert.Equal(expected, _result);
        Assert.Equal(2, CountOccurrences(_result, mask));
        for (int start = 0; start + 6 <= _opaqueKey.Length; start++)
        {
            Assert.DoesNotContain(_opaqueKey.Substring(start, 6), _result, StringComparison.Ordinal);
        }
    }

    [Then(@"the result is ""([^""]*)""")]
    public void ThenTheResultIs(string expected)
    {
        Assert.Null(_thrown);
        Assert.Equal(expected, _result);
    }

    [Then(@"the inner writer receives only ""([^""]*)"" followed by the newline")]
    public void ThenTheInnerWriterReceivesOnlyTheMaskedKeyFollowedByTheNewline(string expected)
    {
        Assert.NotNull(_inner);
        Assert.Equal(expected + "\n", _inner.ToString());
    }

    [Then(@"the text is unchanged and no exception is thrown")]
    public void ThenTheTextIsUnchangedAndNoExceptionIsThrown()
    {
        Assert.Null(_thrown);
        Assert.Equal(_input, _result);
    }

    [Then(@"the output is ""([^""]*)"" because every registered value is masked completely")]
    public void ThenTheOutputIsBecauseEveryRegisteredValueIsMaskedCompletely(string expected)
    {
        Assert.Null(_thrown);
        Assert.Equal(expected, _result);
    }

    [Then(@"the result is an empty string and no exception is thrown")]
    public void ThenTheResultIsAnEmptyStringAndNoExceptionIsThrown()
    {
        Assert.Null(_thrown);
        Assert.Equal(string.Empty, _result);
    }

    [Then(@"both results equal ""([^""]*)""")]
    public void ThenBothResultsEqual(string expected)
    {
        Assert.Null(_thrown);
        Assert.Equal(expected, _result);
        Assert.Equal(expected, _secondResult);
        Assert.Equal(_result, _redactor.Redact(_result));
    }

    private static int CountOccurrences(string? text, string value)
    {
        int count = 0;
        int index = 0;
        while (text is not null && (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}