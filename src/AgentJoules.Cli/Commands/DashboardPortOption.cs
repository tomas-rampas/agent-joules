using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

using AgentJoules.Dashboard;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>--dashboard-port</c> (REQ-032, EDGE-018, P-74): the preferred loopback port of the dashboard, a whole number from 1 to 65535, <see cref="DashboardServer.DefaultPort"/> when not given.
/// A value outside the range or not a whole number is a parse error (exit 2 before any host). One factory, so <c>run</c> and <c>dashboard</c> parse the port the same way.
/// </summary>
internal static class DashboardPortOption
{
    /// <summary>The name of the option.</summary>
    internal const string Name = "--dashboard-port";

    /// <summary>The parse error of a value that is not a port; it names the rule, and the value is not echoed.</summary>
    internal const string RangeMessage = "--dashboard-port must be a whole number from 1 to 65535";

    private const int LowestPort = 1;
    private const int HighestPort = 65535;

    /// <summary>Creates the option.</summary>
    /// <returns>A new option instance (System.CommandLine options belong to one command).</returns>
    internal static Option<int> Create() => new(Name)
    {
        Description = "The preferred loopback port of the dashboard; the next 19 ports are tried when it is taken.",
        DefaultValueFactory = static _ => DashboardServer.DefaultPort,
        CustomParser = static result => Parse(result),
    };

    // The parser of the option: the default one names the .NET type in its error, and a range check after it would never see a token that does not convert.
    private static int Parse(ArgumentResult result)
    {
        if (result.Tokens.Count == 1
            && int.TryParse(result.Tokens[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is >= LowestPort and <= HighestPort)
        {
            return port;
        }

        result.AddError(RangeMessage);
        return 0;
    }
}