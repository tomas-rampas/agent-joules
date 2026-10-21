using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>A fact that is skipped, with the reason, on a host where a folder cannot be made to refuse file creation to the current user (<see cref="FolderAccess.DenyWritingSkipReason"/>).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DeniedWritingFactAttribute : FactAttribute
{
    public DeniedWritingFactAttribute()
    {
        Skip = FolderAccess.DenyWritingSkipReason();
    }
}