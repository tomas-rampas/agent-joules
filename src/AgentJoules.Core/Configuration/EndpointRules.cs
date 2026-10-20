namespace AgentJoules.Core.Configuration;

/// <summary>What is wrong with a tier base URL; the words of the messages belong to the callers, which name their own key path.</summary>
public enum EndpointProblem
{
    /// <summary>Nothing: the URL is usable.</summary>
    None = 0,

    /// <summary>Not an absolute http or https URL, or it carries user info, a query or a fragment.</summary>
    Shape = 1,

    /// <summary>Plain http on a host that is not loopback, where https was demanded.</summary>
    PlainHttpOffLoopback = 2,
}

/// <summary>
/// The one rule for the base URL of a tier, used by the validator at start and by the provider client factory when a client is created (REQ-012,
/// design 5.1), so a URL the validator refuses is refused again at the moment a key would be sent. The scheme is checked first, so a <c>file:</c> URL
/// (whose host counts as loopback) is a shape problem and never passes as a safe local endpoint.
/// </summary>
public static class EndpointRules
{
    /// <summary>Checks <paramref name="endpoint"/>.</summary>
    /// <param name="endpoint">The URL; null is a shape problem.</param>
    /// <param name="requireHttpsOffLoopback">True to refuse plain http unless the host is loopback (always true for the validator; true for a tier that sends a key).</param>
    /// <returns>The first problem, or <see cref="EndpointProblem.None"/>.</returns>
    public static EndpointProblem Check(Uri? endpoint, bool requireHttpsOffLoopback)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri)
        {
            return EndpointProblem.Shape;
        }

        bool https = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        bool http = string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);
        return !(http || https) || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            ? EndpointProblem.Shape
            : requireHttpsOffLoopback && !https && !endpoint.IsLoopback
                ? EndpointProblem.PlainHttpOffLoopback
                : EndpointProblem.None;
    }
}