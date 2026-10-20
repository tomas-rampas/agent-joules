using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>A fact that is skipped, with the reason, on a host that is not Windows, for a test of behaviour only the Windows file system has (a replace that fails while another handle holds the target open).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        Skip = OperatingSystem.IsWindows() ? null : "this behaviour is specific to the Windows file system";
    }
}