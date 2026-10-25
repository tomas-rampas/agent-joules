using System.Text.RegularExpressions;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Local;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// The checks that run before any call (design 3.7, REQ-017, EDGE-011, EDGE-008, EDGE-001): the key check (AJ-052), the model check (AJ-053) and the measured-tier check (AJ-054,
/// <see cref="CheckMeasuredTiers"/>). The key check reads the key variables from <see cref="IConfiguration"/> and never from the process environment, so a test that replaces the configuration controls every
/// key, and it reads only whether a value is present: no value is copied into a message, a field or a result.
/// </summary>
/// <param name="configuration">The configuration that holds the key variables (the process environment, user secrets or an in-memory override, as the host composed it).</param>
/// <param name="options">The bound and validated options.</param>
/// <param name="catalog">The model catalog client that asks the models route of a tier.</param>
/// <param name="redactor">The redactor that masks key values in the model IDs the check compares and reports.</param>
/// <param name="localRuntime">The runtime behind the local tier (EDGE-009): the model check asks <see cref="ILocalRuntime.CheckAvailableAsync"/> for the local tier before it asks the models route. A runtime with nothing to check (<see cref="NoOpLocalRuntime"/>) is a runtime all the same: the service requires one.</param>
public sealed partial class PreflightService(IConfiguration configuration, IOptions<AgentJoulesOptions> options, ModelCatalogClient catalog, SecretRedactor redactor, ILocalRuntime localRuntime)
{
    // The command that fetches an Ollama model; the tag is the configured model ID, never a constant.
    private const string OllamaPullCommand = "ollama pull ";

    /// <summary>The variable that names the SSH host of the <c>cloudgpu</c> VM; required on the laptop (<c>CloudGpu:RequireSshVariables</c> true).</summary>
    public const string SshHostVariable = "CLOUDGPU_SSH_HOST";

    /// <summary>The variable that names the SSH user of the <c>cloudgpu</c> VM; required on the laptop (<c>CloudGpu:RequireSshVariables</c> true).</summary>
    public const string SshUserVariable = "CLOUDGPU_SSH_USER";

    private const string AllTiers = "all";

    // The MIG mode of GpuIdentity when MIG is on (the other values are disabled and not-supported).
    private const string MigEnabled = "enabled";

    // The selection of Meter:Method "auto": the counter when the GPU has one, sampling otherwise.
    private const string AutoMethod = "auto";

    // The GPU port's reason names the library and the exception type (for example NVML library not found: libnvidia-ml.so.1 (EntryPointNotFoundException)); both must survive.
    private const int MaxReasonLength = 128;

    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly IOptions<AgentJoulesOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ModelCatalogClient _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly SecretRedactor _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    private readonly ILocalRuntime _localRuntime = localRuntime ?? throw new ArgumentNullException(nameof(localRuntime));

