using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-031")]
public sealed class KeyHygieneCheckTests : IDisposable
{
    private const string FakeBody = "FAKE-KEY-BODY-0123456789";

    private readonly string _root = Directory.CreateTempSubdirectory("keyhygiene-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_clean_directory_passes()
    {
        Write("README.md");
        Write(Path.Combine("src", "Program.cs"));

        KeyHygieneResult result = KeyHygieneCheck.Run(_root);

        Assert.True(result.Passed);
        Assert.Empty(result.Offenders);
    }

    [Fact]
    public void A_private_key_fails_and_is_named_without_its_body()
    {
        Write("id_ed25519", FakeBody);

        KeyHygieneResult result = KeyHygieneCheck.Run(_root);

        Assert.False(result.Passed);
        Assert.Equal(["id_ed25519"], result.Offenders);
        Assert.Contains("id_ed25519", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeBody, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pem_rsa_and_public_key_names_are_caught_in_subfolders()
    {
        Write(Path.Combine("deploy", "key.pem"));
        Write("id_rsa.pub");
        Write(Path.Combine("a", "b", "ID_RSA"));

        KeyHygieneResult result = KeyHygieneCheck.Run(_root);

        Assert.False(result.Passed);
        Assert.Equal(["a/b/ID_RSA", "deploy/key.pem", "id_rsa.pub"], result.Offenders);
    }

    [GitFact]
    public void The_real_repository_tracks_no_key_file()
    {
        KeyHygieneResult result = KeyHygieneCheck.RunTracked(ConfigurationFixtures.RepositoryRoot());

        Assert.True(result.Passed, result.Message);
    }

    private void Write(string relative, string body = "x")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
    }
}