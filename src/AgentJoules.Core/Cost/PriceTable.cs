using System.Globalization;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Cost;

/// <summary>
/// The committed price file <c>config/prices.json</c> as a lookup (REQ-009, design 3.2): one <see cref="PriceRow"/> per <c>(tier, modelId)</c>, each with the page
/// it was read from and the day it was read. A model without a row has no price, and the cost calculator turns that into N/A, never into a price of 0.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Load"/> enforces in code every rule of <c>config/prices.schema.json</c> (design 2.4 keeps the schema library out of Core; the
/// unit test <c>Loader_and_schema_reject_the_same_invalid_rows</c> keeps the file and the code in step): the file is a JSON array of row objects, each with exactly the six
/// members <c>tier</c>, <c>modelId</c>, <c>inputUsdPer1M</c>, <c>outputUsdPer1M</c>, <c>sourceUrl</c> and <c>retrievedDate</c>, none of them repeated; the tier is one of
/// the four tier names, both prices are finite and above 0, the source is an https URL made of URI characters only and without credentials and the date is a calendar date written
/// <c>yyyy-MM-dd</c>. A tier and model pair may appear once; the comparison ignores letter case, like the <c>AgentModelIds</c> dictionary of the configuration. Every
/// failure is a <see cref="ConfigurationException"/> (exit 2) that names the file by its name only, the row index and the member, and never holds the offending value,
/// the absolute path or the text of a JSON parser, which can quote the document.
/// </para>
/// <para>Passing the schema is necessary but not sufficient: the loader additionally requires a well-formed absolute https URI with a host, a non-blank modelId (by <see cref="char.IsWhiteSpace(char)"/>), a finite price and no repeated member.</para>
/// <para>The table never changes after <see cref="Load"/>, so one instance is shared by every metered client of a run and <see cref="Find"/> is safe from any thread.</para>
/// </remarks>
public sealed class PriceTable
{
    /// <summary>The largest price file <see cref="Load"/> reads: far above any plausible table, far below what could fill the memory of a run.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private const string MemberTier = "tier";
    private const string MemberModelId = "modelId";
    private const string MemberInput = "inputUsdPer1M";
    private const string MemberOutput = "outputUsdPer1M";
    private const string MemberSourceUrl = "sourceUrl";
    private const string MemberRetrievedDate = "retrievedDate";
    private const string DateFormat = "yyyy-MM-dd";
    private const string UnnamedFile = "the price file";
    private const int MaxEchoedFileNameLength = 64;
    private const int MaxEchoedMemberLength = 32;

    // The punctuation of RFC 3986 (unreserved, reserved and the percent sign) except the at sign; letters and digits are added by IsUrlCharacter.
    private const string UrlPunctuation = "-._~:/?#[]!$&'()*+,;=%";

    private static readonly string[] MemberNames = [MemberTier, MemberModelId, MemberInput, MemberOutput, MemberSourceUrl, MemberRetrievedDate];

    // Strict: a byte sequence that is not UTF-8 throws instead of becoming a replacement character.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<string, Dictionary<string, (int Index, PriceRow Row)>> _byTier;

    private PriceTable(PriceRow[] rows, Dictionary<string, Dictionary<string, (int Index, PriceRow Row)>> byTier)
    {
        Rows = Array.AsReadOnly(rows);
        _byTier = byTier;
    }

    /// <summary>The rows in file order; the list cannot be changed.</summary>
    public IReadOnlyList<PriceRow> Rows { get; }

    /// <summary>Reads and checks a price file.</summary>
    /// <param name="path">The path of the file, <c>Paths:Prices</c> resolved by the caller.</param>
    /// <returns>The table; an empty array gives a table without rows.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or white space.</exception>
    /// <exception cref="ConfigurationException">The file is missing, unreadable, larger than <see cref="MaxFileBytes"/>, not valid UTF-8 or not valid JSON; it is not an array of row objects; a row breaks a rule of the schema; or two rows carry the same tier and model.</exception>
    public static PriceTable Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string name = Path.GetFileName(path);
        string label = name.Length == 0 ? UnnamedFile : TextEcho.ForMessage(name, MaxEchoedFileNameLength);
        string text = ReadText(path, label);

