using System.Text.Json.Nodes;

using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit;

using static AgentJoules.TestSupport.ReportRunFixtures;

namespace AgentJoules.UnitTests;

public sealed partial class ReadmeBlockTests
{
    /// <summary>
    /// AJ-129 (REQ-031, EDGE-017): the README block and the tagline rule follow the real state of the cloudgpu tier. The block rows are proven on fixtures (the live README block is generated in M6); the tagline rule and the
    /// story check run on scratch texts.
    /// </summary>
    [Trait("Spec", "EDGE-017")]
    [Trait("Spec", "REQ-031")]
    public sealed class CloudGpuConsequences
    {
        private const string Story = "# Story\n\nThe cloud tier runs on an H100. The laptop tier runs on an RTX GPU. Energy is measured on both.\n";
        private const string ChangedStory = "# Story\n\nThe cloud tier runs on an L40S. The laptop tier runs on an RTX GPU. Energy is measured on both.\n";

        private static string CloudBlock(RunStatus status, bool sampling = false, string gpuName = CloudGpuConsequenceFixtures.L40S) =>
            Block(Wrap(CloudGpuConsequenceFixtures.Document(status, gpuName, sampling)));

        private static string Tagline(CloudGpuTierState state) =>
            CloudGpuTaglineRule.Tagline(state, CloudGpuConsequenceFixtures.CandidateA, CloudGpuConsequenceFixtures.CandidateALocal);

        [Fact]
        public void A_completed_file_naming_an_L40S_keeps_candidate_A()
        {
            CloudGpuTierState state = CloudGpuConsequenceFixtures.StateOf(RunStatus.Completed);

            Assert.Equal(("complete", CloudGpuConsequenceFixtures.FileName, "completed"), (state.State, state.SelectedFile, state.Status));
            Assert.Equal(CloudGpuConsequenceFixtures.CandidateA, Tagline(state));
        }

