using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// The evidence contract of <c>docs/methodology.md</c> (the final M4 review, M-1, m-1 and m-4; REQ-022, prompt section 6 honesty rule 1): every quoted number is read from a committed file under <c>results/</c>, so a
/// document that cites a file that is not committed, or quotes a value the cited file does not store, fails. The committed document and the committed files are read as they are; each mutation test changes one quote
/// of the committed document and expects the one rule it breaks to name both values. The committed view of <c>results/</c> is the HEAD tree (<c>git ls-tree -r --name-only HEAD -- results/</c>); a working-tree or staged file does not count.
/// </summary>
[Trait("Spec", "REQ-022")]
public sealed class MethodologyEvidenceTests
{
    private const string LocalRun = "results/20261025T131930Z-local.json";
    private const string CampaignProbe = "results/probe-20261025T131800Z.json";

    private static string Document() => File.ReadAllText(MethodologyDocumentParser.CommittedPath());

    private static IReadOnlyList<string> Evidence(string document, IReadOnlyCollection<string>? committed = null)
    {
        string root = ConfigurationFixtures.RepositoryRoot();
        return MethodologyDocumentParser.CheckEvidence(
            document,
            committed ?? LaptopResults.CommittedResultsFiles(),
            relative =>
            {
                string full = Path.Combine(root, relative);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            },
            MethodologyDocumentParser.TaskCaps());
    }

