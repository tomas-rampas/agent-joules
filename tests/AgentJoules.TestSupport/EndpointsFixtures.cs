using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;

namespace AgentJoules.TestSupport;

/// <summary>
/// Builders for the AJ-100 tests (REQ-018): a small, valid configuration file that names three tiers and a judge, and an endpoints document that lists exactly the model IDs of that configuration. A test edits the
/// configuration node or the sections before it writes them into a <see cref="TempWorkspace"/>, so the repository files are never read or written. Base URLs come from <see cref="ProviderClientFixtures.BaseUrl"/>.
/// </summary>
public static class EndpointsFixtures
{
    /// <summary>The date the default sections carry, as written in the document.</summary>
    public const string VerifiedDate = "2026-10-20";

    /// <summary>The path of the endpoints document in a workspace, the default of <c>Paths:Endpoints</c>.</summary>
    public static readonly string DocumentPath = Path.Combine("docs", "endpoints.md");

    /// <summary>The tiers of the default configuration, in the order of a run; <c>cloudgpu</c> is not configured.</summary>
    public static readonly string[] DefaultTiers = ["local", "nvidia", "nebius"];

    /// <summary>The model ID that <see cref="DefaultConfig"/> gives <paramref name="tier"/>.</summary>
    /// <param name="tier">A lowercase tier name.</param>
    /// <returns>The model ID, <c>model-&lt;tier&gt;</c>.</returns>
    public static string ModelOf(string tier) => "model-" + tier;

    /// <summary>The default configuration: <c>local</c>, <c>nvidia</c> and <c>nebius</c> enabled with the model <c>model-&lt;tier&gt;</c>, and the judge <c>model-nvidia</c> on the tier <c>nvidia</c>.</summary>
    /// <returns>The root node (<c>AgentJoules</c> section below it); the caller edits it.</returns>
    public static JsonObject DefaultConfig()
    {
        var tiers = new JsonObject();
        foreach (string tier in DefaultTiers)
        {
            tiers[tier] = TierEntry(tier, ModelOf(tier), enabled: true);
        }

        return new JsonObject
        {
            [AgentJoulesOptions.SectionName] = new JsonObject
            {
                ["Tiers"] = tiers,
                ["Judge"] = new JsonObject { ["Tier"] = "nvidia", ["ModelId"] = ModelOf("nvidia") },
            },
        };
    }

    /// <summary>One tier entry of the configuration.</summary>
    /// <param name="tier">The lowercase tier name; it picks the base URL and the key variable.</param>
    /// <param name="modelId">The model ID.</param>
    /// <param name="enabled">The <c>Enabled</c> flag.</param>
    /// <returns>The entry.</returns>
    public static JsonObject TierEntry(string tier, string modelId, bool enabled)
    {
        var entry = new JsonObject
        {
            ["BaseUrl"] = ProviderClientFixtures.BaseUrl(Parse(tier)),
            ["ModelId"] = modelId,
            ["Enabled"] = enabled,
        };
        if (tier == "nvidia")
        {
            entry["ApiKeyVariable"] = ProviderClientFixtures.NvidiaKeyVariable;
        }
        else if (tier == "nebius")
        {
            entry["ApiKeyVariable"] = ProviderClientFixtures.NebiusKeyVariable;
        }

        return entry;
    }

    /// <summary>The <c>AgentJoules</c> section of a configuration node made by <see cref="DefaultConfig"/>.</summary>
    /// <param name="root">The root node.</param>
    /// <returns>The section.</returns>
    public static JsonObject SectionOf(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root[AgentJoulesOptions.SectionName]!.AsObject();
    }

    /// <summary>The <c>Tiers</c> entry of <paramref name="tier"/> of a configuration node made by <see cref="DefaultConfig"/>.</summary>
    /// <param name="root">The root node.</param>
    /// <param name="tier">The lowercase tier name.</param>
    /// <returns>The entry.</returns>
    public static JsonObject TierOf(JsonObject root, string tier) => SectionOf(root)["Tiers"]![tier]!.AsObject();

    /// <summary>The text of a configuration node, indented, with a final line feed.</summary>
    /// <param name="root">The root node.</param>
    /// <returns>The JSON text.</returns>
    public static string Text(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>One section of the endpoints document: the heading, the optional <c>Verified:</c> line and one <c>- model:</c> line per ID.</summary>
    /// <param name="tier">The tier name of the heading.</param>
    /// <param name="verified">The text after <c>Verified: </c>, or null for no such line.</param>
    /// <param name="modelIds">The IDs of the model lines.</param>
    /// <returns>The lines, each ended by a line feed.</returns>
    public static string Section(string tier, string? verified, params string[] modelIds)
    {
        var text = new StringBuilder().Append("## ").Append(tier).Append('\n');
        if (verified is not null)
        {
            _ = text.Append("Verified: ").Append(verified).Append('\n');
        }

        foreach (string modelId in modelIds)
        {
            _ = text.Append("- model: ").Append(modelId).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The default sections: <c>local</c>, <c>nvidia</c> and <c>nebius</c>, each verified and listing the model of the default configuration.</summary>
    /// <returns>The three sections in file order.</returns>
    public static string[] DefaultSections() => [.. DefaultTiers.Select(static tier => Section(tier, VerifiedDate, ModelOf(tier)))];

    /// <summary>An endpoints document: an intro paragraph, then the sections.</summary>
    /// <param name="sections">The sections, as made by <see cref="Section"/>.</param>
    /// <returns>The text with LF line ends and a final line feed.</returns>
    public static string Document(params string[] sections) =>
        "# Endpoints\n\nThis file records the endpoints and model IDs.\n\n" + string.Concat(sections.Select(static section => section + "\n"));

    /// <summary>The default document: <see cref="DefaultSections"/> in an intro.</summary>
    /// <returns>The text.</returns>
    public static string DefaultDocument() => Document(DefaultSections());

    private static Tier Parse(string tier) => tier switch
    {
        "local" => Tier.Local,
        "cloudgpu" => Tier.CloudGpu,
        "nvidia" => Tier.Nvidia,
        "nebius" => Tier.Nebius,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a tier name."),
    };
}