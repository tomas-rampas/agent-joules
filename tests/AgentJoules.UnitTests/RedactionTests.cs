using System.CommandLine;

using AgentJoules.Cli;
using AgentJoules.Cli.Hosting;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Security;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed class RedactionTests
{
    private const string Suffix = "abcdef1234567890";
    private const string MaskedNvidiaKey = "nvapi-****";

    // A command that hangs must fail the test, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

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
    public void Redact_JsonEscapedFormOfAKey_IsMasked()
    {
        // The JSON encoder writes + < > & ' and every non-ASCII character as \uXXXX, so the escaped text of such a key has to be registered with it.
        string[] keys = [string.Concat("k7w", "+", "2qm9xv4t"), string.Concat("caf", ((char)0x00E9).ToString(), "-r8v2kd5m"), string.Concat("a<b>", "&c'", "-p7wshq3n")];
        var redactor = new SecretRedactor();
        foreach (string key in keys)
        {
            Assert.True(redactor.Register(key));
        }

        foreach (string key in keys)
        {
            string escaped = System.Text.Json.JsonSerializer.Serialize(key);
            Assert.NotEqual("\"" + key + "\"", escaped);
            Assert.Equal("\"****\"", redactor.Redact(escaped));
            Assert.Equal("****", redactor.Redact(key));
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
    public void Register_MultiLineValue_MasksEachLineSeparately()
    {
        string first = BuildOpaqueKey();
        string second = BuildOpaqueKey();
        var redactor = new SecretRedactor();

        bool registered = redactor.Register(first + "\n" + second);

        Assert.True(registered);
        Assert.Equal("v=**** end", redactor.Redact("v=" + first + "\n" + second + " end"));
        Assert.Equal("v=**** end", redactor.Redact("v=" + first + " end"));
        Assert.Equal("v=**** end", redactor.Redact("v=" + second + " end"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_KeyFollowedByLineFeedAndLoneSurrogate_StillMasksTheKey()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();

        bool registered = redactor.Register(key + "\n" + (char)0xDC00);

        Assert.True(registered);
        Assert.Equal("k=****", redactor.Redact("k=" + key));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_MultiLineValueOfShortLines_MasksTheJoinedValue()
    {
        var redactor = new SecretRedactor();

        bool registered = redactor.Register("abcd\nefgh\nijkl");

        Assert.True(registered);
        Assert.Equal("x **** y", redactor.Redact("x abcdefghijkl y"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ShortLineBeforeALongLine_MasksTheJoinedValueEchoedWithoutTheNewline()
    {
        var redactor = new SecretRedactor();

        redactor.Register("ab\ncdefgh");

        Assert.Equal("x=****", redactor.Redact("x=abcdefgh"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithCrLfInside_MasksTheWholeValueAndEachLine()
    {
        var redactor = new SecretRedactor();

        redactor.Register("AAAAA1\r\nBBBBB2");

        Assert.Equal("****", redactor.Redact("AAAAA1\r\nBBBBB2"));
        Assert.Equal("****\r\n****", redactor.Redact("AAAAA1") + "\r\n" + redactor.Redact("BBBBB2"));
        Assert.Equal("****\r\n****", redactor.Redact("AAAAA1\r\n") + redactor.Redact("BBBBB2"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithALoneCarriageReturnInside_MasksEachPart()
    {
        var redactor = new SecretRedactor();

        bool registered = redactor.Register("AAAAA1\rBBBBB2");

        Assert.True(registered);
        Assert.Equal("a **** b", redactor.Redact("a AAAAA1 b"));
        Assert.Equal("a **** b", redactor.Redact("a BBBBB2 b"));
    }

    [Theory]
    [Trait("Spec", "REQ-029")]
    [InlineData(0x000D)]
    [InlineData(0x000A)]
    [InlineData(0x000B)]
    [InlineData(0x000C)]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void Register_KeysJoinedByALineTerminator_MaskEachKeyPrintedAlone(int terminator)
    {
        string first = BuildOpaqueKey();
        string second = BuildOpaqueKey();
        var redactor = new SecretRedactor();

        bool registered = redactor.Register(first + (char)terminator + second);

        Assert.True(registered);
        Assert.Equal("a **** b", redactor.Redact("a " + first + " b"));
        Assert.Equal("a **** b", redactor.Redact("a " + second + " b"));
    }

    [Theory]
    [Trait("Spec", "REQ-029")]
    [InlineData(0x000D)]
    [InlineData(0x000A)]
    [InlineData(0x000B)]
    [InlineData(0x000C)]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void Register_ShortPartsSeparatedByALineTerminator_MaskTheJoinedValue(int terminator)
    {
        var redactor = new SecretRedactor();

        bool registered = redactor.Register("abcd" + (char)terminator + "efgh");

        Assert.True(registered);
        Assert.Equal("x=****", redactor.Redact("x=abcdefgh"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueOfOnlyLineBreaks_ReturnsFalse()
    {
        var redactor = new SecretRedactor();

        Assert.False(redactor.Register("\r\n\n\r"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_TheSameValueTwice_ReturnsTrueBothTimes()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();

        Assert.True(redactor.Register(key));
        Assert.True(redactor.Register(key));
        Assert.Equal("k=****", redactor.Redact("k=" + key));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithSurroundingSpaces_KeepsTheNeighbouringWhitespaceInTheOutput()
    {
        string key = BuildOpaqueKey();
        var redactor = new SecretRedactor();

        redactor.Register("  " + key + "  ");

        Assert.Equal("a **** b", redactor.Redact("a " + key + " b"));
        Assert.Equal("a  ****  b", redactor.Redact("a  " + key + "  b"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithInteriorLoneSurrogate_MasksTheReplacementCharacterEcho()
    {
        string raw = "ab" + (char)0xD800 + "cdefghij";
        string echoed = "ab" + char.ConvertFromUtf32(0xFFFD) + "cdefghij";
        var redactor = new SecretRedactor();

        redactor.Register(raw);

        Assert.Equal("see **** end", redactor.Redact("see " + echoed + " end"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void RedactingTextWriter_MultiLineRegistration_MasksEachWrittenLine()
    {
        string first = BuildOpaqueKey();
        string second = BuildOpaqueKey();
        var redactor = new SecretRedactor();
        redactor.Register(first + "\n" + second);
        using var inner = new StringWriter();
        using var writer = new RedactingTextWriter(inner, redactor);

        writer.WriteLine(first);
        writer.WriteLine(second);

        string newLine = Environment.NewLine;
        Assert.Equal("****" + newLine + "****" + newLine, inner.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithInteriorLoneSurrogate_MasksTheRawEchoAndTheWellFormedForm()
    {
        string raw = "ab" + (char)0xD800 + "cdefghij";
        var redactor = new SecretRedactor();

        bool registered = redactor.Register(raw);

        Assert.True(registered);
        Assert.Equal("see **** end", redactor.Redact("see " + raw + " end"));
        Assert.Equal("see **** end", redactor.Redact("see abcdefghij end"));
    }

    [Fact]
    [Trait("Spec", "REQ-029")]
    public void Register_ValueWithTrailingLoneSurrogate_MasksTheRawEchoAndTheBareKey()
    {
        string key = BuildOpaqueKey();
        string raw = key + (char)0xDC00;
        var redactor = new SecretRedactor();

        bool registered = redactor.Register(raw);

        Assert.True(registered);
        Assert.Equal("k=****", redactor.Redact("k=" + raw));
        Assert.Equal("k=****", redactor.Redact("k=" + key));
    }

    [Theory]
    [Trait("Spec", "REQ-006")]
    [InlineData("probe --bogus")]
    [InlineData("probe --help")]
    [InlineData("--help")]
    [InlineData("")]
    [InlineData("fake-throw InvalidOperationException boom")]
    [InlineData("fake-console-error one-line")]
    public async Task Redacting_writer_forwards_only_whole_lines_of_every_cli_producer(string arguments)
    {
        var stdout = new SpyTextWriter();
        var stderr = new SpyTextWriter();

        // Debug level makes the logger and the stack trace produce their multi-line output as well.
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("Logging:LogLevel:Default", "Debug")]);

        _ = await CliHarness.RunAsync(arguments, overrides, stdout, stderr);

        Assert.True(stdout.Calls.Count + stderr.Calls.Count > 0, "the run wrote nothing, so no call boundary was observed");
        foreach (string call in stdout.Calls.Concat(stderr.Calls))
        {
            Assert.True(call.EndsWith('\n'), "a write call did not end with a newline: " + call);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Cli_only_the_final_drain_may_end_without_a_newline()
    {
        var stdout = new SpyTextWriter();
        var stderr = new SpyTextWriter();

        _ = await CliHarness.RunAsync("fake-write", stdout: stdout, stderr: stderr);

        Assert.Equal(FakeCommands.JsonDocument, Assert.Single(stdout.Calls));
        Assert.All(stderr.Calls, call => Assert.True(call.EndsWith('\n'), "a write call did not end with a newline: " + call));
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Console_wrappers_leave_the_inner_writer_open()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        CliRunResult result = await CliHarness.RunAsync("fake-write", stdout: stdout, stderr: stderr);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(Record.Exception(() => stdout.Write("after")));
        Assert.Null(Record.Exception(() => stderr.Write("after")));
        Assert.Equal(FakeCommands.JsonDocument + "after", stdout.ToString());
        Assert.Equal("after", stderr.ToString());
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Console_statics_are_restored_after_RunAsync()
    {
        CliRunResult result = await CliHarness.RunAsync("fake-write");

        Assert.True(result.ConsoleRestored);
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Console_error_written_directly_by_a_command_is_masked_by_the_installed_wrapper()
    {
        CliRunResult result = await CliHarness.RunAsync("fake-console-error leaked-" + BuildNvidiaKey());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("leaked-nvapi-****", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Spec", "REQ-006")]
    [InlineData("NVIDIA_API_KEY")]
    [InlineData("NEBIUS_API_KEY")]
    [InlineData("NGC_API_KEY")]
    public async Task Unregistrable_key_warns_by_variable_name(string variable)
    {
        // Four characters: every registrable form of the value is shorter than five characters.
        string value = string.Concat("k#", "9Q");
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [(variable, value)]);

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(0, result.ExitCode);
        string warning = Assert.Single(result.StderrLines, line => line.StartsWith("warning", StringComparison.Ordinal));
        Assert.Equal($"warning: {variable} is set but could not be registered for redaction", warning);
        Assert.DoesNotContain(value, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(value, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Registrable_and_empty_key_values_do_not_warn()
    {
        CliHostOverrides overrides = CliHarness.Overrides(
            configuration: [("NVIDIA_API_KEY", BuildOpaqueKey()), ("NEBIUS_API_KEY", string.Empty)]);

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(result.StderrLines, line => line.StartsWith("warning", StringComparison.Ordinal));
    }

    // The producers write to the spy directly, with no redacting writer in front of it: this is what proves whole lines. Behind the wrapper
    // (the theory above) the spy only sees what the wrapper forwards.
    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Cli_producers_write_whole_lines()
    {
        string escape = ((char)0x1B).ToString();
        string coloured = escape + "[31m" + "red";
        var spy = new SpyTextWriter();

        // The console logger: Console.Error is process-wide, so the harness gate is held while the spy replaces it.
        await CliHarness.RunExclusiveAsync(() =>
        {
            TextWriter before = Console.Error;
            Console.SetError(spy);
            try
            {
                using ILoggerFactory factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddAgentJoulesConsole());
                ILogger logger = factory.CreateLogger("Test");
                TestLog.Plain(logger, "one");
                TestLog.Coloured(logger, coloured);
                try
                {
                    throw new InvalidOperationException("with a stack trace");
                }
                catch (InvalidOperationException failure)
                {
                    TestLog.Failed(logger, failure, "two");
                }
            }
            finally
            {
                Console.SetError(before);
            }

            return Task.FromResult(0);
        });

        // One call per record, each ending with a newline, and no escape sequence in any of them.
        Assert.Equal(3, spy.Calls.Count);
        Assert.All(spy.Calls, call => Assert.EndsWith(Environment.NewLine, call, StringComparison.Ordinal));
        Assert.All(spy.Calls, call => Assert.DoesNotContain(escape, call, StringComparison.Ordinal));
        Assert.Contains("coloured ?[31mred record", spy.Calls[1], StringComparison.Ordinal);
        Assert.Contains("   at ", spy.Calls[2], StringComparison.Ordinal);

        // ErrorLine: one WriteLine, folded to one line, control characters scrubbed.
        var errors = new SpyTextWriter();
        ErrorLine.Write(errors, new InvalidOperationException("first" + escape + "[0m\r\nsecond\tthird"));
        Assert.Equal("error: first?[0m second third (InvalidOperationException)" + Environment.NewLine, Assert.Single(errors.Calls));

        // ConsoleReporter: one WriteLine per logical line.
        var reportOut = new SpyTextWriter();
        var reportError = new SpyTextWriter();
        var unstable = new BaselineResult(Metric.Measured(25.0, "W"), Metric.Measured(5.0, "W"), 30.0, 300, 0.20, 0.10, ReadingFlags.BaselineUnstable);
        new ConsoleReporter(reportOut, reportError).ReportBaseline(unstable);
        Assert.Empty(reportOut.Calls);
        Assert.EndsWith(Environment.NewLine, Assert.Single(reportError.Calls), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public void ErrorLine_masks_a_registered_key_before_scrubbing_so_a_control_character_inside_it_cannot_leak_it()
    {
        string key = BuildOpaqueKey();
        string decorated = key[..10] + char.ConvertFromUtf32(0x200B) + key[10..];
        var redactor = new SecretRedactor();
        redactor.Register(decorated);

        string line = ErrorLine.Format(new InvalidOperationException("echo " + decorated), redactor);

        Assert.Equal("error: echo **** (InvalidOperationException)", line);
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Registered_key_in_a_parse_error_is_masked_before_the_host_exists()
    {
        string key = BuildOpaqueKey();
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("NEBIUS_API_KEY", key)]);

        CliRunResult result = await CliHarness.RunAsync("probe --bogus " + key, overrides);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--bogus", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(key, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(key, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Registered_key_in_a_config_path_is_masked_in_the_not_found_message()
    {
        string key = BuildOpaqueKey();
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("NVIDIA_API_KEY", key)]);

        CliRunResult result = await CliHarness.RunAsync("probe --config " + key + ".json", overrides);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("****", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        Assert.DoesNotContain(key, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Spec", "REQ-006")]
    [InlineData("probe @{0}")]
    [InlineData("probe --suffix @{0}")]
    public async Task Response_file_contents_are_never_echoed(string template)
    {
        string secret = "FILE-" + BuildOpaqueKey();
        using TempWorkspace workspace = TempWorkspace.Create();
        string path = workspace.WriteFile("keys.env", secret);

        CliRunResult result = await CliHarness.RunAsync(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, template, "\"" + path + "\""),
            new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.DoesNotContain(secret, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public async Task Key_inside_an_exception_logged_at_Debug_is_masked_in_the_log_record()
    {
        string opaque = BuildOpaqueKey();
        CliHostOverrides overrides = CliHarness.Overrides(
            configuration: [("Logging:LogLevel:Default", "Debug"), ("NEBIUS_API_KEY", opaque)]);

        CliRunResult result = await CliHarness.RunAsync($"fake-throw InvalidOperationException failed-{BuildNvidiaKey()},{opaque}", overrides);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.StderrLines, line => line.StartsWith("dbug:", StringComparison.Ordinal));
        Assert.Contains("System.InvalidOperationException: failed-nvapi-****,****", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(opaque, result.Stderr, StringComparison.Ordinal);
    }

    // ---- AJ-052 (EDGE-011): the output writers with a runtime-built key, the 64-character echo cut, the JSON form, the configured variable names ----

    // A test-only command that writes its argument to both standard streams: to stdout in two writes without a newline between them (a key split across calls), to stderr as one line.
    private static Command EchoBothStreams()
    {
        var text = new Argument<string>("text");
        var command = new Command("echo-both") { text };
        command.SetAction(parseResult =>
        {
            string value = parseResult.GetRequiredValue(text);
            int middle = value.Length / 2;
            Console.Out.Write(value[..middle]);
            Console.Out.Write(value[middle..]);
            Console.Out.WriteLine();
            Console.Error.WriteLine("err " + value);
            return 0;
        });
        return command;
    }

    private static async Task<CliRunResult> EchoAsync(string text, params (string Key, string? Value)[] configuration)
    {
        CliHostOverrides overrides = CliHarness.Overrides(configuration) with { AdditionalCommands = [EchoBothStreams()] };
        return await CliHarness.RunTokensAsync(["echo-both", text], overrides).WaitAsync(HangGuard);
    }

    private static void AssertNoWindowOf(string secret, string output, int window)
    {
        for (int start = 0; start + window <= secret.Length; start++)
        {
            Assert.DoesNotContain(secret.Substring(start, window), output, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task Both_output_writers_keep_only_the_nvapi_prefix_of_a_runtime_built_key()
    {
        string suffix = BuildOpaqueKey();
        string key = string.Concat("nvapi-", suffix);

        CliRunResult result = await EchoAsync(key);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(MaskedNvidiaKey + Environment.NewLine, result.Stdout);
        Assert.Equal("err " + MaskedNvidiaKey, Assert.Single(result.StderrLines, line => line.Length > 0));
        foreach (string output in new[] { result.Stdout, result.Stderr })
        {
            Assert.DoesNotContain(suffix, output, StringComparison.Ordinal);
            AssertNoWindowOf(suffix, output, 5);
        }
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task Key_longer_than_64_characters_echoed_through_TextEcho_is_still_masked_in_both_streams()
    {
        string key = BuildOpaqueKey() + BuildOpaqueKey() + BuildOpaqueKey();
        string echoed = TextEcho.ForMessage(key);
        Assert.Equal(64 + 3, echoed.Length);
        Assert.StartsWith(key[..64], echoed, StringComparison.Ordinal);

        CliRunResult result = await EchoAsync(echoed, ("NEBIUS_API_KEY", key));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("****" + Environment.NewLine, result.Stdout);
        Assert.Equal("err ****", Assert.Single(result.StderrLines, line => line.Length > 0));
        AssertNoWindowOf(key, result.Stdout + result.Stderr, 6);
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task Key_longer_than_64_characters_echoed_inside_an_error_line_is_masked()
    {
        string key = BuildOpaqueKey() + BuildOpaqueKey() + BuildOpaqueKey();
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("NEBIUS_API_KEY", key), ("Logging:LogLevel:Default", "Debug")]);

        CliRunResult result = await CliHarness.RunTokensAsync(["fake-throw", "ConfigurationException", "bad value " + TextEcho.ForMessage(key)], overrides).WaitAsync(HangGuard);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("bad value ****", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        AssertNoWindowOf(key, result.Stdout + result.Stderr, 6);
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void Register_masks_the_cut_echo_of_a_long_value_wherever_the_cut_falls_and_nothing_that_was_not_cut()
    {
        string longKey = BuildOpaqueKey() + BuildOpaqueKey() + BuildOpaqueKey();
        string exactly64 = longKey[..64];
        var redactor = new SecretRedactor();

        // The cut forms end in the marker, so the mask replaces the marker with the text it cut.
        Assert.True(redactor.Register(longKey));
        Assert.Equal("****", redactor.Redact(TextEcho.ForMessage(longKey)));
        Assert.Equal("a **** b", redactor.Redact("a " + TextEcho.ForMessage(longKey) + " b"));

        // A cut that left only part of the key (the key came after other text) is masked by the form that ends in the cut marker.
        Assert.Equal(" ****", redactor.Redact(TextEcho.ForMessage(" " + longKey)));
        Assert.Equal("Bearer ****", redactor.Redact(TextEcho.ForMessage("Bearer " + longKey)));

        // A value of 64 characters or fewer is its own echo and needs no cut forms; a fragment that was not cut (no marker after it) is not masked.
        var shortRedactor = new SecretRedactor();
        Assert.True(shortRedactor.Register(exactly64));
        Assert.Equal("****", shortRedactor.Redact(TextEcho.ForMessage(exactly64)));
        Assert.Equal(longKey[..40], redactor.Redact(longKey[..40]));
        Assert.Equal("****", redactor.Redact(longKey));
    }

    [Theory]
    [Trait("Spec", "EDGE-011")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Bearer ")]
    [InlineData("Authorization: Bearer ")]
    public void A_120_character_key_echoed_after_a_prefix_leaves_no_8_character_window_of_it(string prefix)
    {
        string key = (BuildOpaqueKey() + BuildOpaqueKey() + BuildOpaqueKey() + BuildOpaqueKey())[..120];
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));

        string echoed = TextEcho.ForMessage(prefix + key);
        Assert.EndsWith("...", echoed, StringComparison.Ordinal);
        string redacted = redactor.Redact(echoed);

        Assert.StartsWith(prefix, redacted, StringComparison.Ordinal);
        AssertNoWindowOf(key, redacted, 8);
        Assert.Contains("****", redacted, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void A_cut_that_leaves_fewer_than_8_characters_of_the_key_is_the_documented_limit()
    {
        // 60 characters of other text leave 4 characters of the key in the echo; they are not masked (at most 7 can remain), and the full key still is.
        string key = (BuildOpaqueKey() + BuildOpaqueKey() + BuildOpaqueKey())[..90];
        string text = new string('x', 60) + key;
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));

        string redacted = redactor.Redact(TextEcho.ForMessage(text));

        Assert.EndsWith(key[..4] + "...", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(key[..8], redacted, StringComparison.Ordinal);
        Assert.Equal("****", redactor.Redact(key));
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void Register_covers_the_cut_that_stops_one_character_early_in_front_of_a_surrogate_pair()
    {
        // The pair starts at index 63, so TextEcho cuts at 63 instead of splitting it; the registered prefix has to match that shorter cut as well.
        string key = BuildOpaqueKey() + BuildOpaqueKey()[..31] + char.ConvertFromUtf32(0x1F600) + BuildOpaqueKey();
        Assert.True(char.IsHighSurrogate(key[63]));
        var redactor = new SecretRedactor();

        Assert.True(redactor.Register(key));

        string echoed = TextEcho.ForMessage(key);
        Assert.Equal(63 + 3, echoed.Length);
        Assert.Equal("****", redactor.Redact(echoed));
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void Register_adds_the_prefix_of_the_json_encoded_form_too()
    {
        // The encoded form of a key with a plus sign is longer than the key; the prefix of the long form is registered, and the encoded prefix with it.
        string key = BuildOpaqueKey() + "+" + BuildOpaqueKey() + "<" + BuildOpaqueKey();
        var redactor = new SecretRedactor();

        Assert.True(redactor.Register(key));

        string encoded = System.Text.Json.JsonSerializer.Serialize(key);
        Assert.Equal("\"****\"", redactor.Redact(encoded));
        Assert.Equal("****", redactor.Redact(TextEcho.ForMessage(key)));
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task Json_encoded_form_of_a_key_is_masked_in_both_streams()
    {
        string key = "k" + BuildOpaqueKey() + "+" + BuildOpaqueKey()[..12] + "<";
        string encoded = System.Text.Json.JsonSerializer.Serialize(key);
        Assert.NotEqual("\"" + key + "\"", encoded);

        CliRunResult result = await EchoAsync(encoded, ("NEBIUS_API_KEY", key));

        Assert.Equal("\"****\"" + Environment.NewLine, result.Stdout);
        Assert.Equal("err \"****\"", Assert.Single(result.StderrLines, line => line.Length > 0));
        AssertNoWindowOf(key, result.Stdout + result.Stderr, 6);
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task Key_under_a_configured_ApiKeyVariable_name_is_masked_in_a_parse_error_before_the_host_exists()
    {
        // The parse error is printed after RegisterEarly and before any host is built, so only the early pass over the file-free sources can have registered this value.
        string key = BuildOpaqueKey();

        CliRunResult result = await CliHarness.RunTokensAsync(
            ["--bogus-" + key],
            CliHarness.Overrides(configuration: [("AgentJoules:Tiers:nvidia:ApiKeyVariable", "OTHER_API_KEY"), ("OTHER_API_KEY", key)])).WaitAsync(HangGuard);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--bogus-****", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(key, result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task Key_under_a_variable_outside_the_allowlist_is_not_registered_so_a_parse_error_echoes_it()
    {
        // GITHUB_TOKEN does not end in _API_KEY, so the registry never looks it up: the value is echoed by the parse error exactly as an unrelated word would be.
        // (The same configuration is refused at start once a host is built; the parse error comes first.)
        string value = BuildOpaqueKey();

        CliRunResult result = await CliHarness.RunTokensAsync(
            ["--bogus-" + value],
            CliHarness.Overrides(configuration: [("AgentJoules:Tiers:nvidia:ApiKeyVariable", "GITHUB_TOKEN"), ("GITHUB_TOKEN", value)])).WaitAsync(HangGuard);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--bogus-" + value, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task Key_under_a_variable_name_only_the_json_file_declares_is_masked_once_the_host_is_built()
    {
        string key = BuildOpaqueKey();
        using TempWorkspace workspace = TempWorkspace.Create();
        string json = File.ReadAllText(workspace.ConfigPath);
        string renamed = json.Replace("\"ApiKeyVariable\": \"NVIDIA_API_KEY\"", "\"ApiKeyVariable\": \"OTHER_API_KEY\"", StringComparison.Ordinal);
        Assert.NotEqual(json, renamed);
        workspace.WriteConfig(renamed);

        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("OTHER_API_KEY", key)]) with
        {
            AdditionalCommands = [EchoBothStreams()],
            WorkingDirectory = workspace.Root,
        };
        CliRunResult result = await CliHarness.RunTokensAsync(["echo-both", key], overrides).WaitAsync(HangGuard);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("****" + Environment.NewLine, result.Stdout);
        Assert.Equal("err ****", Assert.Single(result.StderrLines, line => line.Length > 0));
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public async Task Configured_key_variable_outside_the_allowlist_is_refused_before_the_command_runs_without_echoing_it()
    {
        string other = BuildOpaqueKey();

        CliRunResult result = await EchoAsync(other, ("AgentJoules:Tiers:nvidia:ApiKeyVariable", "GITHUB_TOKEN"), ("GITHUB_TOKEN", other));

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.DoesNotContain("GITHUB_TOKEN", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(other, result.Stderr, StringComparison.Ordinal);
    }

    // ---- the early pass registers the file-free sources together (item 5 of the AJ-052 fix round) ----

    private static Action<IConfigurationBuilder> MemorySource(params (string Key, string Value)[] pairs) =>
        builder => builder.AddInMemoryCollection(pairs.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void Early_pass_registers_a_value_whose_variable_name_sits_in_another_file_free_source()
    {
        string key = BuildOpaqueKey();
        var registry = new KeyRegistry(new SecretRedactor());

        registry.RegisterEarly(
            [MemorySource(("AgentJoules:Tiers:nvidia:ApiKeyVariable", "OTHER_API_KEY")), MemorySource(("OTHER_API_KEY", key))],
            TextWriter.Null);

        Assert.Equal("****", registry.Redactor.Redact(key));
    }

    [Fact]
    [Trait("Spec", "EDGE-011")]
    public void Early_pass_skips_a_source_that_cannot_be_read_and_still_registers_the_others()
    {
        string key = BuildOpaqueKey();
        var registry = new KeyRegistry(new SecretRedactor());

        registry.RegisterEarly(
            [builder => throw new InvalidDataException("a malformed secrets file"), MemorySource(("NEBIUS_API_KEY", key))],
            TextWriter.Null);

        Assert.Equal("****", registry.Redactor.Redact(key));
    }
}