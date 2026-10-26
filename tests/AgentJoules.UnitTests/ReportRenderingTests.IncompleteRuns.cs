using System.Text.Json;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Json;
using AgentJoules.Reporting.Readme;
using AgentJoules.Reporting.Render;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class ReportRenderingTests
{
    // ---- AJ-123 (EDGE-016): a cloudgpu file that is not a result renders as a tier that was not run ----

    // The rig is disposed when the run or one of the checks throws, and the incomplete run is proved to be one (exit 5, status incomplete) before a test relies on it.
    private static async Task<(CloudGpuBudgetRig Rig, string Folder)> IncompleteCloudGpuRunAsync()
    {
        CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
        try
        {
            rig.Cloud.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.Ok(), FakeAnswer.StreamFailure(1), FakeAnswer.Ok(), FakeAnswer.Ok());
            CliRunResult result = await rig.RunAsync("run --tier cloudgpu --repeat 1 --task incident-brief");
            Assert.True(result.ExitCode == 5, $"Expected exit code 5 but got {result.ExitCode}; stderr: {result.Stderr}");
            using (JsonDocument document = rig.ReadRunFile())
            {
                Assert.Equal("incomplete", document.RootElement.GetProperty("run").GetProperty("status").GetString());
            }

            return (rig, Path.GetDirectoryName(Assert.Single(rig.RunFiles()))!);
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    // The runs the report command would render for the selection: the newest accepted file of each tier and variant, read as run documents.
    private static List<SelectedRun> SelectedRuns(RunFileSelection selection)
    {
        List<SelectedRun> runs = [];
        foreach ((RunVariant variant, string path) in selection.Latest.Values.Select(static path => (RunVariant.Primary, path)).Concat(selection.LatestBaseline.Values.Select(static path => (RunVariant.Baseline, path))))
        {
            RunDocument document = JsonSerializer.Deserialize(File.ReadAllText(path), AgentJoulesJsonContext.Default.RunDocument) ?? throw new InvalidDataException("The run file holds no document.");
            runs.Add(new SelectedRun(variant, Path.GetFileName(path), document));
        }

        return runs;
    }

    [Fact]
    [Trait("Spec", "EDGE-016")]
    public async Task A_report_whose_only_cloudgpu_file_is_incomplete_renders_not_run_and_no_step_row()
    {
        (CloudGpuBudgetRig rig, _) = await IncompleteCloudGpuRunAsync();
        using (rig)
        {
            string summary = await rig.SummaryAsync();

            Assert.Equal([NotRunRow("local"), NotRunRow("cloudgpu"), NotRunRow("nvidia"), NotRunRow("nebius")], DataRows(summary));
            Assert.Contains("- cloudgpu: not run", BulletLines(summary));
            Assert.DoesNotContain("incomplete", summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Spec", "EDGE-016")]
    public async Task The_readme_block_excludes_the_incomplete_cloudgpu_file()
    {
        (CloudGpuBudgetRig rig, string folder) = await IncompleteCloudGpuRunAsync();
        using (rig)
        {
            RunFileSelection selection = RunFileSelector.Select([folder]);

            Assert.DoesNotContain(Tier.CloudGpu, selection.Latest.Keys);
            Assert.Contains(selection.Skipped, skip => skip.Reason == "status-incomplete");

            // The block is built from what the selection would hand to the report: were the incomplete file among the selected runs, the cloudgpu row would not read "not run".
            string block = ReadmeResultsBlock.Render(ReportAggregator.Build(SelectedRuns(selection)), dashboardScreenshotExists: false);
            Assert.Contains("| cloudgpu | not run |", block, StringComparison.Ordinal);
            Assert.DoesNotContain("incomplete", block, StringComparison.Ordinal);

            // The control: the builder itself refuses the incomplete file when it is handed over as a selected run, so a selection that let it through would end the report instead of rendering it.
            string incompleteFile = Assert.Single(rig.RunFiles());
            RunDocument incomplete = JsonSerializer.Deserialize(File.ReadAllText(incompleteFile), AgentJoulesJsonContext.Default.RunDocument)!;
            _ = Assert.Throws<ArgumentException>(() => ReportAggregator.Build([new SelectedRun(RunVariant.Primary, Path.GetFileName(incompleteFile), incomplete)]));
        }
    }
}