    /// <summary>
    /// Resolves the <c>--tier</c> value: <c>all</c> is every enabled tier in the fixed order of a run (<c>local</c>, <c>cloudgpu</c>, <c>nvidia</c>, <c>nebius</c>); a tier name is that
    /// one tier, enabled or not (an explicit <c>--tier</c> runs a disabled tier). The value is matched without regard to case.
    /// </summary>
    /// <param name="selection">A tier name or <c>all</c>.</param>
    /// <returns>The selected tiers in the order of a run; never empty.</returns>
    /// <exception cref="ConfigurationException">The value is neither a tier name nor <c>all</c>, or <c>all</c> finds no enabled tier; the message never shows the value.</exception>
    public IReadOnlyList<Tier> SelectTiers(string selection)
    {
        if (string.Equals(selection, AllTiers, StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, TierOptions> configured = _options.Value.Tiers;
            Tier[] enabled =
            [
                .. Enum.GetValues<Tier>()
                    .Where(tier => configured.TryGetValue(TierOptions.NameOf(tier), out TierOptions? entry) && entry is { Enabled: true }),
            ];

            return enabled.Length > 0 ? enabled : throw new ConfigurationException("--tier all finds no enabled tier; enable one under Tiers:<tier>:Enabled");
        }

        foreach (Tier tier in Enum.GetValues<Tier>())
        {
            if (string.Equals(selection, TierOptions.NameOf(tier), StringComparison.OrdinalIgnoreCase))
            {
                return [tier];
            }
        }

        throw new ConfigurationException($"--tier must be {string.Join(", ", TierOptions.KnownNames)} or {AllTiers}; the value is not shown");
    }

    /// <summary>
    /// Checks that every variable the selected tiers need is set, before any request (EDGE-011): the <c>ApiKeyVariable</c> of each selected tier that names one, for <c>cloudgpu</c> the
    /// two SSH variables when <c>CloudGpu:RequireSshVariables</c> is true, and the judge key (the <c>ApiKeyVariable</c> of the <c>Judge:Tier</c> tier). A variable that is absent, empty
    /// or white space is missing. A missing judge key is part of the failure in a dry run and only a result outside it, which the caller turns into a warning.
    /// </summary>
    /// <param name="tiers">The selected tiers, from <see cref="SelectTiers(string)"/>.</param>
    /// <param name="dryRun">True for <c>--dry-run</c>.</param>
    /// <returns>The result when nothing required is missing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> is null.</exception>
    /// <exception cref="MissingKeyException">Something required is missing: one exception, one message, every missing variable once in the order the check met them (exit code 3).</exception>
    public KeyCheckResult CheckKeys(IReadOnlyList<Tier> tiers, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        AgentJoulesOptions current = _options.Value;
        List<string> missing = [];

        foreach (Tier tier in tiers)
        {
            AddIfMissing(missing, KeyVariableOf(current, TierOptions.NameOf(tier)));

            if (tier == Tier.CloudGpu && current.CloudGpu.RequireSshVariables)
            {
                AddIfMissing(missing, SshHostVariable);
                AddIfMissing(missing, SshUserVariable);
            }
        }

        string? judgeVariable = KeyVariableOf(current, current.Judge.Tier);
        bool judgeMissing = judgeVariable is not null && IsMissing(judgeVariable);
        if (judgeMissing && dryRun)
        {
            AddIfMissing(missing, judgeVariable);
        }

        return missing.Count > 0
            ? throw new MissingKeyException(MissingKeyMessage.Format(missing), missing)
            : new KeyCheckResult(judgeMissing && !dryRun ? judgeVariable : null);
    }

    /// <summary>
    /// Checks that every model ID a run would call is listed by its endpoint, before any completion call (EDGE-008): for each selected tier, in order, the models route is asked once and
    /// the <c>ModelId</c> and every <c>AgentModelIds</c> value of the tier (each once) must be in the answer; then the <c>Judge:ModelId</c> must be in the list of the <c>Judge:Tier</c> tier
    /// (the list of a tier that was already asked is reused, so no tier is asked twice). The judge check is skipped when <paramref name="keys"/> says the judge key is missing outside a dry
    /// run, because that case already warns and the request would only produce a 401 that hides the cause, and when <c>Judge:ModelId</c> is not set.
    /// </summary>
    /// <remarks>
    /// A configured ID matches only when the endpoint listed exactly that text (<see cref="ModelCatalog.Lists(string)"/>, ordinal, as both were written), so IDs that merely look alike after the
    /// 64-character cut or the scrubbing of control characters are different IDs. Only the text that is reported (the missing IDs, the matched IDs, the available IDs) is echoed
    /// (<see cref="ModelIdListFormatter.Echo(SecretRedactor, string)"/>: masked, cut at 64 characters, control characters replaced). The check
    /// stops at the first tier with a missing ID or an empty list; the exception names every missing ID of that tier.
    /// </remarks>
    /// <param name="tiers">The selected tiers, from <see cref="SelectTiers(string)"/>.</param>
    /// <param name="keys">The result of <see cref="CheckKeys(IReadOnlyList{Tier}, bool)"/>: a caller that holds one has run the key check, and it says whether the judge key is missing.</param>
    /// <param name="checkJudge">False leaves the judge model out of the check (the calibration of AJ-109 scores nothing, so it needs neither the judge key nor the hosted judge endpoint); true checks it as above.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The tiers that were asked (the selected ones in order, then the judge tier when it was not selected) with their endpoints and the configured IDs that were listed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> or <paramref name="keys"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tiers"/> is empty.</exception>
    /// <exception cref="ConfigurationException">A tier is not configured, has neither a <c>ModelId</c> nor an <c>AgentModelIds</c> value, or <c>Judge:Tier</c> is not a tier name; exit code 2.</exception>
    /// <exception cref="ModelNotFoundException">A configured ID is not listed, or a list is empty (exit code 4).</exception>
    /// <exception cref="EndpointUnreachableException">A models endpoint is unreachable, answers a status other than success or answers something that is not a model list, or the local runtime does not answer its availability check (exit code 5).</exception>
    /// <exception cref="MissingKeyException">A tier names a key variable that has no value (exit code 3); the key check normally found it first.</exception>
    public async Task<ModelCheckResult> CheckModelsAsync(IReadOnlyList<Tier> tiers, KeyCheckResult keys, bool checkJudge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(keys);
        if (tiers.Count == 0)
        {
            throw new ArgumentException("At least one tier is needed: a result names the tiers that were asked.", nameof(tiers));
        }

        AgentJoulesOptions current = _options.Value;
        List<ListedTier> asked = [];

        foreach (Tier tier in tiers)
        {
            if (asked.Any(listed => listed.Tier == tier))
            {
                continue;
            }

            string name = TierOptions.NameOf(tier);
            string[] configured = ConfiguredIds(TierFor(current, name), name);

            // EDGE-009: a runtime that is not running is told before the models route is asked, so the user gets "ollama serve" and not a bare connection error.
            if (tier == Tier.Local)
            {
                await _localRuntime.CheckAvailableAsync(cancellationToken).ConfigureAwait(false);
            }

            asked.Add(await AskAndVerifyAsync(tier, name, configured, existing: null, cancellationToken).ConfigureAwait(false));
        }

        if (checkJudge && keys.MissingJudgeVariable is null && current.Judge.ModelId is { } judgeModel && !string.IsNullOrWhiteSpace(judgeModel))
        {
            Tier judgeTier = JudgeTierName.Parse(current.Judge.Tier);
            ListedTier? existing = asked.Find(listed => listed.Tier == judgeTier);
            ListedTier verified = await AskAndVerifyAsync(judgeTier, TierOptions.NameOf(judgeTier), [judgeModel], existing, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                asked.Add(verified);
            }
        }

        return new ModelCheckResult([.. asked.Select(static listed => new ModelCheckEntry(listed.Name, listed.Endpoint, [.. listed.Matched]))]);
    }

    /// <summary>The check of <see cref="CheckModelsAsync(IReadOnlyList{Tier}, KeyCheckResult, bool, CancellationToken)"/> with the judge model included, as a run makes it.</summary>
    /// <param name="tiers">The selected tiers, from <see cref="SelectTiers(string)"/>.</param>
    /// <param name="keys">The result of <see cref="CheckKeys(IReadOnlyList{Tier}, bool)"/>.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The tiers that were asked, with their endpoints and the configured IDs that were listed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> or <paramref name="keys"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tiers"/> is empty.</exception>
    /// <exception cref="ConfigurationException">A tier is not configured or has no model ID; exit code 2.</exception>
    /// <exception cref="ModelNotFoundException">A configured ID is not listed, or a list is empty (exit code 4).</exception>
    /// <exception cref="EndpointUnreachableException">A models endpoint is unreachable or answers something that is not a model list (exit code 5).</exception>
    /// <exception cref="MissingKeyException">A tier names a key variable that has no value (exit code 3).</exception>
    public Task<ModelCheckResult> CheckModelsAsync(IReadOnlyList<Tier> tiers, KeyCheckResult keys, CancellationToken cancellationToken) =>
        CheckModelsAsync(tiers, keys, checkJudge: true, cancellationToken);

    /// <summary>
    /// Checks that every selected measured tier (<c>local</c>, <c>cloudgpu</c>) can be measured, before any completion call (design 3.7 point 4, EDGE-001): the GPU at <c>Gpu:Index</c> is
    /// opened through <paramref name="gpus"/>, the <c>Meter:Method</c> is applied to it (unless <paramref name="ignoreMeterMethod"/>), and for <c>cloudgpu</c> MIG must be off. Each source is disposed before the next tier is checked;
    /// hosted tiers are skipped and never open the GPU.
    /// </summary>
    /// <param name="tiers">The selected tiers, from <see cref="SelectTiers(string)"/>.</param>
    /// <param name="gpus">The GPU port; production opens NVML, tests a scripted session.</param>
    /// <param name="ignoreMeterMethod">True selects the method as <c>auto</c> does (the counter when the GPU has one, sampling otherwise) whatever <c>Meter:Method</c> says, so a forced <c>counter</c> on a GPU without it is no failure; the calibration of AJ-109 compares both methods and has no use for a forced one. False, the default, applies <c>Meter:Method</c> as a run does.</param>
    /// <returns>The meter capabilities of each measured tier that was checked, in the order of <paramref name="tiers"/>; empty when no measured tier is selected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> or <paramref name="gpus"/> is null.</exception>
    /// <exception cref="MeasurementPreconditionException">
    /// The GPU cannot be opened (the message carries the reason of the port, which names the missing library when NVML is not installed), <c>Meter:Method</c> is <c>counter</c> on a GPU
    /// without the energy counter, or MIG is enabled on the GPU of <c>cloudgpu</c> (exit code 6).
    /// </exception>
    /// <exception cref="ConfigurationException"><c>Meter:Method</c> or <c>Meter:SampleRateHz</c> is invalid (exit code 2); the options validation normally found it first.</exception>
    public IReadOnlyList<KeyValuePair<Tier, MeterCapabilities>> CheckMeasuredTiers(IReadOnlyList<Tier> tiers, IGpuTelemetrySourceFactory gpus, bool ignoreMeterMethod = false)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(gpus);

        AgentJoulesOptions current = _options.Value;
        List<KeyValuePair<Tier, MeterCapabilities>> checkedTiers = [];
        foreach (Tier tier in tiers.Where(TierKinds.IsMeasured))
        {
            string name = TierOptions.NameOf(tier);
            GpuOpenResult opened = gpus.Open(current.Gpu.Index);
            if (!opened.IsAvailable)
            {
                throw new MeasurementPreconditionException($"tier {name} measures the GPU, but the GPU cannot be opened: {TextEcho.ForMessage(opened.Reason, MaxReasonLength)}");
            }

            using IGpuTelemetrySource source = opened.Source;
            MeterCapabilities capabilities = MeterMethodSelector.Select(ignoreMeterMethod ? AutoMethod : current.Meter.Method, source, current.Meter.SampleRateHz);
            if (tier == Tier.CloudGpu && string.Equals(source.Identity.MigMode, MigEnabled, StringComparison.Ordinal))
            {
                throw new MeasurementPreconditionException("tier cloudgpu cannot be measured while MIG is enabled on the GPU: NVML reads the energy of the whole GPU, not of one instance; disable MIG on the VM");
            }

            checkedTiers.Add(KeyValuePair.Create(tier, capabilities));
        }

        return checkedTiers;
    }

