using System.Text.Json.Nodes;

using AgentJoules.Core.Runs;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>
/// The setup of the Ollama scenarios (AJ-104, EDGE-009): the local tier at a loopback fake that answers the way Ollama does (<c>/api/version</c> and a <c>/v1/models</c> list of tags), the runtime
/// switched to <c>ollama</c> in the settings (the shipped configuration selects <c>nim</c>), the judge tier with its fake key and its own fake endpoint, and a scripted GPU, so
/// <c>run --tier local --dry-run</c> reaches the Ollama check with no network, no real key and no real GPU. Nothing here reads the process environment or calls a live endpoint.
/// </summary>
public static class OllamaFixtures
{
    /// <summary>The tag the story names (design 3.4, AJ-104 criterion 2); a configured constant of the scenarios, never of the code under test.</summary>
    public const string StoryTag = "nemotron-3-nano:30b";

    /// <summary>Another tag, for the scenario that proves the pull command names the configured tag.</summary>
    public const string OtherTag = "qwen3:8b";

    private const string V1Segment = "/v1";

    /// <summary>The base URL of the local tier of the scenarios: a fake https host that no resolver knows (the validator accepts plain http only for a loopback host, and the Ollama address is the live one) (the primary handler is a fake, so nothing is looked up), never the address of a live Ollama.</summary>
    /// <returns>The base URL, ending in <c>/v1</c>.</returns>
    public static string LocalBaseUrl() => Uri.UriSchemeHttps + Uri.SchemeDelimiter + "local.fake.test" + V1Segment;

    /// <summary>The native base of <see cref="LocalBaseUrl"/> (no <c>/v1</c> segment), without a trailing slash, derived from it.</summary>
    /// <returns>The native base.</returns>
    public static string NativeBaseUrl() => LocalBaseUrl()[..^V1Segment.Length];

    /// <summary>One entry of the <c>/api/ps</c> list in the documented shape (AJ-105): <c>name</c>, <c>model</c>, <c>size</c>, <c>size_vram</c>, <c>digest</c>, <c>details</c> and <c>expires_at</c>.</summary>
    /// <param name="name">The tag; both <c>name</c> and <c>model</c> carry it.</param>
    /// <param name="size">The <c>size</c> in bytes, or null to leave the member out.</param>
    /// <param name="sizeVram">The <c>size_vram</c> in bytes, or null to leave the member out.</param>
    /// <returns>The JSON text of the entry.</returns>
    public static string PsEntry(string name, long? size, long? sizeVram)
    {
        var entry = new JsonObject { ["name"] = name, ["model"] = name };
        if (size is { } total)
        {
            entry["size"] = total;
        }

        if (sizeVram is { } vram)
        {
            entry["size_vram"] = vram;
        }

        entry["digest"] = "0000000000000000000000000000000000000000000000000000000000000000";
        entry["details"] = new JsonObject { ["format"] = "gguf", ["family"] = "nemotron_h", ["quantization_level"] = "Q4_K_M" };
        entry["expires_at"] = "2026-10-24T20:30:00Z";
        return entry.ToJsonString();
    }

    /// <summary>The answer of <c>/api/ps</c>: <c>{"models":[...]}</c>, the measured <c>{"models":[]}</c> when no entry is given.</summary>
    /// <param name="entries">The entries, each built by <see cref="PsEntry(string, long?, long?)"/>.</param>
    /// <returns>The JSON text.</returns>
    public static string Ps(params string[] entries)
    {
        var models = new JsonArray();
        foreach (string entry in entries)
        {
            models.Add(JsonNode.Parse(entry));
        }

        return new JsonObject { ["models"] = models }.ToJsonString();
    }

    /// <summary>The answer of <c>POST /api/show</c> in the measured shape of Ollama 0.40.10: the <c>details</c> object (format, family, parameter size, quantization level, runner) beside the other top-level members.</summary>
    /// <param name="quantizationLevel">The <c>details.quantization_level</c>, or null to leave the member out.</param>
    /// <returns>The JSON text.</returns>
    public static string Show(string? quantizationLevel)
    {
        var details = new JsonObject { ["parent_model"] = string.Empty, ["format"] = "gguf", ["family"] = "nemotron_h", ["families"] = new JsonArray("nemotron_h"), ["parameter_size"] = "4.0B" };
        if (quantizationLevel is not null)
        {
            details["quantization_level"] = quantizationLevel;
        }

        details["runner"] = "llamacpp";
        return new JsonObject
        {
            ["license"] = string.Empty,
            ["modelfile"] = string.Empty,
            ["parameters"] = string.Empty,
            ["template"] = string.Empty,
            ["details"] = details,
            ["model_info"] = new JsonObject(),
            ["capabilities"] = new JsonArray("completion"),
            ["modified_at"] = "2026-10-01T00:00:00Z",
        }.ToJsonString();
    }

    /// <summary>A fake that answers <c>/api/version</c> and lists <paramref name="tags"/> on <c>/v1/models</c> the way Ollama does.</summary>
    /// <param name="tags">The tags the models route lists.</param>
    /// <returns>The recording fake.</returns>
    public static FakeHttpMessageHandler Endpoint(params string[] tags) => new() { ModelIds = tags, OllamaStyleModels = true };

    /// <summary>
    /// The setup of one run of <c>run --tier local --dry-run</c> against <paramref name="local"/>: the runtime, the local base URL, the model tag, the fake judge key and endpoint, and a scripted GPU.
    /// </summary>
    /// <param name="local">The fake that stands where Ollama stands.</param>
    /// <param name="modelTag">The configured <c>Tiers:local:ModelId</c>; <see cref="StoryTag"/> when null.</param>
    /// <param name="runtime">The configured <c>Local:Runtime</c>.</param>
    /// <param name="timeoutSeconds">The configured <c>Tiers:local:TimeoutSeconds</c>, or null for the shipped default.</param>
    /// <param name="agentModelIds">Per-agent tags (<c>Tiers:local:AgentModelIds</c>), or null for none.</param>
    /// <returns>The setup; the caller runs it through <see cref="RunCommandHarness"/>.</returns>
    public static RunCommandSetup Setup(
        FakeHttpMessageHandler local,
        string? modelTag = null,
        string runtime = "ollama",
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, string>? agentModelIds = null)
    {
        ArgumentNullException.ThrowIfNull(local);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["NVIDIA_API_KEY"] = ProviderClientFixtures.FakeKey(),
            ["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nvidia),
            ["AgentJoules:Tiers:nebius:BaseUrl"] = ProviderClientFixtures.BaseUrl(Tier.Nebius),
            ["AgentJoules:Local:Runtime"] = runtime,
            ["AgentJoules:Tiers:local:BaseUrl"] = LocalBaseUrl(),
            ["AgentJoules:Tiers:local:ModelId"] = modelTag ?? StoryTag,
        };

        if (timeoutSeconds is { } seconds)
        {
            settings["AgentJoules:Tiers:local:TimeoutSeconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach ((string agent, string tag) in agentModelIds ?? new Dictionary<string, string>())
        {
            settings["AgentJoules:Tiers:local:AgentModelIds:" + agent] = tag;
        }

        // The judge tier (nvidia) lists the judge model of the shipped configuration, so a dry run passes its key and model checks.
        FakeHttpMessageHandler judge = new() { ModelIds = [ConfigurationFixtures.ShippedOptions().Judge.ModelId ?? throw new InvalidOperationException("The shipped configuration holds no judge model ID")] };
        return RunCommandSetup.Create(
            settings,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["local"] = local, ["nvidia"] = judge },
            new ScriptedNvmlApi(new FakeTimeProvider()));
    }
}