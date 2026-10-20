namespace AgentJoules.Cli;

/// <summary>Names of options shared between the application and its tests.</summary>
internal static class CliOptions
{
    /// <summary>The hidden root option that makes a worker thread throw; not part of the documented parse surface.</summary>
    internal const string DebugThrowOnWorker = "--debug-throw-on-worker";
}