using AgentJoules.Core.Cli;
using AgentJoules.Core.Cost;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-070 scenario (load a price table), handed to the step class by Reqnroll context injection. It holds the price file the Given
/// steps write into a <see cref="TempWorkspace"/> (the repository is never written), the table the loader returned, the row a lookup found, and the
/// failure of a scenario that expects one. The scenarios assert on the <see cref="ConfigurationException"/> the loader throws: its exit code is the
/// process exit code and its message is the output.
/// </summary>
public sealed class PriceTableContext : IDisposable
{
    /// <summary>The model ID of the row the Given steps write.</summary>
    public const string ModelId = PriceFileFixtures.ModelId;

    /// <summary>The tier of the row the Given steps write.</summary>
    public const string Tier = PriceFileFixtures.Tier;

    private const string FileName = "prices.json";

    private TempWorkspace? _workspace;

    /// <summary>The path of the price file, or null before a Given step wrote it.</summary>
    public string? PricePath { get; private set; }

    /// <summary>The table the loader returned, or null before it ran or when it failed.</summary>
    public PriceTable? Table { get; private set; }

    /// <summary>The row of the last lookup, or null when the lookup found none or none ran.</summary>
    public PriceRow? Found { get; private set; }

    /// <summary>True once a lookup ran, so a null <see cref="Found"/> means "no row" and not "no lookup".</summary>
    public bool LookedUp { get; private set; }

    /// <summary>The failure of the load of a scenario that expects one, or null.</summary>
    public ConfigurationException? Failure { get; private set; }

    /// <summary>Writes a price file whose rows are the given JSON objects.</summary>
    /// <param name="rows">Each element is one complete row object as JSON text.</param>
    public void WriteRows(params string[] rows)
    {
        _workspace ??= TempWorkspace.CreateEmpty();
        PricePath = _workspace.WriteFile(FileName, "[" + string.Join(",", rows) + "]");
    }

    /// <summary>Loads the price file, keeping the table or the failure.</summary>
    public void Load()
    {
        try
        {
            Table = PriceTable.Load(PricePath ?? throw new InvalidOperationException("No price file was written."));
        }
        catch (ConfigurationException failure)
        {
            Failure = failure;
        }
    }

    /// <summary>Loads the price file and looks the pair up.</summary>
    public void LoadAndFind(string tier, string modelId)
    {
        Load();
        LookedUp = true;
        Found = Table?.Find(tier, modelId);
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}