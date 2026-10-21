using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>A fact that is skipped, with the reason, on a host that cannot create a file symbolic link without elevation (<see cref="FolderAccess.SymbolicLinkSkipReason"/>).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        Skip = FolderAccess.SymbolicLinkSkipReason();
    }
}