    // Asks the models route of the tier (or reuses the list of a tier already asked), checks the configured IDs against it and records the matched ones; throws for the missing ones.
    private async Task<ListedTier> AskAndVerifyAsync(Tier tier, string name, string[] configured, ListedTier? existing, CancellationToken cancellationToken)
    {
        ListedTier listed = existing ?? new ListedTier(tier, name, _catalog.EndpointOf(tier), await _catalog.ListAsync(tier, cancellationToken).ConfigureAwait(false));

        // The configured ID is compared with the listed IDs exactly as both were written; only the text that is reported (matched, missing) is masked, cut and scrubbed.
        List<string> missing = [];
        List<string> commands = [];
        List<string> byHand = [];
        bool ollama = tier == Tier.Local && IsOllamaConfigured();
        foreach (string id in configured)
        {
            string shown = ModelIdListFormatter.Echo(_redactor, id);
            if (listed.Catalog.Lists(id))
            {
                if (!listed.Matched.Contains(shown, StringComparer.Ordinal))
                {
                    listed.Matched.Add(shown);
                }
            }
            else
            {
                // The configured IDs are distinct as written, so two missing tags that share their first 64 characters both stay: each gets its line and its command.
                missing.Add(shown);

                // EDGE-009: on Ollama the fix of a tag that is not listed is one command, built from the configured tag (masked, never cut) and only when the tag is a valid Ollama name; any other tag
                // is named through the echo and must be pulled by hand, so no text of the configuration reaches a shell line.
                if (ollama)
                {
                    string command = PullCommandFor(_redactor.Redact(id));
                    if (command.Length > 0)
                    {
                        commands.Add(command);
                    }
                    else
                    {
                        byHand.Add(shown);
                    }
                }
            }
        }

        return missing.Count > 0
            ? throw new ModelNotFoundException(name, missing, ModelIdListFormatter.Format(listed.Catalog.DisplayIds).Listed, listed.Catalog.Count, commands, byHand)
            : listed;
    }

