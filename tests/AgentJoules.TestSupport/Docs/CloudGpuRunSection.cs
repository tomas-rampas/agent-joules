namespace AgentJoules.TestSupport.Docs;

/// <summary>One <c>## </c> section of <c>docs/cloudgpu-run.md</c> as read by <see cref="CloudGpuRunDocumentParser"/>.</summary>
/// <param name="Name">The heading text the section is looked up by.</param>
/// <param name="Present">Whether a <c>## </c> heading of that name exists outside a code fence.</param>
/// <param name="Body">The text between the heading and the next <c>## </c> heading, lines joined with LF; empty when the section is absent.</param>
/// <param name="FirstLine">The 1-based line number of the first body line, or 0 when the section is absent.</param>
public sealed record CloudGpuRunSection(string Name, bool Present, string Body, int FirstLine);