using AgentJoules.Cli.Output;
using AgentJoules.Core.Security;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed class RedactionTests
{
    private const string Suffix = "abcdef1234567890";
    private const string MaskedNvidiaKey = "nvapi-****";

    private static string BuildNvidiaKey() => string.Concat("nvapi-", Suffix);

    private static string BuildOpaqueKey() => Guid.NewGuid().ToString("N");

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RuntimeBuiltNvidiaKey_ReturnsPrefixPlusStarsWithoutSuffix()
    {
        var redactor = new SecretRedactor();

        string result = redactor.Redact(BuildNvidiaKey());

        Assert.Equal(MaskedNvidiaKey, result);
        Assert.DoesNotContain(Suffix, result, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredValueInsideSentence_ReplacesWithFirstSixCharactersPlusStars()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(key);

        string result = redactor.Redact($"request failed for {key} with status 401");

        string expected = $"request failed for {key[..6]}**** with status 401";
        Assert.Equal(expected, result);
        Assert.DoesNotContain(key[6..], result, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredValueTwiceInOneString_ReplacesBothOccurrences()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(key);

        string result = redactor.Redact($"first {key} then {key}");

        string masked = $"{key[..6]}****";
        Assert.Equal($"first {masked} then {masked}", result);
        Assert.DoesNotContain(key[6..], result, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_UnregisteredNvidiaPattern_MasksTheValueAndIsIdempotent()
    {
        var redactor = new SecretRedactor();
        string input = string.Concat("header ", "nvapi-", "ab_C-9z", " trailer");

        string once = redactor.Redact(input);
        string twice = redactor.Redact(once);

        Assert.Equal($"header {MaskedNvidiaKey} trailer", once);
        Assert.Equal(once, twice);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_ShortestPatternMatchOfFourCharacters_IsMasked()
    {
        var redactor = new SecretRedactor();

        string result = redactor.Redact(string.Concat("nvapi-", "wxyz"));

        Assert.Equal(MaskedNvidiaKey, result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_AlreadyMaskedText_IsUnchanged()
    {
        var redactor = new SecretRedactor();

        string once = redactor.Redact(MaskedNvidiaKey);
        string twice = redactor.Redact(once);

        Assert.Equal(MaskedNvidiaKey, once);
        Assert.Equal(MaskedNvidiaKey, twice);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_KeySplitAcrossTwoWrites_ReachesInnerWriterMaskedAfterNewline()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor());

        writer.Write("nvapi-");
        writer.Write(Suffix);

        Assert.Equal(string.Empty, inner.ToString());

        writer.Write("\n");

        Assert.Equal(MaskedNvidiaKey + "\n", inner.ToString());
        Assert.DoesNotContain(Suffix, inner.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_FlushWithoutNewline_WritesRedactedRemainder()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor());

        writer.Write("token ");
        writer.Write(BuildNvidiaKey());
        writer.Flush();

        Assert.Equal("token " + MaskedNvidiaKey, inner.ToString());
        Assert.DoesNotContain(Suffix, inner.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_RegisteredOpaqueKeySplitAcrossWrites_IsMasked()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(key);
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, redactor);

        writer.Write(key[..10]);
        writer.Write(key[10..]);
        writer.Write("\n");

        Assert.Equal($"{key[..6]}****\n", inner.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_NullEmptyAndWhitespace_AreIgnored()
    {
        var redactor = new SecretRedactor();

        redactor.Register(null);
        redactor.Register(string.Empty);
        redactor.Register("   ");

        Assert.Equal("plain text", redactor.Redact("plain text"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredValueOfFiveCharacters_IsFullyMasked()
    {
        var redactor = new SecretRedactor();
        redactor.Register("abc12");

        string result = redactor.Redact("token abc12 failed");

        Assert.Equal("token **** failed", result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_Null_ReturnsEmptyString()
    {
        var redactor = new SecretRedactor();

        string result = redactor.Redact(null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredValueInsideNvidiaPrefix_StillMasksTheUnregisteredKey()
    {
        var redactor = new SecretRedactor();
        redactor.Register("-");
        string input = $"auth {BuildNvidiaKey()} ok a-b";

        string once = redactor.Redact(input);
        string twice = redactor.Redact(once);

        Assert.DoesNotContain(Suffix, once, StringComparison.Ordinal);
        Assert.Contains("nvapi", once, StringComparison.Ordinal);
        Assert.Equal(once, twice);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredValueOfSixCharacters_IsFullyMasked()
    {
        var redactor = new SecretRedactor();
        redactor.Register("abc123");

        string result = redactor.Redact("x abc123 y");

        Assert.Equal("x **** y", result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredValueOfSevenCharacters_KeepsTheFirstSix()
    {
        var redactor = new SecretRedactor();
        redactor.Register("abcdefg");

        string result = redactor.Redact("x abcdefg y");

        Assert.Equal("x abcdef**** y", result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithTrailingCarriageReturn_AlsoMasksTheBareValue()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(key + "\r");

        string result = redactor.Redact($"leaked {key} here");

        Assert.Equal($"leaked {key[..6]}**** here", result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_TextAfterTheLastNewline_StaysBufferedUntilTheNextNewline()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor());

        writer.Write("a\n" + "nvapi-");

        Assert.Equal("a\n", inner.ToString());

        writer.Write(Suffix);
        writer.Write("\n");

        Assert.Equal("a\n" + MaskedNvidiaKey + "\n", inner.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_CharacterNewline_TriggersTheLineFlush()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor());

        writer.Write("nvapi-");
        writer.Write(Suffix);
        writer.Write('\n');

        Assert.Equal(MaskedNvidiaKey + "\n", inner.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_WriteLine_MasksTheLineAndEndsItWithTheNewLine()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor());

        writer.WriteLine(BuildNvidiaKey());

        Assert.Equal(MaskedNvidiaKey + Environment.NewLine, inner.ToString());
    }
}