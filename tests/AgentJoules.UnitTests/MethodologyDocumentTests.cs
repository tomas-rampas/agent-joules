using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-112 (REQ-022): the contract of <c>docs/methodology.md</c>. The documents are built in memory (<see cref="MethodologyDocumentFixtures"/>) and checked by <see cref="MethodologyDocumentParser.Check"/> with an
/// in-memory reader of the results folder; a pass is an empty list of failures and a failure names the missing heading, string or sentence, or both quoted values. The last test runs the same contract on the committed
/// document and the repository results folder: it is red until AJ-111 commits the calibration file and AJ-112 writes the document (it is not skipped).
/// </summary>
[Trait("Spec", "REQ-022")]
public sealed class MethodologyDocumentTests
{
    private static IReadOnlyList<string> Check(string document, string? calibration = null) =>
        MethodologyDocumentParser.Check(document, MethodologyDocumentFixtures.Reader(calibration ?? MethodologyDocumentFixtures.CalibrationFile()));

    private static string Joined(IReadOnlyList<string> failures) => string.Join('\n', failures);

    // ---- the scenarios of the story ----

    [Fact]
    public void A_complete_document_passes() => Assert.Empty(Check(MethodologyDocumentFixtures.Document()));

    [Fact]
    public void The_limitations_strings_may_come_in_any_order() => Assert.Empty(Check(MethodologyDocumentFixtures.Document(reverseStrings: true)));

    [Theory]
    [InlineData("Measurement")]
    [InlineData("Attribution")]
    [InlineData("Baseline")]
    [InlineData("Labels")]
    [InlineData("Limitations")]
    public void A_missing_heading_fails_and_is_named(string heading)
    {
        IReadOnlyList<string> failures = Check(MethodologyDocumentFixtures.Document(omitHeading: heading));

        Assert.Contains(failures, failure => failure.Contains(heading, StringComparison.Ordinal));
    }

