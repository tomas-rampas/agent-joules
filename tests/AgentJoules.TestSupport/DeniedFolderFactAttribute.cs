using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>A fact that is skipped, with the reason, on a host where a folder cannot be made unlistable for the current user (<see cref="FolderAccess.DenyListingSkipReason"/>).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DeniedFolderFactAttribute : FactAttribute
{
    public DeniedFolderFactAttribute()
    {
        Skip = FolderAccess.DenyListingSkipReason();
    }
}