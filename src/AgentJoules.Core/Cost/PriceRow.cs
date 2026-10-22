using System.Text.Json.Serialization;

namespace AgentJoules.Core.Cost;

/// <summary>
/// One row of the committed price file <c>config/prices.json</c> (REQ-009): the published price of one model on one tier, in USD per 1 000 000 tokens, with the
/// page it was read from and the day it was read. It is a JSON input type read through <see cref="CoreJsonContext"/>, not a configuration-bound type.
/// <see cref="PriceTable.Load"/> checks every rule of <c>config/prices.schema.json</c> before it builds a row, so a row returned by <see cref="PriceTable.Load"/> has two
/// prices above 0, an https source URL and a real calendar date; a price of 0 or a missing source never reaches a row from a file.
/// </summary>
/// <param name="Tier">One of <c>local</c>, <c>cloudgpu</c>, <c>nvidia</c> or <c>nebius</c>.</param>
/// <param name="ModelId">The model ID as the tier's endpoint names it.</param>
/// <param name="InputUsdPer1M">USD per 1 000 000 input tokens; finite and above 0.</param>
/// <param name="OutputUsdPer1M">USD per 1 000 000 output tokens; finite and above 0.</param>
/// <param name="SourceUrl">The https page the price was read from.</param>
/// <param name="RetrievedDate">The day the page was read, as <c>yyyy-MM-dd</c> in the file.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PriceRow(
    string Tier,
    string ModelId,
    double InputUsdPer1M,
    double OutputUsdPer1M,
    string SourceUrl,
    DateOnly RetrievedDate);