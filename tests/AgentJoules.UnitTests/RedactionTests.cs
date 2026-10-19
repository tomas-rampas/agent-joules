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

    private static void AssertNoFragmentOf(string key, string result)
    {
        Assert.DoesNotContain(key, result, StringComparison.Ordinal);
        for (int start = 0; start + 6 <= key.Length; start++)
        {
            Assert.DoesNotContain(key.Substring(start, 6), result, StringComparison.Ordinal);
        }
    }

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
    public void Redact_RegisteredValueInsideSentence_ReplacesTheWholeValueWithStars()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(key);

        string result = redactor.Redact($"request failed for {key} with status 401");

        Assert.Equal("request failed for **** with status 401", result);
        AssertNoFragmentOf(key, result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredValueTwiceInOneString_ReplacesBothOccurrences()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(key);

        string result = redactor.Redact($"first {key} then {key}");

        Assert.Equal("first **** then ****", result);
        AssertNoFragmentOf(key, result);
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
    public void RedactingTextWriter_FlushWithoutNewline_ForwardsNothingAndDisposeWritesRedactedRemainder()
    {
        using var inner = new StringWriter();

        using (var writer = new RedactingTextWriter(inner, new SecretRedactor(), leaveOpen: true))
        {
            writer.Write("token ");
            writer.Write(BuildNvidiaKey());
            writer.Flush();

            Assert.Equal(string.Empty, inner.ToString());
        }

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

        Assert.Equal("****\n", inner.ToString());
        AssertNoFragmentOf(key, inner.ToString());
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
    public void Redact_RegisteredValueThatIsAPrefixOfAnNvidiaKey_StillMasksTheWholeKey()
    {
        var redactor = new SecretRedactor();
        redactor.Register("nvapi-ab");
        string input = $"auth {BuildNvidiaKey()} ok a-b";

        string once = redactor.Redact(input);
        string twice = redactor.Redact(once);

        Assert.DoesNotContain(Suffix, once, StringComparison.Ordinal);
        Assert.Equal("auth " + MaskedNvidiaKey + " ok a-b", once);
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
    public void Redact_RegisteredValueOfSevenCharacters_IsFullyMasked()
    {
        var redactor = new SecretRedactor();
        redactor.Register("abcdefg");

        string result = redactor.Redact("x abcdefg y");

        Assert.Equal("x **** y", result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithTrailingCarriageReturn_AlsoMasksTheBareValue()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(key + "\r");

        string result = redactor.Redact($"leaked {key} here");

        Assert.Equal("leaked **** here", result);
        AssertNoFragmentOf(key, result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_Dispose_FlushesTheRemainderAndDisposesTheInnerWriterByDefault()
    {
        using var inner = new StringWriter();

        using (var writer = new RedactingTextWriter(inner, new SecretRedactor()))
        {
            writer.Write(BuildNvidiaKey());
        }

        Assert.Equal(MaskedNvidiaKey, inner.ToString());
        Assert.Throws<ObjectDisposedException>(() => inner.Write("x"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_LeaveOpen_DoesNotDisposeInner()
    {
        using var inner = new StringWriter();

        using (var writer = new RedactingTextWriter(inner, new SecretRedactor(), leaveOpen: true))
        {
            writer.Write(BuildNvidiaKey());
        }

        inner.Write(" tail");

        Assert.Equal(MaskedNvidiaKey + " tail", inner.ToString());
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

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_CharacterByCharacterWrites_ReachTheInnerWriterOnlyAfterTheNewline()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor());

        foreach (char character in BuildNvidiaKey())
        {
            writer.Write(character);
            Assert.Equal(string.Empty, inner.ToString());
        }

        writer.Write('\n');

        Assert.Equal(MaskedNvidiaKey + "\n", inner.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_FlushBetweenTwoHalvesOfAKey_DoesNotForwardEitherHalfUnmasked()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor());

        writer.Write("nvapi-");
        writer.Flush();

        Assert.Equal(string.Empty, inner.ToString());

        writer.Write(Suffix);
        writer.Write("\n");

        Assert.Equal(MaskedNvidiaKey + "\n", inner.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredJwtShapedValueGluedAfterAnNvidiaKey_LeavesNothingOfEither()
    {
        string jwt = string.Join('.', "eyJhbGciOiJIUzI1NiJ9", "eyJzdWIiOiIxMjM0In0", "c2lnbmF0dXJl");
        var redactor = new SecretRedactor();
        redactor.Register(jwt);

        string result = redactor.Redact(BuildNvidiaKey() + jwt);

        Assert.Equal(MaskedNvidiaKey, result);
        Assert.Equal(result, redactor.Redact(result));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_TwoOverlappingRegisteredValues_AreMaskedAsOne()
    {
        var redactor = new SecretRedactor();
        redactor.Register("SECRETAAAA1");
        redactor.Register("AAA1BBBB");

        string result = redactor.Redact("x SECRETAAAA1BBBB y");

        Assert.Equal("x **** y", result);
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Redact_RegisteredValueStartingWithTheNvidiaPrefix_KeepsOnlyThePublicPrefix()
    {
        string value = string.Concat("nvapi-", "abcd", ".efgh1234");
        var redactor = new SecretRedactor();
        redactor.Register(value);

        string result = redactor.Redact($"see {value} end");

        Assert.Equal("see " + MaskedNvidiaKey + " end", result);
        Assert.Equal(result, redactor.Redact(result));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ShortStarredOrMaskFragmentValues_AreIgnored()
    {
        var redactor = new SecretRedactor();
        foreach (string ignored in new[] { "*", "**", "nvapi", "vapi-", "api", "-", "x", "0" })
        {
            redactor.Register(ignored);
        }

        redactor.Register("abc12");
        const string sentence = "x api - * ** nvapi vapi- 0 done";

        string once = redactor.Redact(sentence);

        Assert.Equal(sentence, once);
        Assert.Equal(once, redactor.Redact(once));
        Assert.Equal("t **** t", redactor.Redact("t abc12 t"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_ValueRegisteredWithCrLf_PreservesTheLineTerminators()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(key + "\r\n");
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, redactor);

        writer.WriteLine("key=" + key);
        writer.WriteLine("next");

        string newLine = Environment.NewLine;
        Assert.Equal("key=****" + newLine + "next" + newLine, inner.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithByteOrderMarkOrZeroWidthSpace_MasksTheBareValue()
    {
        string key = BuildOpaqueKey();
        var withBom = new SecretRedactor();
        withBom.Register(char.ConvertFromUtf32(0xFEFF) + key);
        var withZeroWidth = new SecretRedactor();
        withZeroWidth.Register(char.ConvertFromUtf32(0x200B) + key);

        Assert.Equal("k=****", withBom.Redact("k=" + key));
        Assert.Equal("k=****", withZeroWidth.Redact("k=" + key));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_LeaveOpenSecondDispose_DrainsATailWrittenInBetween()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor(), leaveOpen: true);

        writer.Write("a " + BuildNvidiaKey());
        writer.Dispose();

        Assert.Equal("a " + MaskedNvidiaKey, inner.ToString());

        writer.Write(" b " + BuildNvidiaKey());
        writer.Dispose();

        Assert.Equal("a " + MaskedNvidiaKey + " b " + MaskedNvidiaKey, inner.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_WriteOrFlushAfterDisposeWithoutLeaveOpen_ThrowsObjectDisposedException()
    {
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, new SecretRedactor());
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.Write("x"));
        Assert.Throws<ObjectDisposedException>(() => writer.Write('x'));
        Assert.Throws<ObjectDisposedException>(() => writer.Flush());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithInteriorZeroWidthSpace_MasksTheDecoratedAndTheBareForm()
    {
        string first = BuildOpaqueKey();
        string second = BuildOpaqueKey();
        string decorated = first + char.ConvertFromUtf32(0x200B) + second;
        var redactor = new SecretRedactor();

        bool registered = redactor.Register(decorated);

        Assert.True(registered);
        Assert.Equal("echo ****", redactor.Redact("echo " + decorated));
        Assert.Equal("echo ****", redactor.Redact("echo " + first + second));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithAppendedTagCharacter_MasksTheBareKey()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();

        redactor.Register(key + char.ConvertFromUtf32(0xE0041));

        Assert.Equal("k=****", redactor.Redact("k=" + key));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ReturnsFalseForARejectedValueAndTrueForAnAcceptedOne()
    {
        var redactor = new SecretRedactor();

        Assert.False(redactor.Register("P@ss*word99"));
        Assert.False(redactor.Register(null));
        Assert.False(redactor.Register("abc"));
        Assert.True(redactor.Register(BuildOpaqueKey()));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_MultiLineValue_IsMaskedWhenItOccursWhole()
    {
        string first = BuildOpaqueKey();
        string second = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(first + "\n" + second);

        string result = redactor.Redact("v=" + first + "\n" + second + " end");

        Assert.Equal("v=**** end", result);
    }
}