        JsonDocument document;
        try
        {
            // Repeats are detected per row in ReadRow, so a repeated member is reported with its name and not as a parse failure.
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = true });
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidOperationException)
        {
            // Not attached as the inner exception: the parser's message can quote the document.
            throw new ConfigurationException($"{label}: not valid JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigurationException($"{label}: the file must be a JSON array of rows");
            }

            var rows = new List<PriceRow>();
            var byTier = new Dictionary<string, Dictionary<string, (int Index, PriceRow Row)>>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                int index = rows.Count;
                PriceRow row = ReadRow(label, index, element);

                if (!byTier.TryGetValue(row.Tier, out Dictionary<string, (int Index, PriceRow Row)>? models))
                {
                    models = new Dictionary<string, (int Index, PriceRow Row)>(StringComparer.OrdinalIgnoreCase);
                    byTier[row.Tier] = models;
                }

                if (!models.TryAdd(row.ModelId, (index, row)))
                {
                    throw new ConfigurationException($"{label}: rows {models[row.ModelId].Index.ToString(CultureInfo.InvariantCulture)} and {index.ToString(CultureInfo.InvariantCulture)} have the same {MemberTier} and {MemberModelId}");
                }

                rows.Add(row);
            }

            return new PriceTable([.. rows], byTier);
        }
    }

    /// <summary>Finds the row of a tier and model.</summary>
    /// <param name="tier">The tier name; letter case is ignored.</param>
    /// <param name="modelId">The model ID; letter case is ignored.</param>
    /// <returns>The row, or null when the model has no price row (the cost is then N/A, never 0).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tier"/> or <paramref name="modelId"/> is null.</exception>
    public PriceRow? Find(string tier, string modelId)
    {
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentNullException.ThrowIfNull(modelId);

        return _byTier.TryGetValue(tier, out Dictionary<string, (int Index, PriceRow Row)>? models) && models.TryGetValue(modelId, out (int Index, PriceRow Row) found) ? found.Row : null;
    }

    private static string ReadText(string path, string label)
    {
        byte[] buffer = new byte[MaxFileBytes + 1];
        int read;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // One byte more than the cap: reading it proves the file is larger, and no more of a large file is ever read.
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ConfigurationException($"{label}: the price file does not exist");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Not attached as the inner exception: its message carries the absolute path.
            throw new ConfigurationException($"{label}: the price file cannot be read");
        }

        if (read > MaxFileBytes)
        {
            throw new ConfigurationException($"{label}: the price file is larger than 1 MiB");
        }

        ReadOnlySpan<byte> content = buffer.AsSpan(0, read);
        if (content.StartsWith(Encoding.UTF8.Preamble))
        {
            content = content[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new ConfigurationException($"{label}: the price file is not valid UTF-8");
        }
    }

    private static PriceRow ReadRow(string label, int index, JsonElement element)
    {
        string prefix = $"{label}: row {index.ToString(CultureInfo.InvariantCulture)}: ";
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationException(prefix + "a row must be an object");
        }

        try
        {
            CheckMemberNames(prefix, element);
            CheckTier(prefix, element);
            CheckText(prefix, element, MemberModelId);
            CheckPrice(prefix, element, MemberInput);
            CheckPrice(prefix, element, MemberOutput);
            CheckSourceUrl(prefix, element);
            CheckDate(prefix, element);

            return element.Deserialize(CoreJsonContext.Default.PriceRow) ?? throw new ConfigurationException(prefix + "a row must be an object");
        }
        catch (InvalidOperationException)
        {
            // A string with a lone surrogate escape cannot be decoded. Not attached as the inner exception: it can quote the document.
            throw new ConfigurationException(prefix + "a string holds an invalid escape");
        }
        catch (JsonException)
        {
            // Every rule is checked above, so this is a guard against the checks and the record drifting apart; the parser's message can quote the document and is dropped.
            throw new ConfigurationException(prefix + "the row is not valid");
        }
    }

    private static void CheckMemberNames(string prefix, JsonElement row)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in row.EnumerateObject())
        {
            string memberName;
            try
            {
                memberName = property.Name;
            }
            catch (InvalidOperationException)
            {
                throw new ConfigurationException(prefix + "a member name holds an invalid escape");
            }

            if (!MemberNames.Contains(memberName, StringComparer.Ordinal))
            {
                throw new ConfigurationException(prefix + (memberName.Length > MaxEchoedMemberLength
                    ? $"a member name of {memberName.Length.ToString(CultureInfo.InvariantCulture)} characters is not a known member"
                    : $"{TextEcho.ForMessage(memberName, MaxEchoedMemberLength)} is not a known member"));
            }

            if (!seen.Add(memberName))
            {
                throw new ConfigurationException(prefix + $"{memberName} is repeated");
            }
        }
    }

    private static JsonElement Required(string prefix, JsonElement row, string member, JsonValueKind kind, string kindName)
    {
        return !row.TryGetProperty(member, out JsonElement value)
            ? throw new ConfigurationException(prefix + $"{member} is required")
            : value.ValueKind == kind ? value : throw new ConfigurationException(prefix + $"{member} must be {kindName}");
    }

    private static void CheckTier(string prefix, JsonElement row)
    {
        string tier = Required(prefix, row, MemberTier, JsonValueKind.String, "a string").GetString() ?? string.Empty;
        if (!TierOptions.KnownNames.Contains(tier, StringComparer.Ordinal))
        {
            throw new ConfigurationException(prefix + $"{MemberTier} must be {ConfigurationMessages.OrList(TierOptions.KnownNames)}");
        }
    }

    private static void CheckText(string prefix, JsonElement row, string member)
    {
        string text = Required(prefix, row, member, JsonValueKind.String, "a string").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ConfigurationException(prefix + $"{member} must not be blank");
        }
    }

    private static void CheckPrice(string prefix, JsonElement row, string member)
    {
        JsonElement number = Required(prefix, row, member, JsonValueKind.Number, "a number");
        if (!number.TryGetDouble(out double price) || !double.IsFinite(price))
        {
            throw new ConfigurationException(prefix + $"{member} must be a finite number");
        }

        if (price <= 0)
        {
            throw new ConfigurationException(prefix + $"{member} must be above 0");
        }
    }

    // The one rule of config/prices.schema.json for the source: https://, then only the characters RFC 3986 allows in a URI without the at sign, so no space, no control
    // character, no non-ASCII character, no quote, angle bracket, caret or backslash, and no credentials can be written. The framework check then refuses what is
    // made of those characters but is still not a well-formed URI (a bad percent escape, no host).
    private static void CheckSourceUrl(string prefix, JsonElement row)
    {
        string url = Required(prefix, row, MemberSourceUrl, JsonValueKind.String, "a string").GetString() ?? string.Empty;
        bool acceptable = url.StartsWith(Uri.UriSchemeHttps + Uri.SchemeDelimiter, StringComparison.Ordinal)
            && url.All(IsUrlCharacter)
            && Uri.IsWellFormedUriString(url, UriKind.Absolute)
            && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            // Defence in depth: the at sign is outside the character class, so no credentials can pass the check above.
            && uri.UserInfo.Length == 0
            && uri.Host.Length > 0;
        if (!acceptable)
        {
            throw new ConfigurationException(prefix + $"{MemberSourceUrl} must be an {Uri.UriSchemeHttps} URL");
        }
    }

    private static bool IsUrlCharacter(char character) => char.IsAsciiLetterOrDigit(character) || UrlPunctuation.Contains(character, StringComparison.Ordinal);

    private static void CheckDate(string prefix, JsonElement row)
    {
        string date = Required(prefix, row, MemberRetrievedDate, JsonValueKind.String, "a string").GetString() ?? string.Empty;
        if (!DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ConfigurationException(prefix + $"{MemberRetrievedDate} must be a date written {DateFormat}");
        }
    }
}