        [Fact]
        public void A_partial_file_keeps_candidate_A_and_the_row_says_partial()
        {
            CloudGpuTierState state = CloudGpuConsequenceFixtures.StateOf(RunStatus.Partial);
            string[] row = RowOf(CloudBlock(RunStatus.Partial), "cloudgpu");

            Assert.Equal("partial", state.State);
            Assert.Equal(CloudGpuConsequenceFixtures.CandidateA, Tagline(state));
            Assert.Equal("partial", row[Column("status")]);
            Assert.Contains("partial", row[Column("flags")], StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(RunStatus.Completed, "completed")]
        [InlineData(RunStatus.Partial, "partial")]
        public void Another_GPU_name_replaces_the_L40S_token_of_candidate_A(RunStatus status, string expectedStatus)
        {
            CloudGpuTierState state = CloudGpuConsequenceFixtures.StateOf(status, "NVIDIA H100 80GB");

            string tagline = Tagline(state);

            Assert.Equal(expectedStatus, state.Status);
            Assert.Equal(CloudGpuConsequenceFixtures.CandidateA.Replace("L40S", "NVIDIA H100 80GB", StringComparison.Ordinal), tagline);
            Assert.DoesNotContain("L40S", tagline, StringComparison.Ordinal);
        }

        [Fact]
        public void A_changed_sentence_with_a_build_log_entry_has_no_problem()
        {
            const string logged = "- AJ-129: story sentence changed. Old: \"The cloud tier runs on an H100. The laptop\" New: L40S. Proof: results/x-cloudgpu.json\n";

            Assert.Empty(CloudGpuTaglineRule.ConsequenceProblems(ChangedStory, Story, logged));
            Assert.Empty(CloudGpuTaglineRule.ConsequenceProblems(Story, Story, string.Empty));
        }

        [Fact]
        public void No_selectable_file_selects_candidate_A_local_and_the_row_says_not_run()
        {
            CloudGpuTierState state = CloudGpuTaglineRule.Evaluate([], _ => throw new InvalidOperationException("no file is read"));
            string block = Block(CommittedRun(Tier.Local), CommittedRun(Tier.Nvidia));

            Assert.Equal(new CloudGpuTierState("absent", null, null, null), state);
            Assert.Equal(CloudGpuConsequenceFixtures.CandidateALocal, Tagline(state));
            Assert.Contains("| cloudgpu | not run |", block, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(RunStatus.Incomplete)]
        [InlineData(RunStatus.Running)]
        [InlineData(RunStatus.Failed)]
        public void A_file_that_is_not_a_result_counts_as_absent(RunStatus status)
        {
            CloudGpuTierState state = CloudGpuConsequenceFixtures.StateOf(status);

            Assert.Equal("absent", state.State);
            Assert.Equal(CloudGpuConsequenceFixtures.CandidateALocal, Tagline(state));
        }

        [Fact]
        public void The_state_follows_the_selector_rules_for_names_flags_and_age()
        {
            const string newer = "20261025T090000Z-cloudgpu.json";
            string completed = CloudGpuConsequenceFixtures.Json(RunStatus.Completed);
            string incomplete = CloudGpuConsequenceFixtures.Json(RunStatus.Incomplete);
            string nonPublishable = RunDocumentFixtures.WithRunFlagText(completed, "non-publishable");
            Assert.NotEqual(completed, nonPublishable);

            // The newest file that is selectable wins; a newer incomplete file does not hide an older completed one.
            Assert.Equal(CloudGpuConsequenceFixtures.FileName, CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName, newer], name => name == newer ? incomplete : completed).SelectedFile);

            // A non-publishable file, a baseline file, a file of another tier, a name that is no run file name and text that is no run file count as none.
            Assert.Equal("absent", CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => nonPublishable).State);
            Assert.Equal("absent", CloudGpuTaglineRule.Evaluate(["20261024T090000Z-cloudgpu-baseline.json"], _ => completed).State);
            Assert.Equal("absent", CloudGpuTaglineRule.Evaluate(["20261024T090000Z-local.json"], _ => completed).State);
            Assert.Equal("absent", CloudGpuTaglineRule.Evaluate(["probe-cloudgpu.json"], _ => completed).State);
            Assert.Equal("absent", CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => "not json").State);
        }

        [Fact]
        public void A_changed_sentence_without_an_entry_fails_and_names_the_sentence()
        {
            IReadOnlyList<string> problems = CloudGpuTaglineRule.ConsequenceProblems(ChangedStory, Story, "- AJ-129: nothing about the sentence\n- AJ-128: The cloud tier runs on an H100. The laptop\n");

            string problem = Assert.Single(problems);
            Assert.Contains("\"The cloud tier runs on an H100.\"", problem, StringComparison.Ordinal);
        }

        [Fact]
        public void An_entry_must_name_the_task_and_the_start_of_the_old_sentence()
        {
            Assert.Single(CloudGpuTaglineRule.ConsequenceProblems(ChangedStory, Story, "- AJ-130: The cloud tier runs on an H100.\n"));
            Assert.Single(CloudGpuTaglineRule.ConsequenceProblems(ChangedStory, Story, "- AJ-129: The cloud tier runs on something.\n"));

            // The old start without the proving file (a -cloudgpu.json name or a results/ path) is no entry; with either it is.
            Assert.Single(CloudGpuTaglineRule.ConsequenceProblems(ChangedStory, Story, "- AJ-129: story sentence changed. Old: \"The cloud tier runs on an H100. The laptop\"\n"));
            Assert.Empty(CloudGpuTaglineRule.ConsequenceProblems(ChangedStory, Story, "- AJ-129: Old: \"The cloud tier runs on an H100. The laptop\" Proof: 20261024T090000Z-cloudgpu.json\n"));
        }

        [Fact]
        public void A_filled_placeholder_and_the_checklist_section_need_no_entry()
        {
            const string pre = "The GPU is [[FILL: gpu | source: results]]. The laptop is fixed.\n\n## Fill-after-build checklist\n\nCheck the GPU row first.\n";
            const string filled = "The GPU is an L40S. The laptop is fixed.\n";

            Assert.Empty(CloudGpuTaglineRule.ConsequenceProblems(filled, pre, string.Empty));
        }

        [PreDraftedStoryFact]
        public void The_real_story_differs_from_the_pre_drafted_one_only_with_a_log_entry()
        {
            string root = ConfigurationFixtures.RepositoryRoot();
            string pre = CloudGpuTaglineRule.PreDraftedStory(root) ?? throw new Xunit.Sdk.XunitException("The pre-drafted story cannot be read at the pinned commit.");

            string story = File.ReadAllText(Path.Combine(root, "docs", "devpost-story.md"));
            string log = File.ReadAllText(Path.Combine(root, "docs", "build-log.md"));

            Assert.Empty(CloudGpuTaglineRule.ConsequenceProblems(story, pre, log));
        }

        [Fact]
        public void A_changed_tagline_needs_an_entry_that_names_the_change_and_stays_within_200_characters()
        {
            string changed = CloudGpuConsequenceFixtures.CandidateA.Replace("L40S", "H100", StringComparison.Ordinal);
            string key = CloudGpuConsequenceFixtures.CandidateA[..40];

            // Unchanged: no entry needed.
            Assert.Empty(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, CloudGpuConsequenceFixtures.CandidateA, string.Empty));

            // Changed without an entry, or with an entry that lacks the task or the old text, fails and names the change.
            Assert.Contains("the tagline changed", Assert.Single(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, changed, string.Empty)), StringComparison.Ordinal);
            Assert.Single(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, changed, "- AJ-128: " + key + "\n"));
            Assert.Single(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, changed, "- AJ-129: another sentence\n"));

            // Candidate A and A-local share their first 55 characters, so the old text alone is no entry: the line also needs the replaced GPU name or the proving file.
            Assert.Single(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, changed, "- AJ-129: tagline changed. Old: \"" + key + "\"\n"));
            Assert.Single(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, changed, "- AJ-129: Old: \"" + key + "\" New: another GPU\n"));

            // Changed with a matching entry passes: the old start and the GPU name, or the old start and the proving file.
            Assert.Empty(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, changed, "- AJ-129: tagline changed. Old: \"" + key + "\" New: H100\n"));
            Assert.Empty(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, changed, "- AJ-129: tagline changed. Old: \"" + key + "\" Proof: results/" + CloudGpuConsequenceFixtures.FileName + "\n"));
        }

        [Fact]
        public void A_changed_tagline_over_200_characters_is_a_problem_even_with_an_entry()
        {
            string longer = CloudGpuConsequenceFixtures.CandidateA + new string('x', CloudGpuTaglineRule.TaglineLimit);
            string logged = "- AJ-129: " + CloudGpuConsequenceFixtures.CandidateA[..40] + " " + CloudGpuConsequenceFixtures.FileName + "\n";

            Assert.Contains("limit", Assert.Single(CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, longer, logged)), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(200, true)]
        [InlineData(201, false)]
        public void The_200_character_limit_of_a_changed_tagline_is_inclusive(int length, bool passes)
        {
            string changed = CloudGpuConsequenceFixtures.CandidateA.Replace("L40S", "H100", StringComparison.Ordinal);
            string tagline = changed + new string('x', length - changed.Length);
            string logged = "- AJ-129: " + CloudGpuConsequenceFixtures.CandidateA[..40] + " H100\n";

            Assert.Equal(length, tagline.Length);
            Assert.Equal(passes, CloudGpuTaglineRule.TaglineChangeProblems(CloudGpuConsequenceFixtures.CandidateA, tagline, logged).Count == 0);
        }

        [Fact]
        public void A_partial_file_with_no_ok_step_counts_as_absent_for_the_tagline()
        {
            string partial = CloudGpuConsequenceFixtures.Json(RunStatus.Partial);
            string Outcomes(string outcome, bool empty)
            {
                JsonNode root = JsonNode.Parse(partial)!;
                root["steps"] = empty ? new JsonArray() : root["steps"];
                if (!empty)
                {
                    foreach (JsonNode? step in root["steps"]!.AsArray())
                    {
                        step!["outcome"] = outcome;
                    }
                }

                return root.ToJsonString();
            }

            CloudGpuTierState noSteps = CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => Outcomes("ok", empty: true));
            CloudGpuTierState failedOnly = CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => Outcomes("error", empty: false));
            CloudGpuTierState oneOk = CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => Outcomes("ok", empty: false));

            Assert.Equal(new CloudGpuTierState("absent", null, null, null), noSteps);
            Assert.Equal(new CloudGpuTierState("absent", null, null, null), failedOnly);
            Assert.Equal("partial", oneOk.State);
            Assert.Equal(CloudGpuConsequenceFixtures.CandidateALocal, Tagline(noSteps));
        }

        [Fact]
        public void The_pitch_candidates_are_found_and_candidate_A_fits_the_tagline_limit()
        {
            string pitch = File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "devpost-pitch.md"));

            string candidateA = CloudGpuTaglineRule.CandidateOf(pitch, CloudGpuTaglineRule.CandidateALabel);
            string candidateALocal = CloudGpuTaglineRule.CandidateOf(pitch, CloudGpuTaglineRule.CandidateALocalLabel);

            Assert.NotEmpty(candidateALocal);
            Assert.InRange(candidateA.Length, 1, CloudGpuTaglineRule.TaglineLimit);
        }

        [Fact]
        public void A_flags_member_that_is_not_an_array_of_strings_counts_the_file_as_none()
        {
            string completed = CloudGpuConsequenceFixtures.Json(RunStatus.Completed);
            string WithFlags(JsonNode? flags)
            {
                JsonNode root = JsonNode.Parse(completed)!;
                root["run"]!["flags"] = flags;
                return root.ToJsonString();
            }

            Assert.Equal("absent", CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => WithFlags(new JsonArray(1))).State);
            Assert.Equal("absent", CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => WithFlags(new JsonObject())).State);
            Assert.Equal("complete", CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => WithFlags(null)).State);
            Assert.Equal("complete", CloudGpuTaglineRule.Evaluate([CloudGpuConsequenceFixtures.FileName], _ => WithFlags(new JsonArray("partial-offload"))).State);
        }

        [Fact]
        public void The_candidates_are_read_from_the_pitch_layout()
        {
            const string pitch = "**A (recommended)** (10 characters)\n\n> Text of A.\n\nWhy.\n\n**A-local (fallback)** (5 characters)\n\n> Text of local.\n\n**B** (1)\n\n> B text.\n";

            Assert.Equal("Text of A.", CloudGpuTaglineRule.CandidateOf(pitch, CloudGpuTaglineRule.CandidateALabel));
            Assert.Equal("Text of local.", CloudGpuTaglineRule.CandidateOf(pitch, CloudGpuTaglineRule.CandidateALocalLabel));
            _ = Assert.Throws<InvalidDataException>(() => CloudGpuTaglineRule.CandidateOf("**B** (1)\n", CloudGpuTaglineRule.CandidateALabel));
        }

        [Fact]
        public void A_sampling_method_cloudgpu_row_names_the_method()
        {
            string[] row = RowOf(CloudBlock(RunStatus.Completed, sampling: true), "cloudgpu");

            Assert.Equal("completed", row[Column("status")]);
            Assert.Contains("sampling", row[Column("flags")], StringComparison.Ordinal);
        }
    }
}