    private static string Mutated(string from, string to)
    {
        string document = Document();
        Assert.Contains(from, document, StringComparison.Ordinal);
        return document.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void The_committed_methodology_document_passes_the_evidence_contract() =>
        Assert.True(Evidence(Document()).Count == 0, string.Join('\n', Evidence(Document())));

    // ---- rule 1: every cited results file is committed ----

    [Fact]
    public void A_cited_results_file_that_is_not_committed_fails_and_is_named()
    {
        // The file the first M4 draft cited and git never held.
        string document = Mutated("results/20261025T131930Z-local.json, started", "results/20261025T024349Z-local.json, started");

        Assert.Contains(Evidence(document), static failure => failure.Contains("results/20261025T024349Z-local.json", StringComparison.Ordinal) && failure.Contains("not a committed file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_file_named_without_its_folder_is_the_same_citation()
    {
        string document = Mutated("results/20261025T131930Z-local.json, started", "20261025T024349Z-local.json, started");

        Assert.Contains(Evidence(document), static failure => failure.Contains("results/20261025T024349Z-local.json", StringComparison.Ordinal) && failure.Contains("not a committed file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_exists_only_in_the_working_tree_does_not_satisfy_the_citation()
    {
        // The campaign probe is on disk, but a committed view without it must reject the document.
        string[] committed = [.. LaptopResults.CommittedResultsFiles().Where(static path => !string.Equals(path, CampaignProbe, StringComparison.Ordinal))];

        Assert.Contains(Evidence(Document(), committed), static failure => failure.Contains(CampaignProbe, StringComparison.Ordinal) && failure.Contains("not a committed file", StringComparison.Ordinal));
    }

    [Fact]
    public void The_committed_view_of_results_holds_the_published_campaign_files()
    {
        IReadOnlyList<string> committed = LaptopResults.CommittedResultsFiles();

        Assert.Contains(LocalRun, committed);
        Assert.Contains(CampaignProbe, committed);
    }

    // ---- rule 2: the baseline-unstable example and the EDGE-013 statement, read from the cited run file ----

    [Theory]
    [InlineData("mean 20.109 W", "mean 16.453 W", "16.453", "20.109")]
    [InlineData("standard deviation 6.971 W", "standard deviation 8.559 W", "8.559", "6.971")]
    [InlineData("302 samples over 30.003 s", "302 samples over 30.013 s", "30.013", "30.003")]
    [InlineData("coefficient of variation 0.347", "coefficient of variation 0.520", "0.520", "0.347")]
    public void The_baseline_example_quotes_the_run_file_rounded_to_three_decimals(string from, string to, string quoted, string stored)
    {
        string failure = Assert.Single(Evidence(Mutated(from, to)));

        Assert.Contains("baseline-unstable example", failure, StringComparison.Ordinal);
        Assert.Contains(quoted, failure, StringComparison.Ordinal);
        Assert.Contains(stored, failure, StringComparison.Ordinal);
        Assert.Contains("rounded to three decimals", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("on 36 of its 36 steps", "on 12 of its 36 steps", "12", "36")]
    [InlineData("with the reason SwPowerCap", "with the reason HwSlowdown", "HwSlowdown", "SwPowerCap")]
    [InlineData("peak power 95.4 to 106.1 W", "peak power 95.8 to 106.1 W", "95.8", "95.4")]
    [InlineData("peak power 95.4 to 106.1 W", "peak power 95.4 to 107.8 W", "107.8", "106.1")]
    [InlineData("its embedded probe recorded powerLimitW 105;", "its embedded probe recorded powerLimitW 95;", "95", "105")]
    public void The_throttled_statement_quotes_the_run_file_with_peaks_rounded_to_one_decimal(string from, string to, string quoted, string stored)
    {
        string failure = Assert.Single(Evidence(Mutated(from, to)));

        Assert.Contains("EDGE-013 statement", failure, StringComparison.Ordinal);
        Assert.Contains(quoted, failure, StringComparison.Ordinal);
        Assert.Contains(stored, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void The_baseline_example_must_cite_the_latest_committed_local_run()
    {
        string document = Mutated("Measured example (the published local run results/20261025T131930Z-local.json,", "Measured example (the published local run,");

        Assert.Contains(Evidence(document), static failure => failure.Contains("baseline-unstable example", StringComparison.Ordinal) && failure.Contains(LocalRun, StringComparison.Ordinal));
    }

    // ---- rule 3: the power limit quotes ----

    [Fact]
    public void The_power_limit_item_quotes_the_embedded_probe_of_the_latest_local_run_as_the_run_time_value()
    {
        string failure = Assert.Single(Evidence(Mutated("records powerLimitW 105 (the run-time value)", "records powerLimitW 87.393 (the run-time value)")));

        Assert.Contains("power limit item run-time value", failure, StringComparison.Ordinal);
        Assert.Contains("87.393", failure, StringComparison.Ordinal);
        Assert.Contains("105", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void The_power_limit_item_quotes_the_committed_campaign_probe()
    {
        string failure = Assert.Single(Evidence(Mutated("records powerLimitW 96.39;", "records powerLimitW 105;")));

        Assert.Contains(CampaignProbe, failure, StringComparison.Ordinal);
        Assert.Contains("96.39", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void The_power_limit_item_must_name_the_latest_committed_probe()
    {
        string document = Mutated("the campaign probe results/probe-20261025T131800Z.json, saved before the run at 13:18:00Z, records powerLimitW 96.39;", "the campaign probe records 96.39;");

        Assert.Contains(Evidence(document), static failure => failure.Contains("does not name the latest committed probe", StringComparison.Ordinal) && failure.Contains(CampaignProbe, StringComparison.Ordinal));
    }

    [Fact]
    public void The_offload_item_quotes_the_run_file()
    {
        string failure = Assert.Single(Evidence(Mutated("gpuOffloadPercent 100 in", "gpuOffloadPercent 90 in")));

        Assert.Contains("offload item", failure, StringComparison.Ordinal);
    }

    // ---- rule 4: the REQ-032 sentence stays and the open applications sentence follows it ----

    [Fact]
    public void The_compositing_sentence_is_followed_by_the_open_applications_sentence()
    {
        Assert.Empty(Evidence(Document()));
        Assert.Contains(MethodologyDocumentParser.CompositingSentence + "\n" + MethodologyDocumentParser.OpenApplicationsSentence + "\n", Document(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_without_the_open_applications_sentence_fails()
    {
        string document = Mutated(MethodologyDocumentParser.OpenApplicationsSentence + "\n", string.Empty);

        Assert.Contains(Evidence(document), static failure => failure.Contains("open applications sentence", StringComparison.Ordinal));
    }

    [Fact]
    public void The_open_applications_sentence_must_follow_the_compositing_sentence_not_precede_it()
    {
        string document = Mutated(
            MethodologyDocumentParser.CompositingSentence + "\n" + MethodologyDocumentParser.OpenApplicationsSentence + "\n",
            MethodologyDocumentParser.OpenApplicationsSentence + "\n" + MethodologyDocumentParser.CompositingSentence + "\n");

        Assert.Contains(Evidence(document), static failure => failure.Contains("open applications sentence", StringComparison.Ordinal));
    }

    // ---- rule 5: the output caps ----

    [Theory]
    [InlineData("21 of 36 local steps", "18 of 36 local steps", "18", "21")]
    [InlineData("6 of 36 nebius steps", "12 of 36 nebius steps", "12", "6")]
    [InlineData("6 of 36 nvidia steps", "12 of 36 nvidia steps", "12", "6")]
    [InlineData("incident-brief 512", "incident-brief 500", "500", "512")]
    [InlineData("energy-explainer 384", "energy-explainer 400", "400", "384")]
    public void The_output_caps_item_recomputes_the_counts_and_caps_from_the_committed_files(string from, string to, string quoted, string stored)
    {
        string failure = Assert.Single(Evidence(Mutated(from, to)));

        Assert.Contains("output caps item", failure, StringComparison.Ordinal);
        Assert.Contains(quoted, failure, StringComparison.Ordinal);
        Assert.Contains(stored, failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("judged as returned")]
    [InlineData("carries no flag")]
    [InlineData("finish-reason flag")]
    public void The_output_caps_item_discloses_that_capped_answers_are_judged_unflagged(string phrase) =>
        Assert.Contains(Evidence(Mutated(phrase, "x")), failure => failure.Contains("lacks the phrase \"" + phrase + "\"", StringComparison.Ordinal));

    // ---- rule 6: the settle interval ----

    [Fact]
    public void The_baseline_section_holds_the_settle_sentence()
    {
        Assert.Contains(MethodologyDocumentParser.BaselineSettleSentence, Document(), StringComparison.Ordinal);

        string document = Mutated(MethodologyDocumentParser.BaselineSettleSentence, "The baseline window starts after the warm-up.");

        Assert.Contains(Evidence(document), static failure => failure.Contains("settle sentence", StringComparison.Ordinal));
    }
}