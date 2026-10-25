namespace AgentJoules.Core.Configuration;

/// <summary>The <c>Local</c> section of <c>AgentJoules</c> (REQ-019, design 3.8): which runtime serves the local tier.</summary>
/// <remarks>
/// A mutable class with plain get and set accessors so that the configuration binding generator binds it; <c>Local:Runtime</c> is checked by <see cref="CrossFieldValidator"/>, and its message names the key and never the value.
/// The value is the decision line of <c>docs/nim-local-attempt.md</c> (AJ-103); the Ollama runtime (AJ-104) and the NIM runtime (AJ-105) select by it. With <c>nim</c>, <c>Tiers:local:Precision</c> must be set (<see cref="CrossFieldValidator"/>).
/// </remarks>
public sealed class LocalOptions
{
    /// <summary>The value of <see cref="Runtime"/> that selects Ollama (the default).</summary>
    public const string Ollama = "ollama";

    /// <summary>The value of <see cref="Runtime"/> that selects a NIM container.</summary>
    public const string Nim = "nim";

    /// <summary>The accepted values of <see cref="Runtime"/>, compared ordinally.</summary>
    public static readonly IReadOnlyList<string> Runtimes = [Ollama, Nim];

    /// <summary>Key <c>Local:Runtime</c>, default <c>ollama</c>: <c>ollama</c> or <c>nim</c>.</summary>
    public string Runtime { get; set; } = Ollama;
}