    // The pull command of a tag, or empty when the tag is not an Ollama name (the grammar of Ollama model names and tags, with the optional host and namespace parts) or the command would be longer than the
    // message allows. A tag that the redactor changed holds a mask character, which the grammar does not allow, so a masked tag never becomes a command.
    private static string PullCommandFor(string tag)
    {
        string command = OllamaPullCommand + tag;
        return OllamaName().IsMatch(tag) && command.Length <= ModelNotFoundException.MaxFixCommandLength ? command : string.Empty;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/:-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OllamaName();

    private bool IsOllamaConfigured() => string.Equals(_options.Value.Local.Runtime, LocalOptions.Ollama, StringComparison.Ordinal);

    // The model IDs a run of the tier calls: the tier model and every per-agent model, each once, blank entries ignored like TierOptions.ResolveModelId ignores them.
    private static string[] ConfiguredIds(TierOptions tier, string name)
    {
        string[] ids = [.. new[] { tier.ModelId }.Concat(tier.AgentModelIds.Values).OfType<string>().Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)];

        return ids.Length > 0
            ? ids
            : throw new ConfigurationException("Tiers:" + name + ":ModelId is not set and Tiers:" + name + ":AgentModelIds holds no model ID; set one of them");
    }

    private static TierOptions TierFor(AgentJoulesOptions current, string name) =>
        current.Tiers.TryGetValue(name, out TierOptions? tier) && tier is not null
            ? tier
            : throw new ConfigurationException("Tiers:" + name + " is not configured");

    // One tier that was asked: what the endpoint listed, and the configured IDs found in them so far.
    private sealed class ListedTier(Tier tier, string name, Uri endpoint, ModelCatalog catalog)
    {
        public Tier Tier { get; } = tier;

        public string Name { get; } = name;

        public Uri Endpoint { get; } = endpoint;

        public ModelCatalog Catalog { get; } = catalog;

        public List<string> Matched { get; } = [];
    }

    // The name of the variable the tier reads its key from, or null for a tier that is not configured or names none.
    private static string? KeyVariableOf(AgentJoulesOptions current, string? tierName) =>
        tierName is not null && current.Tiers.TryGetValue(tierName, out TierOptions? tier) && tier is { ApiKeyVariable: { } variable } && !string.IsNullOrWhiteSpace(variable)
            ? variable
            : null;

    private void AddIfMissing(List<string> missing, string? variable)
    {
        if (variable is not null && !missing.Contains(variable, StringComparer.Ordinal) && IsMissing(variable))
        {
            missing.Add(variable);
        }
    }

    private bool IsMissing(string variable) => string.IsNullOrWhiteSpace(_configuration[variable]);
}