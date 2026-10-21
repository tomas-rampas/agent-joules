using AgentJoules.Core.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// Fills <see cref="AgentJoulesOptions"/> through <see cref="OptionsLoader.Bind(IConfiguration, AgentJoulesOptions)"/>, so an unknown key, an empty key and a value of the wrong
/// shape reach the CLI as a <see cref="AgentJoules.Core.Cli.ConfigurationException"/> (exit 2) from <c>StartAsync</c>, with a message that names keys and
/// never shows a value. <c>BindConfiguration</c> would skip those checks.
/// </summary>
internal sealed class AgentJoulesOptionsSetup(IConfiguration configuration) : IConfigureOptions<AgentJoulesOptions>
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public void Configure(AgentJoulesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        OptionsLoader.Bind(_configuration, options);
    }
}