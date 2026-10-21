namespace AgentJoules.Workflow.Providers;

/// <summary>
/// The transport settings <see cref="ProviderClientFactory"/> gives the OpenAI client of a tier, exposed so that a test can assert them without
/// a reflection into the SDK (REQ-012): the endpoint, the network timeout of one call, and the SDK retry count.
/// </summary>
/// <param name="Endpoint">The base URL of the tier, <c>Tiers:&lt;tier&gt;:BaseUrl</c>.</param>
/// <param name="NetworkTimeout">The SDK <c>NetworkTimeout</c>, <c>Tiers:&lt;tier&gt;:TimeoutSeconds</c> as a time span; the <see cref="HttpClient"/> itself has an infinite timeout.</param>
/// <param name="MaxRetries">The SDK retry count; always 0, because the rate-limit retry of a run is its own layer and a hidden SDK retry would spend the energy window twice.</param>
public sealed record ProviderClientSettings(Uri Endpoint, TimeSpan NetworkTimeout, int MaxRetries);