    [Fact]
    public void A_heading_that_appears_twice_fails()
    {
        string document = MethodologyDocumentFixtures.Document() + "\n## Baseline\nAgain.\n";

        Assert.Contains(Check(document), failure => failure.Contains("Baseline", StringComparison.Ordinal) && failure.Contains('2', StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GPU-only")]
    [InlineData("display")]
    [InlineData("power limit")]
    [InlineData("sampling")]
    [InlineData("offload")]
    [InlineData("hosted")]
    [InlineData("cloud")]
    public void A_missing_limitations_string_fails_and_is_named(string text)
    {
        IReadOnlyList<string> failures = Check(MethodologyDocumentFixtures.Document(omitString: text));

        string failure = Assert.Single(failures);
        Assert.Contains("\"" + text + "\"", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_outside_the_limitations_section_does_not_count()
    {
        // The Attribution section of the fixture mentions "cloud"; only the Limitations section counts.
        string document = MethodologyDocumentFixtures.Document(omitString: "cloud");

        Assert.Contains("cloud VM", document, StringComparison.Ordinal);
        Assert.Contains(Check(document), failure => failure.Contains("\"cloud\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_quoted_difference_that_differs_from_the_file_fails_and_names_both_values()
    {
        string document = MethodologyDocumentFixtures.Document(quoted: "0.5");

        string failure = Assert.Single(Check(document));
        Assert.Contains("0.5", failure, StringComparison.Ordinal);
        Assert.Contains(MethodologyDocumentFixtures.StoredDifference, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_with_a_different_number_of_trailing_zeros_fails()
    {
        // The quote is compared with the raw JSON text of the stored number, so 0.02020 is not 0.0202.
        string failure = Assert.Single(Check(MethodologyDocumentFixtures.Document(quoted: "0.02020")));

        Assert.Contains("0.02020", failure, StringComparison.Ordinal);
        Assert.Contains(MethodologyDocumentFixtures.StoredDifference, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_exponent_form_must_be_quoted_as_stored()
    {
        string stored = MethodologyDocumentFixtures.CalibrationFile("1.234E-05");

        Assert.Empty(Check(MethodologyDocumentFixtures.Document(quoted: "1.234E-05"), stored));
        Assert.NotEmpty(Check(MethodologyDocumentFixtures.Document(quoted: "0.00001234"), stored));
    }

    [Fact]
    public void A_negative_stored_value_needs_its_sign_in_the_quote()
    {
        const string stored = "-0.0202";

        Assert.Empty(Check(MethodologyDocumentFixtures.Document(quoted: stored), MethodologyDocumentFixtures.CalibrationFile(stored)));
        Assert.NotEmpty(Check(MethodologyDocumentFixtures.Document(quoted: "0.0202"), MethodologyDocumentFixtures.CalibrationFile(stored)));
    }

    [Fact]
    public void The_quote_may_sit_on_the_line_after_the_one_that_names_the_file()
    {
        string document = MethodologyDocumentFixtures.Document().Replace(" and its relativeDifference is 0.0202.", "\nwhose relativeDifference is 0.0202.", StringComparison.Ordinal);

        Assert.Empty(Check(document));
    }

    [Fact]
    public void The_digits_of_the_file_name_are_not_read_as_the_quote()
    {
        // The name comes after the word: without cutting it out, 20261025 would be the first number.
        string document = MethodologyDocumentFixtures.Document().Replace(
            "The calibration file is " + MethodologyDocumentFixtures.CalibrationName + " and its relativeDifference is 0.0202.",
            "The relativeDifference (" + MethodologyDocumentFixtures.CalibrationName + ") is 0.0202.",
            StringComparison.Ordinal);

        Assert.Contains("relativeDifference (" + MethodologyDocumentFixtures.CalibrationName + ")", document, StringComparison.Ordinal);
        Assert.Empty(Check(document));
    }

    [Fact]
    public void A_number_before_the_word_is_not_the_quote()
    {
        string document = MethodologyDocumentFixtures.Document().Replace("The calibration file is ", "In 2 ways, the calibration file is ", StringComparison.Ordinal);

        Assert.Empty(Check(document));
        Assert.NotEmpty(Check(MethodologyDocumentFixtures.Document(quoted: "0.5").Replace("The calibration file is ", "In 0.0202 ways, the calibration file is ", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_same_file_named_twice_is_one_file() =>
        Assert.Empty(Check(MethodologyDocumentFixtures.Document() + "\nAgain: " + MethodologyDocumentFixtures.CalibrationName + "\n"));

    [Fact]
    public void A_file_name_with_a_trailing_character_is_not_a_calibration_file_name()
    {
        string document = MethodologyDocumentFixtures.Document().Replace(MethodologyDocumentFixtures.CalibrationName, MethodologyDocumentFixtures.CalibrationName + "x", StringComparison.Ordinal);

        Assert.Contains(Check(document), static failure => failure.Contains("the calibration file must be named", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_name_followed_by_a_full_stop_is_a_calibration_file_name()
    {
        string document = MethodologyDocumentFixtures.Document().Replace(
            "The calibration file is " + MethodologyDocumentFixtures.CalibrationName + " and its relativeDifference is 0.0202.",
            "Its relativeDifference is 0.0202; the file is " + MethodologyDocumentFixtures.CalibrationName + ".",
            StringComparison.Ordinal);

        Assert.Empty(Check(document));
    }

    [Fact]
    public void No_named_calibration_file_fails_and_says_so()
    {
        IReadOnlyList<string> failures = Check(MethodologyDocumentFixtures.Document(nameFile: false));

        Assert.Contains(failures, failure => failure.Contains("the calibration file must be named", StringComparison.Ordinal));
    }

    [Fact]
    public void A_named_file_that_does_not_exist_fails_and_names_it()
    {
        IReadOnlyList<string> failures = MethodologyDocumentParser.Check(MethodologyDocumentFixtures.Document(), static _ => null);

        Assert.Contains(failures, failure => failure.Contains(MethodologyDocumentFixtures.CalibrationName, StringComparison.Ordinal));
    }

    [Fact]
    public void Two_different_calibration_files_fail()
    {
        string document = MethodologyDocumentFixtures.Document() + "\nOlder: results/calibration-20261001T000000Z.json\n";

        Assert.Contains(Check(document), failure => failure.Contains("exactly one", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stored_null_requires_the_quote_of_N_A_with_the_stored_reason()
    {
        string stored = MethodologyDocumentFixtures.CalibrationFile(difference: null, reason: "counter-zero");

        Assert.Empty(Check(MethodologyDocumentFixtures.Document(quoted: null, quotedReason: "counter-zero"), stored));
        Assert.Contains(Check(MethodologyDocumentFixtures.Document(quoted: null, quotedReason: "no-samples"), stored), failure => failure.Contains("counter-zero", StringComparison.Ordinal));
        Assert.NotEmpty(Check(MethodologyDocumentFixtures.Document(quoted: "0.0202"), stored));
    }

    [Theory]
    [InlineData("The dashboard server adds no GPU work.")]
    [InlineData("A browser tab adds compositing load, so published runs are recorded without one.")]
    [InlineData("Estimates shown on the live dashboard never enter results files.")]
    public void A_missing_dashboard_sentence_fails_and_is_named(string sentence)
    {
        IReadOnlyList<string> failures = Check(MethodologyDocumentFixtures.Document(omitSentence: sentence));

        string failure = Assert.Single(failures);
        Assert.Contains(sentence, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sentence_wrapped_over_two_lines_still_counts()
    {
        string document = MethodologyDocumentFixtures.Document().Replace("adds no GPU work", "adds no\nGPU work", StringComparison.Ordinal);

        Assert.Empty(Check(document));
    }

    [Fact]
    public void A_dashboard_sentence_that_appears_twice_fails()
    {
        string document = MethodologyDocumentFixtures.Document().Replace("## Attribution\n", "## Attribution\nThe dashboard server adds no GPU work.\n", StringComparison.Ordinal);

        string failure = Assert.Single(Check(document));
        Assert.Contains("adds no GPU work", failure, StringComparison.Ordinal);
        Assert.Contains("2 times", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dashboard_sentence_outside_the_limitations_section_fails()
    {
        string document = MethodologyDocumentFixtures.Document(omitSentence: MethodologyDocumentParser.CompositingSentence)
            .Replace("## Attribution\n", "## Attribution\n" + MethodologyDocumentParser.CompositingSentence + "\n", StringComparison.Ordinal);

        string failure = Assert.Single(Check(document));
        Assert.Contains("Limitations section lacks the sentence", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dashboard_sentence_under_a_later_heading_of_the_same_level_does_not_count()
    {
        // The Limitations section ends at the next heading of its level, so the sentence that follows "## Dashboard" is outside it.
        string document = MethodologyDocumentFixtures.Document().Replace("The dashboard server adds no GPU work.\n", "## Dashboard\nThe dashboard server adds no GPU work.\n", StringComparison.Ordinal);

        Assert.Contains(Check(document), static failure => failure.Contains("adds no GPU work", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dashboard_sentence_under_a_subheading_of_limitations_counts()
    {
        string document = MethodologyDocumentFixtures.Document().Replace("The dashboard server adds no GPU work.\n", "### Dashboard\nThe dashboard server adds no GPU work.\n", StringComparison.Ordinal);

        Assert.Empty(Check(document));
    }

    [Fact]
    public void A_missing_verified_line_fails()
    {
        Assert.Contains(Check(MethodologyDocumentFixtures.Document(verified: false)), failure => failure.Contains("Verified:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_verified_label_inside_a_line_is_not_a_verified_line()
    {
        string document = MethodologyDocumentFixtures.Document(verified: false).Replace("# Methodology\n", "# Methodology\nLast Verified: 2026-10-25\n", StringComparison.Ordinal);

        Assert.Contains(Check(document), static failure => failure.Contains("Verified:", StringComparison.Ordinal));
    }

    [Fact]
    public void Too_few_measurement_terms_fail()
    {
        string document = MethodologyDocumentFixtures.Document()
            .Replace("millijoule", "mJ", StringComparison.Ordinal)
            .Replace("milliwatt", "mW", StringComparison.Ordinal);

        IReadOnlyList<string> failures = Check(document);

        Assert.Contains(failures, failure => failure.Contains("Measurement", StringComparison.Ordinal) && failure.Contains('4', StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("\"millijoule\"", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("\"milliwatt\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("millijoule")]
    [InlineData("milliwatt")]
    [InlineData("sample rate")]
    [InlineData("HTTP call")]
    public void Each_measurement_term_is_required_and_named(string term)
    {
        // The line that held the term no longer matches, so the count of lines falls to three as well.
        string document = MethodologyDocumentFixtures.Document().Replace(term, "thing", StringComparison.Ordinal);

        IReadOnlyList<string> failures = Check(document);

        Assert.Contains(failures, failure => failure.Contains("lacks the term \"" + term + "\"", StringComparison.Ordinal));
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public void The_measurement_terms_are_matched_without_regard_to_case() =>
        Assert.Empty(Check(MethodologyDocumentFixtures.Document().Replace("millijoule", "MILLIJOULE", StringComparison.Ordinal).Replace("HTTP call", "http call", StringComparison.Ordinal)));

    [Fact]
    public void Four_lines_with_a_term_pass_and_three_do_not()
    {
        // The document holds all four terms on four lines; two terms on one line leave three lines (grep -ciE counts lines).
        Assert.Empty(Check(MethodologyDocumentFixtures.Document()));

        string document = MethodologyDocumentFixtures.Document().Replace("NVML reports the energy counter in millijoule units.\nNVML reports the power draw in milliwatt units.\n", "NVML reports millijoule and milliwatt units.\n", StringComparison.Ordinal);

        string failure = Assert.Single(Check(document));
        Assert.Contains("3 lines", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_term_repeated_on_one_line_counts_that_line_once()
    {
        string document = MethodologyDocumentFixtures.Document()
            .Replace("NVML reports the energy counter in millijoule units.\n", "millijoule millijoule millijoule millijoule\n", StringComparison.Ordinal)
            .Replace("NVML reports the power draw in milliwatt units.\n", string.Empty, StringComparison.Ordinal)
            .Replace("The sample rate of the sampling method is 10 Hz.\n", string.Empty, StringComparison.Ordinal)
            .Replace("A window opens just before the HTTP call and closes just after it.\n", string.Empty, StringComparison.Ordinal);

        IReadOnlyList<string> failures = Check(document);

        Assert.Contains(failures, static failure => failure.Contains("1 lines", StringComparison.Ordinal));
        Assert.Equal(4, failures.Count);
    }

    [Fact]
    public void Terms_outside_the_measurement_section_do_not_count()
    {
        string document = MethodologyDocumentFixtures.Document()
            .Replace("millijoule", "mJ", StringComparison.Ordinal)
            .Replace("milliwatt", "mW", StringComparison.Ordinal)
            .Replace("## Attribution\n", "## Attribution\nmillijoule and milliwatt.\nmillijoule.\nmilliwatt.\n", StringComparison.Ordinal);

        Assert.Contains(Check(document), static failure => failure.Contains("Measurement section", StringComparison.Ordinal));
    }

    [Fact]
    public void A_term_that_appears_only_outside_the_measurement_section_is_missing()
    {
        // Measurement keeps four matching lines but lacks "HTTP call", which appears only in Attribution: the presence rule is pinned to the section.
        string document = MethodologyDocumentFixtures.Document()
            .Replace("A window opens just before the HTTP call and closes just after it.\n", "A window opens before the request; the sample rate is fixed.\n", StringComparison.Ordinal)
            .Replace("## Attribution\n", "## Attribution\nThe HTTP call is the unit of attribution.\n", StringComparison.Ordinal);

        string failure = Assert.Single(Check(document));

        Assert.Contains("lacks the term \"HTTP call\"", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matching_line_outside_the_measurement_section_does_not_count()
    {
        // Measurement holds all four terms on three lines; a fourth matching line sits in Attribution: the line count is pinned to the section.
        string document = MethodologyDocumentFixtures.Document()
            .Replace("NVML reports the energy counter in millijoule units.\nNVML reports the power draw in milliwatt units.\n", "NVML reports millijoule and milliwatt units.\n", StringComparison.Ordinal)
            .Replace("## Attribution\n", "## Attribution\nNo millijoule is attributed by correlation.\n", StringComparison.Ordinal);

        string failure = Assert.Single(Check(document));

        Assert.Contains("the Measurement section has 3 lines", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subsection_of_measurement_belongs_to_it() =>
        Assert.Empty(Check(MethodologyDocumentFixtures.Document().Replace("The sample rate of", "### Rates\nThe sample rate of", StringComparison.Ordinal)));

    [Fact]
    public void An_existing_sentence_survives_the_merge_and_the_headings_exist()
    {
        const string existing = "The meter reads the NVML counter once per window.";
        string merged = MethodologyDocumentFixtures.Document(existing: existing);

        Assert.Empty(MethodologyDocumentParser.LostSentences(existing, merged));
        Assert.DoesNotContain(Check(merged), failure => failure.Contains("heading", StringComparison.Ordinal));
    }

    [Fact]
    public void A_sentence_wrapped_over_two_lines_is_not_lost_in_the_merge()
    {
        const string before = "The meter reads the NVML counter once per window.";
        string after = MethodologyDocumentFixtures.Document(existing: "The meter reads the NVML\ncounter once per window.");

        Assert.Empty(MethodologyDocumentParser.LostSentences(before, after));
    }

    [Fact]
    public void A_limitations_string_in_a_different_case_does_not_count()
    {
        string document = MethodologyDocumentFixtures.Document().Replace("- The cloud tier is a dedicated VM GPU.", "- The Cloud tier is a dedicated VM GPU.", StringComparison.Ordinal);

        Assert.Contains(Check(document), static failure => failure.Contains("\"cloud\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_sentence_lost_in_the_merge_is_named()
    {
        const string existing = "The meter reads the NVML counter once per window.";

        string lost = Assert.Single(MethodologyDocumentParser.LostSentences(existing, MethodologyDocumentFixtures.Document()));
        Assert.Equal(existing, lost);
    }

    // ---- the committed document ----

    [Fact]
    public void The_committed_methodology_document_passes_the_contract()
    {
        string path = MethodologyDocumentParser.CommittedPath();
        Assert.True(File.Exists(path), "docs/methodology.md does not exist yet (AJ-112 writes it)");

        string root = ConfigurationFixtures.RepositoryRoot();
        IReadOnlyList<string> failures = MethodologyDocumentParser.Check(
            File.ReadAllText(path),
            relative =>
            {
                string full = Path.Combine(root, relative);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            });

        Assert.True(failures.Count == 0, Joined(failures));

        // The merge helper runs on the real file too: nothing is lost against an empty earlier document.
        Assert.Empty(MethodologyDocumentParser.LostSentences(string.Empty, File.ReadAllText(path)));
    }
}