using AgentJoules.Core.Labels;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>AJ-048 (REQ-012): <see cref="TextEcho"/> is public, with the 64 character cut as its default and an optional shorter or longer one.</summary>
[Trait("Spec", "REQ-012")]
public sealed class TextEchoTests
{
    [Fact]
    public void The_class_is_public_so_other_assemblies_can_echo_document_text()
    {
        Assert.True(typeof(TextEcho).IsPublic);
        Assert.True(typeof(TextEcho).IsAbstract && typeof(TextEcho).IsSealed);
    }

    [Fact]
    public void The_default_cut_is_64_characters()
    {
        string echoed = TextEcho.ForMessage(new string('x', 100));

        Assert.Equal(new string('x', 64) + "...", echoed);
        Assert.Equal(new string('y', 64), TextEcho.ForMessage(new string('y', 64)));
    }

    [Fact]
    public void An_explicit_length_cuts_there()
    {
        Assert.Equal("abcde...", TextEcho.ForMessage("abcdefghij", 5));
        Assert.Equal("abcde", TextEcho.ForMessage("abcde", 5));
        Assert.Equal(new string('x', 200), TextEcho.ForMessage(new string('x', 200), 200));
        Assert.Equal(new string('x', 200) + "...", TextEcho.ForMessage(new string('x', 201), 200));
    }

    [Fact]
    public void A_cut_never_leaves_a_lone_high_surrogate()
    {
        string pair = char.ConvertFromUtf32(0x1F600);
        string text = "ab" + pair + "cd";

        string echoed = TextEcho.ForMessage(text, 3);

        Assert.Equal("ab...", echoed);
        Assert.DoesNotContain(echoed, char.IsSurrogate);
    }

    [Fact]
    public void Control_characters_are_still_replaced_with_a_custom_length()
    {
        Assert.Equal("a?b?c", TextEcho.ForMessage("a\nb\tc", 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_length_below_one_is_refused(int maxLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextEcho.ForMessage("x", maxLength));
    }

    [Fact]
    public void Null_text_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => TextEcho.ForMessage(null!));
    }
}