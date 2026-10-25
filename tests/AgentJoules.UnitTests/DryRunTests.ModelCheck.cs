using System.Globalization;
using System.Reflection;
using System.Text;

using AgentJoules.Cli;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class DryRunTests
{
    /// <summary>
    /// AJ-053 (EDGE-008): the model check before any completion call: a configured model ID that the models endpoint does not list (exit 4), the capped and ordered list of available IDs,
    /// an empty list, an endpoint that is unreachable or answers something that is not a model list (exit 5), and the redaction of provider text. A nested class, so that the class-level
    /// <c>Spec=EDGE-011</c> trait of the key check tests does not tag these tests; the helpers are the private static members of <see cref="DryRunTests"/>.
    /// </summary>
    [Trait("Spec", "EDGE-008")]
    public sealed class ModelCheck
    {
        // ---- criterion 1: a missing ID exits 4 before any completion call ----

        [Fact]
        public async Task Missing_model_exits_4()
        {
            FakeHttpMessageHandler nvidia = Lists("acme/listed-a", "acme/listed-b");

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia, extra: ("AgentJoules:Tiers:nvidia:ModelId", "missing-model"));

            Assert.Equal(4, result.ExitCode);
            string output = Output(result);
            Assert.Contains("missing-model", output, StringComparison.Ordinal);
            Assert.Contains("acme/listed-a", output, StringComparison.Ordinal);
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));

            // The check asked the models route once for the tier and the judge (both nvidia), and the one error line is the mapped failure.
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
            Assert.EndsWith("(ModelNotFoundException)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
            Assert.Contains("missing-model", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
            Assert.Empty(result.DeniedRequests);
        }

        [Fact]
        public async Task A_dry_run_checks_the_models_too_and_makes_no_completion_call()
        {
            FakeHttpMessageHandler nvidia = Lists(ModelOf("nvidia"), "acme/other");

            CliRunResult listed = await RunModelsAsync("run --tier nvidia --dry-run", nvidia);

            Assert.Equal(0, listed.ExitCode);
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));

            FakeHttpMessageHandler lacking = Lists("acme/other");
            CliRunResult missing = await RunModelsAsync("run --tier nvidia --dry-run", lacking);

            Assert.Equal(4, missing.ExitCode);
            Assert.Equal(0, lacking.Count(FakeRoute.ChatCompletions));
        }

        // ---- criterion 2: at most 20 IDs, Nemotron and Qwen first ----

        [Fact]
        public async Task Model_ids_capped_at_20_nemotron_first()
        {
            // 27 plain entries, then two Nemotron entries (one with a capital) and one Qwen entry spread over positions 10, 20 and 28 of the provider's order.
            List<string> ids = [.. Enumerable.Range(1, 27).Select(static index => $"acme/model-{index:00}")];
            ids.Insert(9, "nvidia/nemotron-4-340b-instruct");
            ids.Insert(19, "qwen/qwen3-235b-a22b");
            ids.Insert(27, "NVIDIA/Llama-Nemotron-Super");
            Assert.Equal(30, ids.Count);
            FakeHttpMessageHandler nvidia = Lists([.. ids]);

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia);

            Assert.Equal(4, result.ExitCode);
            string[] printed = PrintedIds(result);
            Assert.Equal(20, printed.Length);
            Assert.All(printed.Take(3), id => Assert.True(IsPreferred(id), id));
            Assert.DoesNotContain(printed.Skip(3), IsPreferred);

            // The three preferred entries keep the order of the provider among themselves, and so do the plain ones after them.
            Assert.Equal(["nvidia/nemotron-4-340b-instruct", "qwen/qwen3-235b-a22b", "NVIDIA/Llama-Nemotron-Super"], printed.Take(3));
            Assert.Equal(ids.Where(static id => !IsPreferred(id)).Take(17), printed.Skip(3));
            Assert.Contains("first 20 of 30", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));
        }

        [Fact]
        public void The_formatter_caps_orders_and_keeps_the_provider_order_stable()
        {
            string[] ids = [.. Enumerable.Range(1, 25).Select(static index => $"plain-{index:00}"), "x/QWEN-1", "x/nemotron-1", "x/qwen-2"];

            ModelIdList list = ModelIdListFormatter.Format(ids);

            Assert.Equal(20, ModelIdListFormatter.MaxListed);
            Assert.Equal(28, list.Total);
            Assert.Equal(20, list.Listed.Count);
            Assert.Equal(["x/QWEN-1", "x/nemotron-1", "x/qwen-2"], list.Listed.Take(3));
            Assert.Equal(Enumerable.Range(1, 17).Select(static index => $"plain-{index:00}"), list.Listed.Skip(3));

            // The same input gives the same output.
            Assert.Equal(list.Listed, ModelIdListFormatter.Format(ids).Listed);
        }

        [Fact]
        public void The_formatter_removes_duplicates_ordinally_and_counts_the_distinct_IDs()
        {
            ModelIdList list = ModelIdListFormatter.Format(["b", "a", "b", "A", "a", "qwen-x", "qwen-x"]);

            Assert.Equal(["qwen-x", "b", "a", "A"], list.Listed);
            Assert.Equal(4, list.Total);
            Assert.Equal(0, ModelIdListFormatter.Format([]).Total);
            Assert.Empty(ModelIdListFormatter.Format([]).Listed);
            Assert.Throws<ArgumentNullException>(() => ModelIdListFormatter.Format(null!));
        }

        [Fact]
        public void The_formatter_lists_exactly_20_when_there_are_exactly_20_and_all_of_them_when_there_are_fewer()
        {
            ModelIdList twenty = ModelIdListFormatter.Format([.. Enumerable.Range(1, 20).Select(static index => $"m-{index:00}")]);
            ModelIdList three = ModelIdListFormatter.Format(["m-1", "m-2", "m-3"]);

            Assert.Equal(20, twenty.Listed.Count);
            Assert.Equal(20, twenty.Total);
            Assert.Equal(["m-1", "m-2", "m-3"], three.Listed);
            Assert.Equal(3, three.Total);
        }

        // ---- criterion 3: every configured ID of a selected tier, and the judge model ----

        [Fact]
        public async Task A_writer_override_absent_from_the_list_exits_4_and_is_printed()
        {
            FakeHttpMessageHandler nebius = Lists(ModelOf("nebius"), "acme/listed");

            CliRunResult result = await RunModelsAsync(
                "run --tier nebius --dry-run",
                Lists(JudgeModel),
                nebius,
                ("AgentJoules:Tiers:nebius:AgentModelIds:Writer", "acme/writer-model-not-listed"));

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("acme/writer-model-not-listed", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
            Assert.Equal(0, nebius.Count(FakeRoute.ChatCompletions));
        }

        [Fact]
        public async Task Every_AgentModelIds_value_of_a_selected_tier_is_checked_and_the_failure_names_every_missing_one()
        {
            var nebius = Lists(ModelOf("nebius"), "acme/planner", "acme/reviewer");
            var nvidia = Lists(JudgeModel);
            using PreflightHost host = HostFor(
                new Dictionary<string, FakeHttpMessageHandler> { ["nebius"] = nebius, ["nvidia"] = nvidia },
                ("AgentJoules:Tiers:nebius:AgentModelIds:Planner", "acme/planner"),
                ("AgentJoules:Tiers:nebius:AgentModelIds:Writer", "acme/writer"),
                ("AgentJoules:Tiers:nebius:AgentModelIds:Researcher", "acme/researcher"),
                ("AgentJoules:Tiers:nebius:AgentModelIds:Reviewer", "acme/reviewer"));
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nebius], dryRun: false);

            ModelNotFoundException failure = await Assert.ThrowsAsync<ModelNotFoundException>(() => host.Preflight.CheckModelsAsync([Tier.Nebius], keys, CancellationToken.None));

            Assert.Equal("nebius", failure.TierName);
            Assert.Equal(ExitCode.ModelNotFound, failure.ExitCode);
            Assert.Equal(["acme/researcher", "acme/writer"], failure.MissingIds.Order(StringComparer.Ordinal));
            Assert.Contains("acme/writer", failure.Message, StringComparison.Ordinal);
            Assert.Contains("acme/researcher", failure.Message, StringComparison.Ordinal);
            Assert.Equal(3, failure.AvailableCount);
            Assert.Equal(0, nebius.Count(FakeRoute.ChatCompletions));
            Assert.Equal(1, nebius.Count(FakeRoute.Models));
        }

        [Fact]
        public async Task All_configured_IDs_listed_passes_and_reports_the_matched_IDs_once_each_per_tier()
        {
            var nebius = Lists(ModelOf("nebius"), "acme/writer", "acme/extra");
            var nvidia = Lists(JudgeModel, "acme/extra");
            using PreflightHost host = HostFor(
                new Dictionary<string, FakeHttpMessageHandler> { ["nebius"] = nebius, ["nvidia"] = nvidia },
                ("AgentJoules:Tiers:nebius:AgentModelIds:Writer", "acme/writer"),
                ("AgentJoules:Tiers:nebius:AgentModelIds:Reviewer", "acme/writer"));
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nebius], dryRun: true);

            ModelCheckResult result = await host.Preflight.CheckModelsAsync([Tier.Nebius], keys, CancellationToken.None);

            Assert.Equal(["nebius", "nvidia"], result.Tiers.Select(static entry => entry.Tier));
            ModelCheckEntry entry = result.Tiers[0];
            Assert.Equal(new Uri(Shipped.Tiers["nebius"].BaseUrl!), entry.Endpoint);
            Assert.Equal([ModelOf("nebius"), "acme/writer"], entry.MatchedIds);
            Assert.Equal([JudgeModel], result.Tiers[1].MatchedIds);
            Assert.Equal(1, nebius.Count(FakeRoute.Models));
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
            Assert.Equal(0, nebius.Count(FakeRoute.ChatCompletions) + nvidia.Count(FakeRoute.ChatCompletions));
            _ = await Assert.ThrowsAsync<ArgumentNullException>(() => host.Preflight.CheckModelsAsync(null!, keys, CancellationToken.None));
            _ = await Assert.ThrowsAsync<ArgumentNullException>(() => host.Preflight.CheckModelsAsync([Tier.Nebius], null!, CancellationToken.None));
        }

        [Fact]
        public async Task The_judge_model_is_checked_against_the_judge_tier_and_the_failure_names_that_tier()
        {
            var nebius = Lists(ModelOf("nebius"));
            var nvidia = Lists("acme/other");
            using PreflightHost host = HostFor(
                new Dictionary<string, FakeHttpMessageHandler> { ["nebius"] = nebius, ["nvidia"] = nvidia },
                ("AgentJoules:Judge:ModelId", "judge-not-listed"));
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nebius], dryRun: false);

            ModelNotFoundException failure = await Assert.ThrowsAsync<ModelNotFoundException>(() => host.Preflight.CheckModelsAsync([Tier.Nebius], keys, CancellationToken.None));

            Assert.Equal("nvidia", failure.TierName);
            Assert.Equal(["judge-not-listed"], failure.MissingIds);
            Assert.Equal(["acme/other"], failure.AvailableIds);
            Assert.Equal(1, failure.AvailableCount);
            Assert.Equal(1, nebius.Count(FakeRoute.Models));
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
        }

        [Fact]
        public async Task The_list_of_a_tier_is_reused_when_it_is_also_the_judge_tier()
        {
            var nvidia = Lists(ModelOf("nvidia"), "acme/other");
            using PreflightHost host = HostFor(
                new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = nvidia },
                ("AgentJoules:Judge:ModelId", "judge-not-listed"));
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nvidia], dryRun: true);

            ModelNotFoundException failure = await Assert.ThrowsAsync<ModelNotFoundException>(() => host.Preflight.CheckModelsAsync([Tier.Nvidia], keys, CancellationToken.None));

            Assert.Equal(["judge-not-listed"], failure.MissingIds);
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
        }

        [Fact]
        public async Task The_judge_model_is_not_checked_outside_a_dry_run_when_the_judge_key_is_missing_and_is_checked_in_a_dry_run()
        {
            var nebius = Lists(ModelOf("nebius"));
            var nvidia = Lists("acme/other");
            IConfiguration noJudgeKey = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), ("NEBIUS_API_KEY", Key()));
            using PreflightHost skipped = PreflightFixtures.Build(noJudgeKey, new Dictionary<string, FakeHttpMessageHandler> { ["nebius"] = nebius, ["nvidia"] = nvidia });
            KeyCheckResult keys = skipped.Preflight.CheckKeys([Tier.Nebius], dryRun: false);

            ModelCheckResult result = await skipped.Preflight.CheckModelsAsync([Tier.Nebius], keys, CancellationToken.None);

            // The warning path of AJ-052 owns the missing key; asking the judge tier would only produce a 401 that hides the cause.
            Assert.Equal("NVIDIA_API_KEY", keys.MissingJudgeVariable);
            Assert.Equal(["nebius"], result.Tiers.Select(static entry => entry.Tier));
            Assert.Equal(0, nvidia.RequestCount);

            // In a dry run the judge key is present (a missing one is exit 3), and the judge model is checked.
            var nebiusAgain = Lists(ModelOf("nebius"));
            var nvidiaAgain = Lists("acme/other");
            using PreflightHost checkedHost = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nebius"] = nebiusAgain, ["nvidia"] = nvidiaAgain });
            KeyCheckResult dryKeys = checkedHost.Preflight.CheckKeys([Tier.Nebius], dryRun: true);

            ModelNotFoundException failure = await Assert.ThrowsAsync<ModelNotFoundException>(() => checkedHost.Preflight.CheckModelsAsync([Tier.Nebius], dryKeys, CancellationToken.None));

            Assert.Equal([JudgeModel], failure.MissingIds);
            Assert.Equal(1, nvidiaAgain.Count(FakeRoute.Models));
        }

        [Fact]
        public async Task A_judge_without_a_model_ID_is_not_checked_and_a_tier_without_any_configured_ID_is_refused_before_a_request()
        {
            AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
            options.Judge.Tier = "nvidia";
            options.Judge.ModelId = null;
            var nvidia = Lists("model-nvidia");
            var nebius = Lists("model-nebius");
            using PreflightHost host = PreflightFixtures.Build(
                ProviderClientFixtures.KeyConfiguration(),
                new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = nvidia, ["nebius"] = nebius },
                options);
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nebius], dryRun: true);

            // No judge model ID: only the selected tier is listed.
            ModelCheckResult result = await host.Preflight.CheckModelsAsync([Tier.Nebius], keys, CancellationToken.None);
            Assert.Equal(["nebius"], result.Tiers.Select(static entry => entry.Tier));
            Assert.Equal(0, nvidia.RequestCount);

            // A judge tier that is not a tier name is a configuration failure that does not echo the value.
            options.Judge.ModelId = "model-judge";
            options.Judge.Tier = "bogus";
            ConfigurationException unknown = await Assert.ThrowsAsync<ConfigurationException>(() => host.Preflight.CheckModelsAsync([Tier.Nebius], keys, CancellationToken.None));
            Assert.Equal(ExitCode.InvalidArguments, unknown.ExitCode);
            Assert.DoesNotContain("bogus", unknown.Message, StringComparison.Ordinal);

            // The validator matches Judge:Tier exactly, so a different case is not a tier name here either.
            options.Judge.Tier = "Nvidia";
            _ = await Assert.ThrowsAsync<ConfigurationException>(() => host.Preflight.CheckModelsAsync([Tier.Nebius], keys, CancellationToken.None));
            options.Judge.ModelId = null;

            // A tier with neither ModelId nor AgentModelIds has nothing to check; that is a configuration failure (exit 2), found before any request.
            options.Tiers["nebius"].ModelId = null;
            int before = nebius.RequestCount;
            ConfigurationException failure = await Assert.ThrowsAsync<ConfigurationException>(() => host.Preflight.CheckModelsAsync([Tier.Nebius], keys, CancellationToken.None));
            Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
            Assert.Contains("nebius", failure.Message, StringComparison.Ordinal);
            Assert.Equal(before, nebius.RequestCount);
        }

        // ---- criterion 4: an unreachable endpoint exits 5, an empty list exits 4 ----

        [Fact]
        public async Task An_empty_model_list_exits_4_with_the_line_no_models_listed()
        {
            FakeHttpMessageHandler nvidia = Lists();

            CliRunResult result = await RunModelsAsync("run --tier nvidia --dry-run", nvidia);

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("no models listed", result.StderrLines);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains("tier nvidia: no models listed", error, StringComparison.Ordinal);
            Assert.EndsWith("(ModelNotFoundException)", error, StringComparison.Ordinal);
            Assert.Empty(PrintedIds(result));
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));
        }

        [Fact]
        public async Task A_models_endpoint_that_refuses_the_connection_exits_5_before_any_completion_call()
        {
            var nvidia = new FakeHttpMessageHandler { ConnectionRefused = true };

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia);

            Assert.Equal(5, result.ExitCode);
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));
            string error = Assert.Single(result.ErrorLines);
            Assert.EndsWith("(EndpointUnreachableException)", error, StringComparison.Ordinal);
            Assert.Contains("nvidia", error, StringComparison.Ordinal);
            Assert.Contains("/models", error, StringComparison.Ordinal);
            Assert.False(Output(result).Contains(Key(), StringComparison.Ordinal), "the output holds the configured key");
            Assert.Empty(result.DeniedRequests);
        }

        [Theory]
        [InlineData(401)]
        [InlineData(404)]
        [InlineData(500)]
        [InlineData(302)]
        public async Task A_non_2xx_models_answer_exits_5_with_the_status_and_no_byte_of_the_body(int status)
        {
            var nvidia = new FakeHttpMessageHandler();
            nvidia.SetStatuses(FakeRoute.Models, status);
            nvidia.CustomizeResponse = static (route, response) =>
            {
                if (route == FakeRoute.Models)
                {
                    response.Content = new StringContent("{\"error\":\"" + BodyMarker + " " + Key() + "\"}");
                }
            };

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia);

            Assert.Equal(5, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), error, StringComparison.Ordinal);
            Assert.EndsWith("(EndpointUnreachableException)", error, StringComparison.Ordinal);
            Assert.DoesNotContain(BodyMarker, Output(result), StringComparison.Ordinal);
            Assert.False(Output(result).Contains(Key(), StringComparison.Ordinal), "the output holds the configured key");
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));
        }

        [Theory]
        [InlineData("this is not json body-marker-7f3a91")]
        [InlineData("{\"body-marker-7f3a91\":1}")]
        [InlineData("{\"data\":\"body-marker-7f3a91\"}")]
        [InlineData("[\"body-marker-7f3a91\"]")]
        [InlineData("")]
        public async Task A_models_answer_that_is_not_a_model_list_exits_5_without_echoing_the_body(string body)
        {
            var nvidia = new FakeHttpMessageHandler
            {
                CustomizeResponse = (route, response) =>
                {
                    if (route == FakeRoute.Models)
                    {
                        response.Content = new StringContent(body);
                    }
                },
            };

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia);

            Assert.Equal(5, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains("unreadable model list", error, StringComparison.Ordinal);
            Assert.Contains("nvidia", error, StringComparison.Ordinal);
            Assert.DoesNotContain(BodyMarker, Output(result), StringComparison.Ordinal);
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));
        }

        [Fact]
        public async Task A_models_endpoint_that_does_not_answer_in_time_is_unreachable_and_a_cancelled_caller_is_not()
        {
            var slow = new FakeHttpMessageHandler { ResponseDelay = TimeSpan.FromSeconds(30) };
            using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = slow }, ("AgentJoules:Tiers:nvidia:TimeoutSeconds", "1"));

            EndpointUnreachableException timeout = await Assert.ThrowsAsync<EndpointUnreachableException>(() => host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None));

            Assert.Equal(ExitCode.Unreachable, timeout.ExitCode);
            Assert.Contains("nvidia", timeout.Message, StringComparison.Ordinal);
            Assert.Contains("timed out", timeout.Message, StringComparison.Ordinal);
            Assert.IsType<OperationCanceledException>(timeout.InnerException, exactMatch: false);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Catalog.ListAsync(Tier.Nvidia, cancelled.Token));
        }

        // ---- the catalog client: the request, the key, the parsing ----

        [Fact]
        public async Task The_catalog_sends_one_GET_to_the_models_route_with_the_bearer_key_of_a_keyed_tier_and_none_for_a_keyless_one()
        {
            var local = Lists("local-a", "local-b");
            var nvidia = Lists("nv-a", "nv-b", "nv-a");
            using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["local"] = local, ["nvidia"] = nvidia });

            ModelCatalog nvidiaIds = await host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None);
            ModelCatalog localIds = await host.Catalog.ListAsync(Tier.Local, CancellationToken.None);

            RecordedRequest keyed = Assert.Single(nvidia.Requests);
            Assert.Equal("GET", keyed.Method);
            Assert.Equal(Shipped.Tiers["nvidia"].BaseUrl + "/models", keyed.Uri.AbsoluteUri);
            Assert.True(keyed.HasBearerToken);
            Assert.Equal("Bearer", keyed.AuthorizationScheme);
            Assert.Equal(["nv-a", "nv-b"], nvidiaIds.DisplayIds);
            Assert.Equal(2, nvidiaIds.Count);
            Assert.True(nvidiaIds.Lists("nv-a"));
            Assert.False(nvidiaIds.Lists("nv-c"));

            RecordedRequest keyless = Assert.Single(local.Requests);
            Assert.False(keyless.HasBearerToken);
            Assert.Null(keyless.AuthorizationScheme);
            Assert.Equal(Shipped.Tiers["local"].BaseUrl + "/models", keyless.Uri.AbsoluteUri);
            Assert.Equal(["local-a", "local-b"], localIds.DisplayIds);
        }

        [Fact]
        public async Task A_keyed_tier_without_a_key_value_is_refused_before_any_request_and_a_key_on_plain_http_is_never_sent()
        {
            var nvidia = Lists(ModelOf("nvidia"));
            IConfiguration noKey = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath());
            using PreflightHost missing = PreflightFixtures.Build(noKey, new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = nvidia });

            MissingKeyException failure = await Assert.ThrowsAsync<MissingKeyException>(() => missing.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None));

            Assert.Equal(["NVIDIA_API_KEY"], failure.Variables);
            Assert.Equal(0, nvidia.RequestCount);

            // The validator refuses plain http for a keyed tier at start (exit 2), so the client check is reached through options that skip the validator.
            AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
            options.Tiers["nvidia"].BaseUrl = "http://nvidia.fake.test/v1";
            using PreflightHost plain = PreflightFixtures.Build(ProviderClientFixtures.KeyConfiguration(), new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = nvidia }, options);

            ConfigurationException insecure = await Assert.ThrowsAsync<ConfigurationException>(() => plain.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None));

            Assert.Contains("https", insecure.Message, StringComparison.Ordinal);
            Assert.Equal(0, nvidia.RequestCount);
        }

        [Fact]
        public async Task The_catalog_skips_entries_without_a_string_ID_and_returns_an_empty_list_for_an_empty_data_array()
        {
            var nvidia = new FakeHttpMessageHandler
            {
                CustomizeResponse = static (route, response) =>
                {
                    if (route == FakeRoute.Models)
                    {
                        response.Content = new StringContent("{\"object\":\"list\",\"data\":[{\"name\":\"no-id\"},{\"id\":7},{\"id\":\"\"},{\"id\":\"ok-1\"},null,{\"id\":\"ok-2\"}]}");
                    }
                },
            };
            using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = nvidia });

            ModelCatalog ids = await host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None);

            Assert.Equal(["ok-1", "ok-2"], ids.DisplayIds);
            Assert.Equal(2, ids.Count);

            nvidia.CustomizeResponse = static (route, response) =>
            {
                if (route == FakeRoute.Models)
                {
                    response.Content = new StringContent("{\"data\":[]}");
                }
            };
            ModelCatalog empty = await host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None);
            Assert.Empty(empty.DisplayIds);
            Assert.Equal(0, empty.Count);
        }

        // ---- provider text is redacted, scrubbed and cut before it is printed or compared ----

        [Fact]
        public async Task A_listed_ID_that_holds_a_registered_key_is_masked_in_the_output_and_in_the_returned_list()
        {
            string key = Key();
            FakeHttpMessageHandler nvidia = Lists("acme/" + key, "acme/plain");

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia);

            Assert.Equal(4, result.ExitCode);
            Assert.False(Output(result).Contains(key, StringComparison.Ordinal), "the output holds the configured key");
            Assert.Contains("acme/****", PrintedIds(result));
            Assert.Contains("acme/plain", PrintedIds(result));

            using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = nvidia });
            ModelCatalog catalog = await host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None);
            Assert.Equal(["acme/****", "acme/plain"], catalog.DisplayIds);
            Assert.True(catalog.Lists("acme/" + key));
            Assert.False(catalog.Lists("acme/****"));
        }

        [Fact]
        public async Task A_configured_ID_that_holds_a_registered_key_is_masked_in_the_failure()
        {
            string key = Key();
            using PreflightHost host = HostFor(
                new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = Lists("acme/plain") },
                ("AgentJoules:Tiers:nvidia:ModelId", "mine-" + key));
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nvidia], dryRun: true);

            ModelNotFoundException failure = await Assert.ThrowsAsync<ModelNotFoundException>(() => host.Preflight.CheckModelsAsync([Tier.Nvidia], keys, CancellationToken.None));

            Assert.Equal(["mine-****"], failure.MissingIds);
            Assert.False(failure.Message.Contains(key, StringComparison.Ordinal), "the message holds the configured key");
        }

        [Fact]
        public async Task A_listed_ID_with_a_control_character_or_over_64_characters_is_echoed_scrubbed_and_cut()
        {
            string bell = "bad" + (char)7 + "id";
            string escape = "esc" + (char)27 + "[31mred";
            string longId = new('x', 100);
            FakeHttpMessageHandler nvidia = Lists(bell, escape, longId);

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia);

            Assert.Equal(4, result.ExitCode);
            string[] printed = PrintedIds(result);
            Assert.Equal(["bad?id", "esc?[31mred", new string('x', 64) + "..."], printed);
            Assert.DoesNotContain((char)7, Output(result));
            Assert.DoesNotContain((char)27, Output(result));
            Assert.DoesNotContain(new string('x', 65), Output(result), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_configured_ID_that_is_listed_matches_even_when_it_is_long_or_holds_a_control_character()
        {
            string longId = new('y', 100);
            string withBell = "bell" + (char)7 + "model";
            FakeHttpMessageHandler nvidia = Lists(longId, withBell);

            CliRunResult longRun = await RunModelsAsync(
                "run --tier nvidia --dry-run",
                nvidia,
                extra: [("AgentJoules:Tiers:nvidia:ModelId", longId), ("AgentJoules:Judge:ModelId", longId)]);
            CliRunResult bellRun = await RunModelsAsync(
                "run --tier nvidia --dry-run",
                nvidia,
                extra: [("AgentJoules:Tiers:nvidia:ModelId", withBell), ("AgentJoules:Judge:ModelId", withBell)]);

            Assert.Equal(0, longRun.ExitCode);
            Assert.Equal(0, bellRun.ExitCode);
            Assert.Empty(longRun.ErrorLines);
            Assert.Empty(bellRun.ErrorLines);
        }

        // ---- the failure report, the exceptions and the registrations ----

        [Fact]
        public void The_reporter_prints_a_header_and_one_scrubbed_ID_per_line_to_the_error_writer_only()
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var reporter = new ConsoleReporter(output, error);
            var failure = new ModelNotFoundException("nvidia", ["missing"], ["nvidia/nemotron-a", "bad" + (char)7 + "id"], 30);

            reporter.ReportModelNotFound(failure);

            Assert.Empty(output.ToString());
            Assert.Equal(["available models of nvidia (first 2 of 30):", "  nvidia/nemotron-a", "  bad?id"], error.ToString().TrimEnd().Split(Environment.NewLine));

            using var emptyError = new StringWriter();
            new ConsoleReporter(output, emptyError).ReportModelNotFound(new ModelNotFoundException("nvidia", ["missing"], [], 0));

            Assert.Equal("no models listed" + Environment.NewLine, emptyError.ToString());
            Assert.Throws<ArgumentNullException>(() => reporter.ReportModelNotFound(null!));
        }

        [Fact]
        public void The_model_not_found_exception_exits_4_with_a_one_line_message_and_the_endpoint_unreachable_exception_exits_5()
        {
            var notFound = new ModelNotFoundException("nvidia", ["a", "b" + (char)10 + "c"], ["x"], 1);
            var empty = new ModelNotFoundException("nebius", ["a"], [], 0);
            var unreachable = new EndpointUnreachableException("tier nvidia: models endpoint unreachable (GET /models)");
            var inner = new IOException("inner");
            var wrapped = new EndpointUnreachableException("tier nvidia: models endpoint unreachable (GET /models)", inner);

            Assert.Equal(ExitCode.ModelNotFound, notFound.ExitCode);
            Assert.Equal(4, ExitCodeMapper.Map(notFound, shutdownRequested: false));
            Assert.Equal(4, ExitCodeMapper.Map(empty, shutdownRequested: false));
            Assert.DoesNotContain('\n', notFound.Message);
            Assert.Contains("tier nvidia", notFound.Message, StringComparison.Ordinal);
            Assert.Contains("a, b?c", notFound.Message, StringComparison.Ordinal);
            Assert.Equal("tier nebius: no models listed; model ID not listed: a", empty.Message);
            Assert.Equal(["a", "b" + (char)10 + "c"], notFound.MissingIds);
            Assert.Equal(["x"], notFound.AvailableIds);
            Assert.Equal(1, notFound.AvailableCount);
            Assert.Equal("nvidia", notFound.TierName);

            Assert.Equal(ExitCode.Unreachable, unreachable.ExitCode);
            Assert.Equal(5, ExitCodeMapper.Map(unreachable, shutdownRequested: false));
            Assert.Same(inner, wrapped.InnerException);
            Assert.Equal(5, ExitCodeMapper.Map(wrapped, shutdownRequested: false));
        }

        [Fact]
        public void The_model_not_found_exception_refuses_a_blank_tier_no_missing_ID_and_an_inconsistent_count()
        {
            Assert.Throws<ArgumentException>(() => new ModelNotFoundException(" ", ["a"], [], 0));
            Assert.Throws<ArgumentException>(() => new ModelNotFoundException("nvidia", [], [], 0));
            Assert.Throws<ArgumentNullException>(() => new ModelNotFoundException("nvidia", null!, [], 0));
            Assert.Throws<ArgumentNullException>(() => new ModelNotFoundException("nvidia", ["a"], null!, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ModelNotFoundException("nvidia", ["a"], ["x", "y"], 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ModelNotFoundException("nvidia", ["a"], [], -1));
        }

        [Fact]
        public void The_model_catalog_client_is_registered_once_with_the_preflight()
        {
            var services = new ServiceCollection();

            _ = services.AddAgentJoulesPreflight().AddAgentJoulesPreflight();

            _ = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ModelCatalogClient));
        }

        // ---- exact matching: an ID is compared as the provider wrote it; only what is shown is cut and scrubbed ----

        [Fact]
        public async Task Two_IDs_that_share_their_first_64_characters_are_different_IDs()
        {
            string prefix = new('y', 64);
            FakeHttpMessageHandler nvidia = Lists(prefix + "-b", "acme/other");

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia, extra: ("AgentJoules:Tiers:nvidia:ModelId", prefix + "-a"));

            Assert.Equal(4, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains(prefix + "...", error, StringComparison.Ordinal);
            Assert.Contains(prefix + "...", PrintedIds(result));
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));
        }

        [Fact]
        public async Task A_configured_ID_with_a_control_character_is_not_matched_by_its_scrubbed_form()
        {
            string configured = "abc" + (char)0x1B + "x";
            FakeHttpMessageHandler nvidia = Lists("abc?x");

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia, extra: ("AgentJoules:Tiers:nvidia:ModelId", configured));

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("abc?x", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
            Assert.DoesNotContain((char)0x1B, Output(result));
        }

        [Fact]
        public async Task The_catalog_compares_the_exact_IDs_and_shows_only_echoed_ones()
        {
            string bell = "bad" + (char)7 + "id";
            string backspace = "bad" + (char)8 + "id";
            string longId = new('x', 100);
            string keyed = "acme/" + Key();
            using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = Lists(bell, longId, keyed, bell, backspace, "plain") });

            ModelCatalog catalog = await host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None);

            Assert.True(catalog.Lists(bell));
            Assert.True(catalog.Lists(backspace));
            Assert.False(catalog.Lists("bad?id"));
            Assert.True(catalog.Lists(longId));
            Assert.False(catalog.Lists(new string('x', 64) + "..."));
            Assert.False(catalog.Lists(new string('x', 99)));
            Assert.True(catalog.Lists(keyed));
            Assert.False(catalog.Lists("acme/****"));
            Assert.False(catalog.Lists("PLAIN"));

            // Five distinct IDs as written; the two with a control character show the same, so four are shown, in the order of the provider.
            Assert.Equal(5, catalog.Count);
            Assert.Equal(["bad?id", new string('x', 64) + "...", "acme/****", "plain"], catalog.DisplayIds);
        }

        // ---- the key stays inside the provider client factory ----

        [Fact]
        public void The_raw_key_does_not_leave_the_provider_client_factory()
        {
            MethodInfo? read = typeof(ProviderClientFactory).GetMethod("ReadKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.True(read is null || read.IsPrivate, "ReadKey must be private: the key is handed to the SDK credential or to one request header and nowhere else.");
        }

        [Fact]
        public void The_factory_builds_the_models_request_with_the_bearer_key_or_none_and_refuses_a_tier_it_cannot_serve()
        {
            using PreflightHost host = HostFor();

            using HttpRequestMessage keyed = host.Factory.CreateModelsRequest(Tier.Nvidia);
            using HttpRequestMessage keyless = host.Factory.CreateModelsRequest(Tier.Local);

            Assert.Equal(HttpMethod.Get, keyed.Method);
            Assert.Equal(Shipped.Tiers["nvidia"].BaseUrl + "/models", keyed.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", keyed.Headers.Authorization?.Scheme);
            Assert.True(string.Equals(Key(), keyed.Headers.Authorization?.Parameter, StringComparison.Ordinal), "the bearer parameter is not the configured key");
            Assert.Equal(HttpMethod.Get, keyless.Method);
            Assert.Equal(Shipped.Tiers["local"].BaseUrl + "/models", keyless.RequestUri!.AbsoluteUri);
            Assert.Null(keyless.Headers.Authorization);

            using PreflightHost noKey = PreflightFixtures.Build(ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath()));
            MissingKeyException missing = Assert.Throws<MissingKeyException>(() => noKey.Factory.CreateModelsRequest(Tier.Nvidia));
            Assert.Equal(["NVIDIA_API_KEY"], missing.Variables);

            AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
            options.Tiers["nvidia"].BaseUrl = "http://nvidia.fake.test/v1";
            using PreflightHost plain = PreflightFixtures.Build(ProviderClientFixtures.KeyConfiguration(), optionsOverride: options);
            _ = Assert.Throws<ConfigurationException>(() => plain.Factory.CreateModelsRequest(Tier.Nvidia));
        }

        // ---- the preflight does not wait for the completion timeout ----

        [Theory]
        [InlineData(1, 1)]
        [InlineData(29, 29)]
        [InlineData(30, 30)]
        [InlineData(31, 30)]
        [InlineData(300, 30)]
        [InlineData(int.MaxValue, 30)]
        public void The_preflight_limit_is_the_tier_timeout_capped_at_30_seconds(int tierSeconds, int expectedSeconds)
        {
            Assert.Equal(30, ModelCatalogClient.PreflightTimeoutSeconds);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ModelCatalogClient.PreflightLimit(TimeSpan.FromSeconds(tierSeconds)));
        }

        [Fact]
        public async Task A_tier_timeout_far_above_the_range_of_CancelAfter_does_not_break_the_preflight()
        {
            using PreflightHost host = HostFor(
                new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = Lists("m") },
                ("AgentJoules:Tiers:nvidia:TimeoutSeconds", int.MaxValue.ToString(CultureInfo.InvariantCulture)));

            ModelCatalog catalog = await host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None);

            Assert.Equal(1, catalog.Count);
        }

        [Fact]
        public async Task A_body_that_stalls_after_the_headers_is_cut_by_the_preflight_timeout_and_exits_5()
        {
            var nvidia = new FakeHttpMessageHandler
            {
                CustomizeResponse = static (route, response) =>
                {
                    if (route == FakeRoute.Models)
                    {
                        response.Content = new StreamContent(new StallingStream("{\"data\":["));
                    }
                },
            };

            CliRunResult result = await RunModelsAsync("run --tier nvidia", nvidia, extra: ("AgentJoules:Tiers:nvidia:TimeoutSeconds", "1")).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(5, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains("nvidia", error, StringComparison.Ordinal);
            Assert.Contains("timed out", error, StringComparison.Ordinal);
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));
        }

        // ---- the body: the cap, the byte order mark ----

        private static FakeHttpMessageHandler Serving(Func<HttpContent> content) =>
            new()
            {
                CustomizeResponse = (route, response) =>
                {
                    if (route == FakeRoute.Models)
                    {
                        response.Content = content();
                    }
                },
            };

        // A valid list padded to exactly this many bytes.
        private static string ListPaddedTo(int bytes)
        {
            const string head = "{\"data\":[{\"id\":\"big-ok\"}],\"pad\":\"";
            const string tail = "\"}";
            return head + new string('p', bytes - head.Length - tail.Length) + tail;
        }

        [Fact]
        public async Task A_models_body_over_the_4_MiB_cap_is_unreadable_and_one_at_the_cap_parses()
        {
            const int cap = 4 * 1024 * 1024;

            FakeHttpMessageHandler over = Serving(() => new StringContent(ListPaddedTo(cap + 1)));
            CliRunResult result = await RunModelsAsync("run --tier nvidia", over);

            Assert.Equal(5, result.ExitCode);
            string error = Assert.Single(result.ErrorLines);
            Assert.Contains("unreadable model list", error, StringComparison.Ordinal);
            Assert.DoesNotContain("pppp", Output(result), StringComparison.Ordinal);
            Assert.DoesNotContain("big-ok", Output(result), StringComparison.Ordinal);

            foreach (int size in new[] { cap - 1, cap })
            {
                using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = Serving(() => new StringContent(ListPaddedTo(size))) });

                ModelCatalog catalog = await host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None);

                Assert.True(catalog.Lists("big-ok"));
            }
        }

        [Fact]
        public async Task A_models_body_that_starts_with_a_UTF8_byte_order_mark_is_read()
        {
            byte[] json = Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"bom-ok\"}]}");
            byte[] withBom = [0xEF, 0xBB, 0xBF, .. json];
            using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = Serving(() => new ByteArrayContent(withBom)) });

            ModelCatalog catalog = await host.Catalog.ListAsync(Tier.Nvidia, CancellationToken.None);

            Assert.True(catalog.Lists("bom-ok"));
        }

        // ---- several tiers: the first failing tier stops the check ----

        [Fact]
        public async Task The_first_failing_tier_stops_the_check_and_the_later_tier_is_not_asked()
        {
            var nvidia = Lists("acme/other");
            var nebius = Lists(ModelOf("nebius"));
            using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = nvidia, ["nebius"] = nebius });
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nvidia, Tier.Nebius], dryRun: false);

            ModelNotFoundException failure = await Assert.ThrowsAsync<ModelNotFoundException>(() => host.Preflight.CheckModelsAsync([Tier.Nvidia, Tier.Nebius], keys, CancellationToken.None));

            Assert.Equal("nvidia", failure.TierName);
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
            Assert.Equal(0, nebius.Count(FakeRoute.Models));
        }

        [Fact]
        public async Task Two_selected_tiers_that_list_their_IDs_are_each_asked_once_and_reported_in_order()
        {
            var nvidia = Lists(ModelOf("nvidia"), "acme/other");
            var nebius = Lists(ModelOf("nebius"));
            using PreflightHost host = HostFor(new Dictionary<string, FakeHttpMessageHandler> { ["nvidia"] = nvidia, ["nebius"] = nebius });
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nvidia, Tier.Nebius], dryRun: true);

            ModelCheckResult result = await host.Preflight.CheckModelsAsync([Tier.Nvidia, Tier.Nebius], keys, CancellationToken.None);

            Assert.Equal(["nvidia", "nebius"], result.Tiers.Select(static entry => entry.Tier));
            Assert.Equal([ModelOf("nvidia")], result.Tiers[0].MatchedIds);
            Assert.Equal([ModelOf("nebius")], result.Tiers[1].MatchedIds);
            Assert.Equal(1, nvidia.Count(FakeRoute.Models));
            Assert.Equal(1, nebius.Count(FakeRoute.Models));
        }

        [Fact]
        public async Task An_empty_tier_selection_is_refused_because_a_result_names_at_least_one_tier()
        {
            using PreflightHost host = HostFor();
            KeyCheckResult keys = host.Preflight.CheckKeys([Tier.Nvidia], dryRun: true);

            _ = await Assert.ThrowsAsync<ArgumentException>(() => host.Preflight.CheckModelsAsync([], keys, CancellationToken.None));
        }

        // A body that delivers its first bytes and then waits until the read is cancelled, as a server that stalls after the headers does.
        private sealed class StallingStream(string prefix) : Stream
        {
            private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
            private bool _delivered;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_delivered)
                {
                    _delivered = true;
                    _prefix.CopyTo(buffer);
                    return _prefix.Length;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}