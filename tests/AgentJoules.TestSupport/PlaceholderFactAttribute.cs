using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>
/// Marks an M0 placeholder test that the named story replaces. It needs no analyzer exception
/// because <see cref="FactAttribute.Skip"/> is set at runtime, not as a literal argument.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PlaceholderFactAttribute : FactAttribute
{
    public PlaceholderFactAttribute(string replacedBy)
    {
        Skip = "placeholder: replaced by " + replacedBy;
    }
}