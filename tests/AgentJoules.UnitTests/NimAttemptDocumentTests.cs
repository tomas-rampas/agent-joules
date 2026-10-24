using System.Globalization;
using System.Security.Cryptography;

using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-102 (REQ-019, EDGE-010): the contract of <c>docs/nim-local-attempt.md</c>. The documents are built in memory (<see cref="NimAttemptDraft"/>) and read by <see cref="NimAttemptDocumentParser"/>; the rules are
/// <see cref="NimAttemptRules"/>. A pass is an empty list of violations; a violation names the rule and the heading or line and never shows a key. The last test runs the same rules on the committed document and is
/// skipped, with a message, while the file does not exist.
/// </summary>
[Trait("Spec", "REQ-019")]
[Trait("Spec", "EDGE-010")]
public sealed class NimAttemptDocumentTests
{
    private static IReadOnlyList<string> Check(NimAttemptDraft draft, string? ngcKey = null) =>
        NimAttemptRules.Check(NimAttemptDocumentParser.Parse(draft.Render()), ngcKey);

    private static string Joined(IReadOnlyList<string> violations) => string.Join('\n', violations);

    private static string RandomBase64Url(int length) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes((length * 3 / 4) + 3)).Replace('+', '-').Replace('/', '_').Replace("=", string.Empty, StringComparison.Ordinal)[..length];

    // ---- the scenarios of the story ----

    [Fact]
    public void A_complete_document_passes() => Assert.Empty(Check(new NimAttemptDraft()));

    [Fact]
    public void Exactly_90_minutes_passes() => Assert.Empty(Check(new NimAttemptDraft().Between(TimeSpan.FromMinutes(90))));

    [Fact]
    public void An_exceeded_box_with_a_stated_reason_passes()
    {
        var draft = new NimAttemptDraft().Between(TimeSpan.FromMinutes(104));
        draft.BoxExceeded = "image pull stalled";

        Assert.Empty(Check(draft));
    }

    [Fact]
    public void A_missing_heading_fails_and_the_message_names_it()
    {
        var draft = new NimAttemptDraft();
        _ = draft.Headings.Remove("Outcome");

        string message = Assert.Single(Check(draft));

        Assert.Contains("Outcome", message, StringComparison.Ordinal);
        Assert.Contains("heading", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ninety_one_minutes_without_a_statement_fails_and_the_message_names_the_box()
    {
        string message = Assert.Single(Check(new NimAttemptDraft().Between(TimeSpan.FromMinutes(91))));

        Assert.Contains("90-minute box", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_docker_login_with_a_password_argument_fails_and_the_message_says_password_stdin()
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines.Insert(0, "docker login nvcr.io --username '$oauthtoken' --password not-a-key");

        string message = Assert.Single(Check(draft));

        Assert.Contains("--password-stdin", message, StringComparison.Ordinal);
        Assert.Contains("line ", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_built_key_in_the_text_fails_and_the_message_does_not_echo_the_suffix()
    {
        string suffix = RandomBase64Url(40);
        Assert.Equal(40, suffix.Length);
        var draft = new NimAttemptDraft();
        draft.OutcomeLines.Add("export NGC_API_KEY=nvapi-" + suffix);

        IReadOnlyList<string> violations = Check(draft);

        string message = Assert.Single(violations);
        Assert.Contains("key material found", message, StringComparison.Ordinal);
        Assert.Contains("line ", message, StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, Joined(violations), StringComparison.Ordinal);
        Assert.DoesNotContain("nvapi-", Joined(violations), StringComparison.Ordinal);
    }

    // ---- one test per rule edge ----

    [Fact]
    public void Ninety_minutes_and_one_second_fails()
    {
        string message = Assert.Single(Check(new NimAttemptDraft().Between(TimeSpan.FromMinutes(90) + TimeSpan.FromSeconds(1))));

        Assert.Contains("90-minute box", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fraction_form_parses_and_exactly_90_minutes_in_that_form_passes()
    {
        var draft = new NimAttemptDraft().Between(TimeSpan.FromMinutes(90), fraction: true);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.000Z$", draft.Started);

        NimAttemptDocument document = NimAttemptDocumentParser.Parse(draft.Render());

        Assert.Equal(NimAttemptDraft.DefaultStart, document.Started);
        Assert.Equal(NimAttemptDraft.DefaultStart.AddMinutes(90), document.Ended);
        Assert.Empty(NimAttemptRules.Check(document, null));
    }

    [Fact]
    public void The_fraction_form_keeps_the_milliseconds()
    {
        var draft = new NimAttemptDraft { Started = "2026-10-24T10:00:00.250Z", Ended = "2026-10-24T10:00:01.750Z" };

        NimAttemptDocument document = NimAttemptDocumentParser.Parse(draft.Render());

        Assert.Equal(TimeSpan.FromMilliseconds(1500), document.Ended - document.Started);
    }

    [Fact]
    public void A_heading_with_trailing_text_does_not_count()
    {
        var draft = new NimAttemptDraft();
        draft.Headings[draft.Headings.IndexOf("Commands")] = "Commands (UTC)";

        NimAttemptDocument document = NimAttemptDocumentParser.Parse(draft.Render());
        string message = Assert.Single(NimAttemptRules.Check(document, null));

        Assert.Contains("Commands (UTC)", document.Headings);
        Assert.Contains("\"Commands\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_heading_inside_a_code_fence_does_not_count()
    {
        var draft = new NimAttemptDraft();
        _ = draft.Headings.Remove("Outcome");
        draft.CommandLines.AddRange(["```", "## Outcome", "```"]);

        string message = Assert.Single(Check(draft));

        Assert.Contains("Outcome", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_Started_line_fails_and_names_the_line()
    {
        var draft = new NimAttemptDraft { Started = null };

        string message = Assert.Single(Check(draft));

        Assert.Contains("Started:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_Ended_value_fails_and_is_kept_as_text()
    {
        var draft = new NimAttemptDraft { Ended = "yesterday around noon" };

        NimAttemptDocument document = NimAttemptDocumentParser.Parse(draft.Render());
        string message = Assert.Single(NimAttemptRules.Check(document, null));

        Assert.Null(document.Ended);
        Assert.Equal("yesterday around noon", document.EndedText);
        Assert.Contains("Ended:", message, StringComparison.Ordinal);
        Assert.Contains("yesterday around noon", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-10-24T10:00:00")]
    [InlineData("2026-10-24T10:00:00+02:00")]
    [InlineData("2026-10-24 10:00:00Z")]
    [InlineData("2026-10-24T10:00Z")]
    public void Only_the_two_UTC_forms_are_times(string value)
    {
        var draft = new NimAttemptDraft { Started = value };

        NimAttemptDocument document = NimAttemptDocumentParser.Parse(draft.Render());

        Assert.Null(document.Started);
        Assert.Equal(value, document.StartedText);
        _ = Assert.Single(NimAttemptRules.Check(document, null));
    }

    [Fact]
    public void An_Ended_before_Started_fails()
    {
        var draft = new NimAttemptDraft { Ended = "2026-10-24T09:00:00Z" };

        string message = Assert.Single(Check(draft));

        Assert.Contains("before", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Box_exceeded_line_without_a_reason_does_not_excuse_the_box()
    {
        var draft = new NimAttemptDraft().Between(TimeSpan.FromMinutes(104));
        draft.BoxExceeded = "   ";

        string message = Assert.Single(Check(draft));

        Assert.Contains("90-minute box", message, StringComparison.Ordinal);
        Assert.Contains("reason", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Local runtime: nim")]
    [InlineData("Local runtime: ollama")]
    public void The_two_decision_lines_pass_and_set_the_runtime(string line)
    {
        var draft = new NimAttemptDraft();
        draft.DecisionLines[^1] = line;

        NimAttemptDocument document = NimAttemptDocumentParser.Parse(draft.Render());

        Assert.Equal(line["Local runtime: ".Length..], document.DecisionRuntime);
        Assert.Empty(NimAttemptRules.Check(document, null));
    }

    [Theory]
    [InlineData("Local runtime: vllm")]
    [InlineData("Local runtime: NIM")]
    [InlineData("runtime: nim")]
    public void A_decision_that_ends_with_another_line_fails_and_names_the_heading(string line)
    {
        var draft = new NimAttemptDraft();
        draft.DecisionLines[^1] = line;

        string message = Assert.Single(Check(draft));

        Assert.Contains("Decision", message, StringComparison.Ordinal);
        Assert.Contains("Local runtime:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_line_that_is_not_the_last_line_of_the_decision_fails()
    {
        var draft = new NimAttemptDraft();
        draft.DecisionLines.Add("Closing remark after the decision.");

        string message = Assert.Single(Check(draft));

        Assert.Contains("Decision", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Trailing_blank_lines_after_the_decision_line_are_allowed()
    {
        var draft = new NimAttemptDraft();
        draft.DecisionLines.AddRange([string.Empty, "   ", string.Empty]);

        Assert.Empty(Check(draft));
    }

    [Theory]
    [InlineData("docker login nvcr.io --username '$oauthtoken'")]
    [InlineData("docker login nvcr.io --username '$oauthtoken' --password=not-a-key")]
    [InlineData("docker login nvcr.io --username '$oauthtoken' --password not-a-key --password-stdin")]
    [InlineData("docker login nvcr.io --username '$oauthtoken' -p not-a-key")]
    [InlineData("echo x | docker login nvcr.io -u '$oauthtoken' -p not-a-key --password-stdin")]
    [InlineData("docker login nvcr.io --username '$oauthtoken' --password=not-a-key --password-stdin")]
    [InlineData("docker  login nvcr.io --username '$oauthtoken' -p not-a-key")]
    [InlineData("docker\tlogin nvcr.io --username '$oauthtoken' -p not-a-key")]
    [InlineData("docker login nvcr.io --username '$oauthtoken' --password-stdin <<< not-a-key")]
    public void A_docker_login_without_password_stdin_or_with_a_password_argument_fails(string line)
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines.Insert(0, line);

        string message = Assert.Single(Check(draft));

        Assert.Contains("--password-stdin", message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-key", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_offending_login_line_is_reported_and_its_number_is_named()
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines.Add("docker login nvcr.io --username '$oauthtoken' -p not-a-key");
        string[] lines = draft.Render().Split('\n');
        int lineNumber = Array.FindIndex(lines, static line => line.EndsWith("-p not-a-key", StringComparison.Ordinal)) + 1;

        string message = Assert.Single(Check(draft));

        Assert.Contains("line " + lineNumber.ToString(CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_built_ngc_key_without_the_nvapi_prefix_fails_without_being_echoed()
    {
        string key = RandomBase64Url(32);
        var draft = new NimAttemptDraft();
        draft.OutcomeLines.Add("The key was " + key + " in the log.");

        IReadOnlyList<string> violations = Check(draft, key);

        string message = Assert.Single(violations);
        Assert.Contains("key material found", message, StringComparison.Ordinal);
        Assert.Matches(@"at line \d+;", message);
        Assert.DoesNotContain(key, message, StringComparison.Ordinal);
        Assert.Empty(Check(draft, null));
        Assert.Empty(Check(draft, string.Empty));
    }

    [Fact]
    public void A_short_nvapi_text_is_not_a_key()
    {
        var draft = new NimAttemptDraft();
        draft.OutcomeLines.Add("The key prefix is nvapi-abc and nothing follows.");

        Assert.Empty(Check(draft));
    }

    [Fact]
    public void A_missing_logout_fails_and_names_docker_logout()
    {
        var draft = new NimAttemptDraft();
        _ = draft.CommandLines.Remove(NimAttemptDraft.LogoutLine);

        string message = Assert.Single(Check(draft));

        Assert.Contains("docker logout", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_violations_come_in_the_order_of_the_rules()
    {
        var draft = new NimAttemptDraft().Between(TimeSpan.FromMinutes(91));
        _ = draft.Headings.Remove("Image");
        _ = draft.CommandLines.Remove(NimAttemptDraft.LogoutLine);

        IReadOnlyList<string> violations = Check(draft);

        Assert.Equal(3, violations.Count);
        Assert.Contains("Image", violations[0], StringComparison.Ordinal);
        Assert.Contains("90-minute box", violations[1], StringComparison.Ordinal);
        Assert.Contains("docker logout", violations[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_parser_reads_the_lines_the_contract_names()
    {
        var draft = new NimAttemptDraft().Between(TimeSpan.FromMinutes(104));
        draft.BoxExceeded = "image pull stalled";

        NimAttemptDocument document = NimAttemptDocumentParser.Parse(draft.Render());

        Assert.Equal(["Commands", "Image", "Outcome", "Decision"], document.Headings);
        Assert.Equal(NimAttemptDraft.DefaultStart, document.Started);
        Assert.Equal(NimAttemptDraft.DefaultStart.AddMinutes(104), document.Ended);
        Assert.Equal("image pull stalled", document.BoxExceededReason);
        Assert.Equal("ollama", document.DecisionRuntime);
        Assert.Contains(NimAttemptDraft.LogoutLine, document.Sections["Commands"], StringComparison.Ordinal);
        Assert.Contains("memory.used", document.Sections["Outcome"], StringComparison.Ordinal);
        Assert.Equal(draft.Render(), document.Raw);
    }

    [Fact]
    public void Carriage_returns_do_not_change_the_reading()
    {
        string text = new NimAttemptDraft().Render().Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Empty(NimAttemptRules.Check(NimAttemptDocumentParser.Parse(text), null));
    }

    [Fact]
    public void A_malformed_time_that_holds_a_nvapi_key_shows_neither_the_prefix_nor_any_window_of_the_suffix()
    {
        string suffix = RandomBase64Url(40);
        var draft = new NimAttemptDraft { Ended = "nvapi-" + suffix };

        IReadOnlyList<string> violations = Check(draft);

        Assert.Equal(2, violations.Count);
        Assert.Contains("(not shown)", violations[0], StringComparison.Ordinal);
        Assert.DoesNotContain("nvapi-", Joined(violations), StringComparison.OrdinalIgnoreCase);
        AssertNoWindowOf(suffix, Joined(violations));
    }

    [Fact]
    public void A_malformed_time_that_holds_the_ngc_key_without_the_nvapi_prefix_is_not_echoed()
    {
        string key = RandomBase64Url(32);
        var draft = new NimAttemptDraft { Ended = key };

        IReadOnlyList<string> violations = Check(draft, key);

        Assert.Equal(2, violations.Count);
        Assert.Contains("(not shown)", violations[0], StringComparison.Ordinal);
        Assert.Contains("key material found", violations[1], StringComparison.Ordinal);
        AssertNoWindowOf(key, Joined(violations));
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    public void The_key_pattern_needs_eight_characters_after_the_prefix(int length, bool fails)
    {
        var draft = new NimAttemptDraft();
        draft.OutcomeLines.Add("The text nvapi-" + new string('a', length) + " ends here.");

        IReadOnlyList<string> violations = Check(draft);

        Assert.Equal(fails ? 1 : 0, violations.Count);
    }

    [Fact]
    public void A_key_value_shorter_than_eight_characters_counts_as_absent()
    {
        var draft = new NimAttemptDraft();
        draft.OutcomeLines.Add("The text 1234567 is ordinary.");

        Assert.Empty(Check(draft, "1234567"));
        Assert.Equal(8, NimAttemptRules.MinimumKeyLength);
    }

    [Fact]
    public void A_key_value_is_trimmed_before_it_is_searched()
    {
        string key = RandomBase64Url(24);
        var draft = new NimAttemptDraft();
        draft.OutcomeLines.Add("The key was " + key + " in the log.");

        string message = Assert.Single(Check(draft, "  " + key + "\r\n"));

        Assert.Contains("key material found", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void A_whitespace_only_key_counts_as_absent(string key) => Assert.Empty(Check(new NimAttemptDraft(), key));

    [Fact]
    public void A_login_command_continued_on_the_next_line_is_judged_as_one_command_and_named_by_its_first_line()
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines.InsertRange(0, ["docker login nvcr.io --username '$oauthtoken' --password-stdin \\", "  -p hunter2"]);
        int first = Array.FindIndex(draft.Render().Split('\n'), static line => line.StartsWith("docker login", StringComparison.Ordinal)) + 1;

        string message = Assert.Single(Check(draft));

        Assert.Contains("line " + first.ToString(CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
        Assert.Contains("--password-stdin", message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\\')]
    [InlineData('`')]
    [InlineData('^')]
    public void Every_continuation_character_joins_the_next_line(char continuation)
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines.InsertRange(0, ["docker login nvcr.io --username '$oauthtoken' --password-stdin " + continuation, "  -p hunter2"]);

        _ = Assert.Single(Check(draft));
    }

    [Fact]
    public void A_login_command_continued_correctly_passes()
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines.InsertRange(0, ["printf '%s' \"$NGC_API_KEY\" | docker login nvcr.io \\", "  --username '$oauthtoken' --password-stdin"]);

        Assert.Empty(Check(draft));
    }

    [Fact]
    public void A_logout_without_the_registry_does_not_satisfy_the_logout_rule()
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines[draft.CommandLines.IndexOf(NimAttemptDraft.LogoutLine)] = "docker logout";

        string message = Assert.Single(Check(draft));

        Assert.Contains("docker logout", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_logout_with_other_white_space_satisfies_the_logout_rule()
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines[draft.CommandLines.IndexOf(NimAttemptDraft.LogoutLine)] = "docker\tlogout  nvcr.io";

        Assert.Empty(Check(draft));
    }

    [Fact]
    public void The_box_message_rounds_the_seconds_up()
    {
        var draft = new NimAttemptDraft { Started = "2026-10-24T10:00:00.000Z", Ended = "2026-10-24T11:30:00.500Z" };

        string message = Assert.Single(Check(draft));

        Assert.Contains("90-minute box", message, StringComparison.Ordinal);
        Assert.Contains("90 minutes 1 seconds", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ended_equal_to_Started_passes() => Assert.Empty(Check(new NimAttemptDraft().Between(TimeSpan.Zero)));

    [Fact]
    public void The_first_of_two_headings_with_the_same_name_wins()
    {
        NimAttemptDocument document = NimAttemptDocumentParser.Parse("## Outcome\nfirst\n## Outcome\nsecond\n");

        Assert.Equal(["Outcome", "Outcome"], document.Headings);
        Assert.Equal("first\n", document.Sections["Outcome"]);
    }

    [Theory]
    [InlineData("2026-10-24T10:00:00.1234567Z")]
    [InlineData("2026-10-24T10:00:00.1234Z")]
    [InlineData("2026-10-24T10:00:00.12Z")]
    [InlineData("2026-10-24T10:00:00.Z")]
    public void Only_exactly_three_fraction_digits_are_a_time(string value) => Assert.Null(NimAttemptDocumentParser.ParseTime(value));

    [Fact]
    public void A_Started_line_inside_a_code_fence_is_ignored()
    {
        var draft = new NimAttemptDraft { Started = null };
        draft.CommandLines.AddRange(["```", "Started: 2026-10-24T10:00:00Z", "Box exceeded: not a statement", "```"]);

        NimAttemptDocument document = NimAttemptDocumentParser.Parse(draft.Render());
        string message = Assert.Single(NimAttemptRules.Check(document, null));

        Assert.Null(document.StartedText);
        Assert.Null(document.BoxExceededReason);
        Assert.Contains("Started:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_Decision_section_reports_only_the_heading()
    {
        var draft = new NimAttemptDraft();
        _ = draft.Headings.Remove("Decision");

        string message = Assert.Single(Check(draft));

        Assert.Contains("\"## Decision\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prose_line_that_ends_with_inline_code_is_not_a_continuation()
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines.InsertRange(0, ["The login must use `--password-stdin`", "docker login nvcr.io --username '$oauthtoken' -p hunter2"]);
        int login = Array.FindIndex(draft.Render().Split('\n'), static line => line.StartsWith("docker login", StringComparison.Ordinal)) + 1;

        string message = Assert.Single(Check(draft));

        Assert.Contains("line " + login.ToString(CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docker logout nvcr.io", true)]
    [InlineData("docker logout nvcr.io.", true)]
    [InlineData("docker logout nvcr.io.example", false)]
    [InlineData("docker logout nvcr.io-other", false)]
    public void The_logout_must_name_exactly_nvcr_io(string line, bool satisfies)
    {
        var draft = new NimAttemptDraft();
        draft.CommandLines[draft.CommandLines.IndexOf(NimAttemptDraft.LogoutLine)] = line;

        Assert.Equal(satisfies, Check(draft).Count == 0);
    }

    // ---- the skip decisions of the two fact attributes (the seams take the document path) ----

    [Fact]
    public void The_parameterless_constructors_agree_with_the_seams()
    {
        Assert.Null(new NimAttemptDocumentFactAttribute().Skip);
        Assert.Equal(
            NimDecisionIntegrationFactAttribute.SkipReasonFor(Environment.GetEnvironmentVariable(IntegrationGate.VariableName), NimAttemptDocumentParser.CommittedPath()),
            new NimDecisionIntegrationFactAttribute().Skip);
    }

    [Fact]
    public void The_document_fact_skips_with_the_story_message_while_the_document_is_absent_and_runs_when_it_exists()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Path.Combine(workspace.Root, "docs", "nim-local-attempt.md");

        Assert.Equal("docs/nim-local-attempt.md does not exist yet (AJ-103)", NimAttemptDocumentFactAttribute.SkipReasonFor(path));

        _ = workspace.WriteFile(Path.Combine("docs", "nim-local-attempt.md"), new NimAttemptDraft().Render());
        Assert.Null(NimAttemptDocumentFactAttribute.SkipReasonFor(path));
    }

    [Fact]
    public void The_decision_fact_runs_only_when_the_gate_is_open_and_the_document_decides_nim()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Path.Combine(workspace.Root, "docs", "nim-local-attempt.md");
        var nim = new NimAttemptDraft();
        nim.DecisionLines[^1] = "Local runtime: nim";

        Assert.Equal(NimAttemptDocumentFactAttribute.AbsentMessage, NimDecisionIntegrationFactAttribute.SkipReasonFor("1", path));

        _ = workspace.WriteFile(Path.Combine("docs", "nim-local-attempt.md"), nim.Render());
        Assert.Null(NimDecisionIntegrationFactAttribute.SkipReasonFor("1", path));
        Assert.Null(NimDecisionIntegrationFactAttribute.SkipReasonFor(" 1 ", path));
        Assert.Equal(IntegrationGate.SkipReason("0"), NimDecisionIntegrationFactAttribute.SkipReasonFor("0", path));
        Assert.Equal(IntegrationGate.SkipReason(null), NimDecisionIntegrationFactAttribute.SkipReasonFor(null, path));

        _ = workspace.WriteFile(Path.Combine("docs", "nim-local-attempt.md"), new NimAttemptDraft().Render());
        string? reason = NimDecisionIntegrationFactAttribute.SkipReasonFor("1", path);
        Assert.NotNull(reason);
        Assert.Contains("decides ollama, not nim", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_decision_fact_names_a_document_without_a_decision_line()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = workspace.WriteFile("nim.md", "## Decision\nNo line.\n");

        string? reason = NimDecisionIntegrationFactAttribute.DecisionSkipReason(path);

        Assert.NotNull(reason);
        Assert.Contains("decides no runtime, not nim", reason, StringComparison.Ordinal);
    }

    private static void AssertNoWindowOf(string secret, string text)
    {
        for (int start = 0; start + 8 <= secret.Length; start++)
        {
            Assert.DoesNotContain(secret.Substring(start, 8), text, StringComparison.Ordinal);
        }
    }

    // ---- the committed document ----

    [NimAttemptDocumentFact]
    public void Committed_document_satisfies_the_contract()
    {
        string text = File.ReadAllText(NimAttemptDocumentParser.CommittedPath());

        IReadOnlyList<string> violations = NimAttemptRules.Check(NimAttemptDocumentParser.Parse(text), Environment.GetEnvironmentVariable("NGC_API_KEY"));

        Assert.True(violations.Count == 0, "docs/nim-local-attempt.md breaks the contract:\n" + Joined(violations));
    }

    [NimAttemptDocumentFact]
    public void Shipped_Local_Runtime_equals_the_committed_decision()
    {
        string text = File.ReadAllText(NimAttemptDocumentParser.CommittedPath());

        string? decision = NimAttemptDocumentParser.Parse(text).DecisionRuntime;

        Assert.Equal(decision, ConfigurationFixtures.ShippedOptions().Local.Runtime, StringComparer.Ordinal);
    }
}