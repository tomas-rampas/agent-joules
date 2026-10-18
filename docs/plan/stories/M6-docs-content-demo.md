# M6: Docs, content, demo

Goal: turn the committed results files into the public face of Agent Joules. The README with its generated results block, three LinkedIn post drafts, the demo program and its under-three-minutes video script, the long-form article, the Devpost checklist, the filled Devpost story and the filled feedback answers are all produced here, and every number in them traces to a committed file under `results/`. `AJ` in this file stands for `dotnet run --project src/AgentJoules.Cli --`, run from the repository root.

## Dates and exit criteria

Dates: 2026-10-27 to 2026-10-29 (three working days, about 18 working hours at six hours per day). Deadline for the whole project: 2026-10-30 10:00 PDT (17:00 UTC).

Exit criteria:
- [ ] `AJ report --help` exits 0 and lists `--update-readme`, `--verify-readme` and `--render-content` (AJ-144).
- [ ] `AJ report --verify-readme` exits 0 and `grep -c "<!-- results:begin -->" README.md` prints 1.
- [ ] `grep -c "Filled during the build" README.md` prints 0 and `grep -c '^```mermaid$' README.md` prints 1.
- [ ] `grep -c "{{results\." content/linkedin-posts.md` prints 0 and every file named on the `Sources:` line of `content/linkedin-posts.md` exists.
- [ ] The duration column of the shot list in `content/demo-script.md` sums to 170 or less (planned: 165) and `AJ <command> --help` exits 0 for probe, run, report, validate and calibrate.
- [ ] `content/article.md` contains `2407.16893`, `TokenPowerBench`, `2604.09048`, `greentoken` and a `Limitations` heading.
- [ ] `docs/devpost-checklist.md` contains the headings `Registration`, `Tracks`, `Judging criteria`, `Credits`, `Feedback form, user fills`, a line starting `Demo URL:`, the string `2026-10-30 10:00 PDT (17:00 UTC)` and fenced blocks with `git push`, `gh release create` and the Pages settings.
- [ ] `grep -cF "[[FILL:" docs/devpost-story.md` prints 0 and `grep -ci "Fill-after-build" docs/devpost-story.md` prints 0.
- [ ] `grep -cF "owner: builder" docs/devpost-feedback.md` prints 0 and `grep -cF "owner: user" docs/devpost-feedback.md` prints 7.
- [ ] `AJ run --tier local --dashboard --dry-run` prints a line starting `Dashboard:` with `http://127.0.0.1:` and exits 0; `AJ dashboard --replay <committed hosted run file> --speed 50 --exit-after 5` exits 0; README and `content/demo-script.md` both mention `--dashboard`, `dashboard --replay` and that the published results were recorded without a browser open.
- [ ] The honesty audit of AJ-143 exits 0 over README, content and Devpost files, and `dotnet test` exits 0.

## Coverage

REQ-025, REQ-026, REQ-027, REQ-028, and REQ-016 only where its `report` flags and the content renderer live (AJ-144; the rest of REQ-016 is M3).

## Dependencies and user actions

Needs from earlier milestones:

| Needs | From | Used by |
|---|---|---|
| Summary, SVG and HTML renderers, `ReadmeResultsBlock.Render` (AJ-087), `index.html`, `.nojekyll` | M3 | AJ-144, AJ-133, AJ-135 |
| The three `report` flags and `ContentTemplateRenderer` | AJ-144 (this milestone) | AJ-133, AJ-134, AJ-135 |
| `docs/endpoints.md`, `docs/methodology.md`, `docs/nim-local-attempt.md`, local run, probe and calibration files | M2, M4 | AJ-132, AJ-137, AJ-140 |
| `results/*-nvidia.json`, `results/*-nebius.json` | M2, M3 | AJ-133, AJ-134, AJ-140 |
| `results/*-cloudgpu.json`, `results/probe-*-cloudgpu.json`, `docs/cloudgpu-run.md` (may be absent; every story has the fallback) | M5 | AJ-132, AJ-135, AJ-140, AJ-141 |
| `docs/build-log.md` entries | M0 to M5 | AJ-140, AJ-142 |

User actions (the user's, not the builder's; section 12 step numbers):

| Step | User action | Where the result is recorded | Needed by |
|---|---|---|---|
| 2 | Join the hackathon on Devpost; fill `Devpost handle`, `Joined on`, `Credits requested on` | `docs/devpost-checklist.md` section `Registration` | AJ-139 |
| 3 | Paste the Rules tab text if the builder's fetch returns HTTP 403 | `docs/devpost-checklist.md` section `Rules verification` | AJ-138 |
| 4 | Request the USD 25 Token Factory credits and join the Builders Program | section `Credits` | AJ-139 |
| 5, 6, 7 | Keys `NEBIUS_API_KEY`, `NVIDIA_API_KEY`, `NGC_API_KEY` set in the environment (before the M2 and M4 runs) | not recorded (never in the repository) | M2, M4 |
| 13 | Delete the VM and boot disk, read the final cost, give both to the builder | `docs/cloudgpu-run.md` sections `Cost` and `Teardown` | AJ-132, AJ-140, AJ-142 |
| - | Fill the seven `owner: user` placeholders of `docs/devpost-feedback.md` | `docs/devpost-feedback.md` | M7 AJ-158 |
| - | Record the video (planned 165 s) and upload it as a public YouTube video | URL given to the builder for the checklist | M7 AJ-158 |
| - | `git push`, enable GitHub Pages, create the release | commands in `docs/devpost-checklist.md` | M7 AJ-157, AJ-158 |

## Stories

### AJ-130: Write the README pitch and architecture diagram
Covers: REQ-025
User story: As a hackathon judge, I want a README that opens with the pitch and an architecture diagram, so that I understand what Agent Joules measures within one minute.
Acceptance criteria:
1. `grep -c '^```mermaid$' README.md` prints 1, the block starts with `flowchart LR` and ends at a line of three backticks (checked by `DocumentChecks.MermaidBlock`, exit 0).
2. The diagram contains nodes for agents, metered clients, providers, the NVML meter, the sink, the results files, the report and OpenTelemetry: `awk '/^```mermaid$/{f=1;next}/^```$/{f=0}f' README.md | grep -ciE "agent|metered|provider|nvml|sink|results|report|opentelemetry"` prints 8 or more lines.
3. Line 3 of `README.md` is the text of the `## Tagline (60 characters)` section of `docs/devpost-pitch.md` (58 characters, plural "GPUs" because energy is measured on two GPUs): `grep -cF "Agent Joules: energy per agent step, measured on your GPUs" README.md docs/devpost-pitch.md` prints 1 for each file. When no completed or partial cloudgpu run file exists (AJ-141 rule), the singular form `Agent Joules: energy per agent step, measured on your GPU` (57 characters, the current README line) is used in README only and the change is logged in `docs/build-log.md`.
4. The status paragraph no longer says "work in progress" and the `What it measures` and `Architecture` sections no longer say "Filled during the build": `grep -ci "work in progress" README.md` prints 0.
5. The headings `What it measures`, `Architecture`, `Quick start`, `Results`, `License` exist, each exactly once: `grep -c "^## Architecture$" README.md` prints 1.
6. The file is ASCII only with LF endings: `grep -cP "[^\x00-\x7F]" README.md` prints 0 and `grep -c $'\r' README.md` prints 0.
BDD scenarios:
```gherkin
@unit @AJ-S130 @REQ-025
Feature: AJ-130 README pitch and architecture diagram
  @positive
  Scenario: AJ-130 README carries a valid Mermaid architecture block
    Given the committed README.md
    When the Mermaid block is extracted
    Then exactly one block exists and its first line is "flowchart LR"
    And it names agents, metered clients, providers, the NVML meter, the sink, results, the report and OpenTelemetry
  @positive
  Scenario: AJ-130 README pitch equals the tagline in the pitch file
    Given the committed README.md and docs/devpost-pitch.md and a completed cloudgpu run file
    When line 3 of the README is compared with the Tagline section
    Then both read "Agent Joules: energy per agent step, measured on your GPUs"
    And the text is 58 characters
  @positive
  Scenario: AJ-130 README uses the singular tagline when the cloudgpu tier did not run
    Given the committed README.md and no completed or partial cloudgpu run file
    When line 3 of the README is checked
    Then it reads "Agent Joules: energy per agent step, measured on your GPU"
    And the change is logged in docs/build-log.md
  @negative
  Scenario: AJ-130 README without a Mermaid fence is rejected
    Given a copy of the README with the mermaid fence line removed
    When the README structure check runs
    Then it fails with "missing mermaid block"
  @negative
  Scenario: AJ-130 README with a missing node in the diagram is rejected
    Given a copy of the README whose diagram has no OpenTelemetry node
    When the README structure check runs
    Then it fails with "diagram lacks: OpenTelemetry"
  @negative
  Scenario: AJ-130 README with a missing Architecture heading is rejected
    Given a copy of the README without the heading "## Architecture"
    When the README structure check runs
    Then it fails with "missing heading: Architecture"
  @negative
  Scenario: AJ-130 README with the old status text is rejected
    Given a copy of the README that still says "Filled during the build" under Architecture
    When the README structure check runs
    Then it fails with "leftover placeholder text: Filled during the build"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-130-T1 | Write the failing structure feature for README pitch and diagram | tests/AgentJoules.Specs/Features/Reporting/ReadmeStructure.feature | 0.25 | - |
| AJ-130-T2 | Add the `RepositoryRoot` helper and the `DocumentChecks` class with `MermaidBlock`, `HasHeading`, `NoLeftoverText` checks plus step bindings | tests/AgentJoules.TestSupport/RepositoryRoot.cs, tests/AgentJoules.Specs/Support/DocumentChecks.cs, tests/AgentJoules.Specs/Steps/ReportingSteps.cs | 0.75 | AJ-130-T1 |
| AJ-130-T3 | Rewrite the README opening: tagline line, status paragraph, `What it measures` | README.md | 0.5 | AJ-130-T2 |
| AJ-130-T4 | Draw the Mermaid architecture diagram from the project graph of the technical design | README.md | 0.5 | AJ-130-T3 |
| AJ-130-T5 | Record the diagram source and the pitch alignment in the build log | docs/build-log.md | 0.25 | AJ-130-T4 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/ReadmeStructure.feature
Docs impact: README.md sections What it measures and Architecture; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] Mermaid syntax passes the `flowchart LR` and bracket-balance check; the user confirms rendering on GitHub after the push (user action in M7)
Demo relevance: yes: shot 2 of the video shows this diagram.

### AJ-131: Write the README quick start, CLI reference and reproduce steps
Covers: REQ-025
User story: As a developer with an NVIDIA GPU, I want exact quick-start, CLI and reproduce steps, so that I can regenerate every number on my own machine; and as a judge without a GPU I want a path that needs neither a GPU nor a key.
Acceptance criteria:
1. The README contains the headings `Quick start`, `Judges without a GPU`, `CLI reference`, `Reproduce` and `Prior art`, each once: `for h in "Quick start" "Judges without a GPU" "CLI reference" "Reproduce" "Prior art"; do grep -c "^#\+ $h$" README.md; done` prints five lines of 1.
2. `Judges without a GPU` lists exactly four actions, none needing a key or a GPU: `dotnet test`, `AJ validate results/` (prints `Validated <n> files`), opening `docs/report/index.html`, and `AJ dashboard --replay results/<committed hosted run file> --speed 4 --exit-after 60` (the live view without a GPU). The extracted section contains no `_API_KEY`: the `awk` extraction piped to `grep -c "_API_KEY"` prints 0.
3. The quick start names the three key variables and both storage routes (`setx` and `dotnet user-secrets set ... --project src/AgentJoules.Cli`) and shows no key value: `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" README.md` returns no hits.
4. The CLI reference lists the six commands (`probe`, `run`, `report`, `validate`, `calibrate`, `dashboard`) and every option of the design table (section 3.6), including `run --dashboard [--dashboard-port <n>]` and `dashboard --replay <run file> [--speed <factor>] [--exit-after <seconds>]`. A short note states: the dashboard is off by default, stores nothing, and a browser on the measured GPU adds compositing load, so the published results files were recorded without a browser open and only the demo recording shows the live page. For each command, every long option named in the README appears in the output of `AJ <command> --help` (exit 0), checked by `CliHarness`.
5. This story creates the README exit-code table (AJ-131-T4 owns it; AJ-035 and AJ-058 only verify it or add rows): codes 0 to 7 with the meanings of the Objective of the prompt, including for 5 the interrupted run (Ctrl+C, SIGTERM or SIGHUP marks in-flight steps `incomplete`) and the case that no free loopback port is found for `dashboard --replay`. Check: `grep -cE "^\| [0-7] \|" README.md` prints 8, `grep -c "^| 0 |" README.md` prints 1 (the table exists once), and the CLI harness test maps each row to the exit code of `ExitCode.cs`.
6. `Reproduce` is an ordered list that matches the ten demo steps of AJ-135 command for command.
7. `Prior art` cites, with links, arXiv 2407.16893, TokenPowerBench (AAAI), "Watt Counts" arXiv 2604.09048 and the Python greentoken CLI, and scopes the claim "none attributes energy to an agent step" to the search of 2026-10-17. The links are re-checked on the day of writing and the date is recorded in `docs/build-log.md` (@integration scenarios).
8. The README links `docs/report/dashboard.png` (the screenshot of the live dashboard page, taken by the user during the AJ-135 rehearsal) under the dashboard usage text only when the file exists, and contains no link to it otherwise: `test -f docs/report/dashboard.png` and `grep -c "docs/report/dashboard.png" README.md` are both true or both false (a dangling link fails the check).
BDD scenarios:
```gherkin
@unit @AJ-S131 @REQ-025
Feature: AJ-131 README quick start, CLI reference and reproduce steps
  @positive
  Scenario: AJ-131 Judges without a GPU needs no key and no GPU
    Given the committed README.md
    When the section "Judges without a GPU" is extracted
    Then it contains "dotnet test", "validate results/" and "docs/report/index.html"
    And it contains no "_API_KEY" text
  @positive
  Scenario: AJ-131 Every documented option exists in the CLI help
    Given the CLI reference table of the README
    When "run --help" is executed in process
    Then the exit code is 0
    And the help text contains each option named for run: --tier --task --mode --out --baseline-seconds --repeat --dry-run
  @positive
  Scenario: AJ-131 Exit-code table is complete
    Given the committed README.md
    When the exit-code table is parsed
    Then it has the codes 0 1 2 3 4 5 6 7 each once
  @negative
  Scenario: AJ-131 README that documents a nonexistent option is rejected
    Given a copy of the README whose run options include "--fast"
    When the option check runs against "run --help"
    Then it fails with "option not in help: --fast"
  @negative
  Scenario: AJ-131 README with a key value is rejected
    Given a copy of the README containing a runtime-built "nvapi-" plus 16 characters value
    When the secret check runs on the file
    Then it fails with "key material in README.md"
  @negative
  Scenario: AJ-131 README missing the Judges without a GPU heading is rejected
    Given a copy of the README without that heading
    When the README structure check runs
    Then it fails with "missing heading: Judges without a GPU"
  @negative
  Scenario: AJ-131 Judges section that asks for a key is rejected
    Given a copy of the README whose Judges without a GPU section mentions NVIDIA_API_KEY
    When the README structure check runs
    Then it fails with "judges section needs a key"
  @positive
  Scenario: AJ-131 The dashboard screenshot is linked when the file exists
    Given a workspace where docs/report/dashboard.png exists
    When the README link check runs
    Then the README links docs/report/dashboard.png under the dashboard usage text
  @negative
  Scenario: AJ-131 A link to a missing dashboard screenshot is rejected
    Given a README that links docs/report/dashboard.png and a workspace where the file is absent
    When the README link check runs
    Then it fails with "dangling link: docs/report/dashboard.png"

@integration @AJ-S131 @REQ-025
Feature: AJ-131 Prior-art citations resolve
  @positive
  Scenario: AJ-131 Each prior-art link answers
    Given the links in the Prior art section
    When each link is requested
    Then each answers HTTP 200 and the check date is written to docs/build-log.md
  @negative
  Scenario: AJ-131 A dead prior-art link is reported
    Given a link in the Prior art section that answers HTTP 404
    When the link check runs
    Then it fails naming the URL and the README is not marked verified
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-131-T1 | Write the failing guide feature (sections, key-free judges path, option and exit-code checks) | tests/AgentJoules.Specs/Features/Reporting/ReadmeGuides.feature | 0.5 | AJ-130-T2 |
| AJ-131-T2 | Add the option-versus-help check and the exit-code table parser to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.75 | AJ-131-T1 |
| AJ-131-T3 | Write `Quick start` and `Judges without a GPU` | README.md | 0.75 | AJ-131-T2 |
| AJ-131-T4 | Create the exit-code table (codes 0 to 7 with the interrupted-run and dashboard-port wording of exit 5) and write `CLI reference`, the dashboard options and the browser-load note, copying option names from the help output, and link `docs/report/dashboard.png` under the dashboard usage text only when the file exists | README.md | 0.75 | AJ-131-T3 |
| AJ-131-T5 | Write `Reproduce` from the ten demo steps and `Prior art` with checked links | README.md | 0.75 | AJ-131-T4 |
| AJ-131-T6 | Record the citation check date and any dead link in the build log | docs/build-log.md | 0.25 | AJ-131-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/ReadmeGuides.feature
Docs impact: README.md sections Quick start, Judges without a GPU, CLI reference, Reproduce, Prior art; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] every command in `Reproduce` that needs no GPU and no key was executed once by the builder and its exit code recorded in `docs/build-log.md`
Demo relevance: yes: the commands in this README are the commands shown in shot 3 and shot 4.

### AJ-132: Write the README sections the Devpost form requires
Covers: REQ-025
User story: As a hackathon judge, I want the README to state how Nemotron, Nebius Token Factory and other Nebius services were used and which local runtime produced the numbers, so that I can score the technology criteria without opening other files.
Acceptance criteria:
1. The headings `How this project uses NVIDIA Nemotron`, `Where Nebius Token Factory accelerated the workflow`, `Other Nebius tools or services used` and `Judges without a GPU` each occur once: `grep -c "^#\+ How this project uses NVIDIA Nemotron$" README.md` prints 1, likewise for the other three.
2. Every model ID or tag named in the README appears as a `- model: <id>` line in `docs/endpoints.md` and `AJ validate --config` exits 0. Every results file named in these sections exists (`test -f` over the extracted names exits 0).
3. The README contains the sentence `Local numbers were produced with <runtime> running <tag>.` with `<runtime>` and `<tag>` equal to `environment.localRuntime` and the model tag of the latest local results file (sentence defined by the local-runtime requirement of M4).
4. The section `NIM-local outcome` links `docs/nim-local-attempt.md` and states the outcome in the words of that file's `Decision` section; if the decision records a fallback, the README says NIM-local was attempted and why it was not used.
5. `Other Nebius tools or services used` names the Nebius AI Cloud VM with preset, region, GPU-hours and observed cost, each copied from `docs/cloudgpu-run.md` with the file named, when a completed or partial cloudgpu run file exists. Otherwise it contains exactly the sentence `None besides Token Factory.` and the README labels the tier `cloudgpu | not run`. The README contains the string `cloudgpu` in both cases.
6. The methodology summary links `docs/methodology.md`; the seven honesty rules of prompt section 6 appear in short form, one line each; the judge model ID is named, judge calls are stated as excluded from workflow totals, and self-judging is disclosed when `run.selfJudged` is true in the latest file.
7. `grep -c "Filled during the build" README.md` prints 0 after this story.
BDD scenarios:
```gherkin
@unit @AJ-S132 @REQ-025
Feature: AJ-132 README sections required by the Devpost form
  @positive
  Scenario: AJ-132 Required headings and sentences are present
    Given the committed README.md and the latest local results file
    When the Devpost sections are extracted
    Then the four required headings occur once each
    And the sentence "Local numbers were produced with" names the runtime and tag of the results file
  @positive
  Scenario: AJ-132 Model IDs in the README exist in the endpoints file
    Given every model ID named in README.md
    When they are compared with the "- model:" lines of docs/endpoints.md
    Then each ID is listed and "validate --config" exits 0
  @positive
  Scenario: AJ-132 Cloud section without a cloudgpu run uses the fixed sentence
    Given a workspace with no completed cloudgpu run file
    When the README "Other Nebius tools or services used" section is checked
    Then it reads "None besides Token Factory." and the README labels the cloudgpu tier "not run"
  @negative
  Scenario: AJ-132 Missing required heading is rejected
    Given a copy of the README without "Where Nebius Token Factory accelerated the workflow"
    When the README structure check runs
    Then it fails with "missing heading: Where Nebius Token Factory accelerated the workflow"
  @negative
  Scenario: AJ-132 Model ID absent from the endpoints file is rejected
    Given a copy of the README naming the model ID "nemotron-invented"
    When the model-ID check runs
    Then it fails with "model id not in docs/endpoints.md: nemotron-invented"
  @negative
  Scenario: AJ-132 Cloud cost without a source file is rejected
    Given a copy of the README stating an observed cost with no "docs/cloudgpu-run.md" reference
    When the source-line check runs
    Then it fails with "number without source: observed cost"
  @negative
  Scenario: AJ-132 Leftover placeholder text is rejected
    Given a copy of the README that still contains "Filled during the build"
    When the README structure check runs
    Then it fails with "leftover placeholder text"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-132-T1 | Write the failing feature for the Devpost-form sections and the model-ID check | tests/AgentJoules.Specs/Features/Reporting/ReadmeDevpostSections.feature | 0.5 | AJ-130-T2 |
| AJ-132-T2 | Add the model-ID and source-reference checks to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.5 | AJ-132-T1 |
| AJ-132-T3 | Write `How this project uses NVIDIA Nemotron` and `Where Nebius Token Factory accelerated the workflow` from endpoints.md and the run files | README.md | 0.75 | AJ-132-T2 |
| AJ-132-T4 | Write `Other Nebius tools or services used` and `NIM-local outcome` from cloudgpu-run.md and nim-local-attempt.md | README.md | 0.75 | AJ-132-T3 |
| AJ-132-T5 | Write the methodology summary, the short honesty rules, the judge disclosure and the local-runtime sentence | README.md | 0.75 | AJ-132-T4 |
| AJ-132-T6 | Record the source file of each README fact in the build log | docs/build-log.md | 0.25 | AJ-132-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/ReadmeDevpostSections.feature
Docs impact: README.md; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the README sentences about the cloudgpu tier match the tier outcome (completed, partial or not run) stated in `docs/cloudgpu-run.md`
Demo relevance: yes: shot 7 shows the `Other Nebius tools or services used` section (or the not-run fallback).

### AJ-144: Build the `report` flags `--update-readme`, `--verify-readme`, `--render-content` and the content template renderer
Covers: REQ-025, REQ-026, REQ-016
User story: As the author of the README and the LinkedIn posts, I want `AJ report` to update and verify the README results block and to render the post template from the latest results files, so that no number in either file is typed by hand and a manual edit is detected.
Acceptance criteria:
1. `AJ report --help` exits 0 and lists `--in`, `--out`, `--update-readme`, `--verify-readme`, `--render-content` and `--config` (design section 3.6); `AJ report --in results/ --out docs/report/` without the new flags keeps its previous behaviour and exits 0.
2. `AJ report --update-readme` replaces only the text between `<!-- results:begin -->` and `<!-- results:end -->` with `ReadmeResultsBlock.Render(latestPerTier)` (the render function of AJ-087) and writes LF line endings; a second run leaves `git diff --exit-code README.md` at exit 0 once the first result is committed.
3. `AJ report --verify-readme` exits 0 when the marked block equals the generated block and exits 7 after any manual edit inside the markers; the comparison normalizes CRLF to LF on both sides, so a CRLF checkout of the same block verifies with exit 0 (design review M-02). A missing marker line exits 2 and names the marker.
4. `AJ report --render-content` reads `content/linkedin-posts.template.md`, resolves every `{{results.<tier>.<dot.path>}}` against the latest completed or partial run file of that tier (arrays as `[index]`, label objects addressed as `.value`, optional `|F0` or `|F1` invariant format) and writes `content/linkedin-posts.md`; `{{repo.url}}` is left unresolved; the first line is `Sources:` followed by the names of the run files used.
5. A value that is null (label `N/A` or `not-reported`) renders as its label text, never as `0`, `null` or an empty string (REQ-026 as amended 2026-10-18).
6. A placeholder path missing from the results file, or a tier with no completed or partial run file, exits 2 and names the placeholder or the tier; a rendered variant above 1 300 characters exits 2 and names the variant and its length; variants are split on the `## Variant:` headings.
7. The three flags are implemented in `src/AgentJoules.Cli/Commands/ReportCommand.cs` and the renderer in `src/AgentJoules.Reporting/Content/ContentTemplateRenderer.cs`; the unit tests run in `ReadmeBlockTests` and `ContentRenderingTests` with no GPU, no network and no key.
BDD scenarios:
```gherkin
@unit @AJ-S144 @REQ-025 @REQ-026 @REQ-016
Feature: AJ-144 report flags and the content template renderer
  @positive
  Scenario: AJ-144 Update then verify exits 0 and a second update changes nothing
    Given a workspace with latest results files for local, nvidia and nebius and a README with markers
    When "report --update-readme" runs twice and then "report --verify-readme" runs
    Then the second update changes no byte and verify exits 0
  @positive
  Scenario: AJ-144 Verify accepts a CRLF checkout of the same block
    Given an updated README converted to CRLF line endings
    When "report --verify-readme" runs
    Then the exit code is 0
  @positive
  Scenario: AJ-144 Render writes the variants with a Sources line first
    Given a workspace with the template and latest results files for local and nvidia
    When "report --render-content" runs
    Then the first line of content/linkedin-posts.md starts with "Sources:" and names both run files
    And "{{results." occurs 0 times and "{{repo.url}}" occurs once per variant
  @positive
  Scenario: AJ-144 An indexed path with the value suffix and a format resolves
    Given a placeholder "{{results.local.steps[0].netJoules.value|F1}}" and a run file whose first step has net joules 12.34 labelled measured
    When the placeholder is rendered
    Then the output reads "12.3"
  @positive
  Scenario: AJ-144 A null label value renders as its label text
    Given a placeholder for the nvidia gross joules whose value is null with label "N/A"
    When the placeholder is rendered
    Then the output reads "N/A" and never "0", "null" or an empty string
  @negative
  Scenario: AJ-144 A manual edit inside the markers exits 7
    Given an updated README whose results block has one number edited by hand
    When "report --verify-readme" runs
    Then the exit code is 7
  @negative
  Scenario: AJ-144 A README without the begin marker exits 2
    Given a README without the line "<!-- results:begin -->"
    When "report --update-readme" runs
    Then the exit code is 2 and the message names the marker
  @negative
  Scenario: AJ-144 A placeholder path missing from the results file exits 2
    Given a template placeholder "{{results.local.totals.joulesPerToken.value}}" that the file lacks
    When "report --render-content" runs
    Then the exit code is 2 and the message names the placeholder
  @negative
  Scenario: AJ-144 A tier with no completed or partial run file exits 2
    Given a template placeholder for cloudgpu and no completed or partial cloudgpu run file
    When "report --render-content" runs
    Then the exit code is 2 and the message names the tier "cloudgpu"
  @negative
  Scenario: AJ-144 A variant over 1300 characters exits 2
    Given a template variant whose rendered text is 1301 characters
    When "report --render-content" runs
    Then the exit code is 2 and the message names the variant and its length
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-144-T1 | Write the failing feature for update, verify, CRLF, render, null value and the exit codes | tests/AgentJoules.Specs/Features/Reporting/ReportFlags.feature | 0.5 | AJ-130-T2 |
| AJ-144-T2 | Write the failing unit cases for block update, verify (exit 7), placeholder resolution and variant length | tests/AgentJoules.UnitTests/ReadmeBlockTests.cs, tests/AgentJoules.UnitTests/ContentRenderingTests.cs | 0.75 | AJ-144-T1 |
| AJ-144-T3 | Add `Update` and `Verify` with LF normalization to the results block | src/AgentJoules.Reporting/Readme/ReadmeResultsBlock.cs | 1.25 | AJ-144-T2 |
| AJ-144-T4 | Implement `ContentTemplateRenderer` (placeholders, `[index]`, `.value`, formats, null as label text, Sources line, variant split and length check) | src/AgentJoules.Reporting/Content/ContentTemplateRenderer.cs | 2.0 | AJ-144-T2 |
| AJ-144-T5 | Wire `--update-readme`, `--verify-readme` and `--render-content` into the report command with the exit codes 0, 2 and 7 | src/AgentJoules.Cli/Commands/ReportCommand.cs | 1.0 | AJ-144-T3, AJ-144-T4 |
| AJ-144-T6 | Create or extend `ExitCodeTests` with the exit-code 7 case and run the feature green | tests/AgentJoules.UnitTests/ExitCodeTests.cs | 0.5 | AJ-144-T5 |
| AJ-144-T7 | Record the flags and their exit codes in the build log | docs/build-log.md | 0.25 | AJ-144-T6 |
Test mapping: ReadmeBlockTests, ContentRenderingTests, ExitCodeTests (code 7); tests/AgentJoules.Specs/Features/Reporting/ReportFlags.feature
Docs impact: docs/build-log.md; README.md CLI reference (option names, written in AJ-131)
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `AJ report --update-readme --render-content` runs once on the committed results and exits 0, then `AJ report --verify-readme` exits 0
Demo relevance: yes: demo steps 7 and 8 run these flags.

### AJ-133: Generate and verify the README results block
Covers: REQ-025
User story: As a reader, I want a results table that the tool generated from the latest results files, so that no number in it was typed by hand and I can regenerate it.
Acceptance criteria:
1. After `AJ report --update-readme`, the text between `<!-- results:begin -->` and `<!-- results:end -->` has one row per tier and GPU for `local`, `cloudgpu`, `nvidia`, `nebius`, in that order; the cloudgpu row reads `cloudgpu | not run` when no completed or partial cloudgpu file exists.
2. The block header names GPU, driver version, run date (UTC), local runtime and the model IDs with precision, and the block contains the sentence `Energy is GPU-board energy only.`
3. Hosted rows show `N/A` for energy, never 0; a sampling-method tier row contains `sampling`; a budget-stopped cloudgpu row contains `partial`.
4. `AJ report --verify-readme` exits 0 after the update; after one manual edit inside the markers it exits 7; once the generated block is committed, a second `AJ report --update-readme` leaves `git diff --exit-code README.md` at exit 0.
5. Cloudgpu figures and laptop figures stay in separate rows and no sentence averages across tiers: `grep -ci "average of" README.md` prints 0.
6. `AJ report --in results/ --out docs/report/` leaves `docs/report/index.html` with `grep -c "<script" docs/report/index.html` printing 0 and `docs/.nojekyll` present.
BDD scenarios:
```gherkin
@unit @AJ-S133 @REQ-025
Feature: AJ-133 README results block
  @positive
  Scenario: AJ-133 Update then verify succeeds and is idempotent
    Given a workspace with latest results files for local, nvidia and nebius and a README with markers
    When "report --update-readme" runs twice and then "report --verify-readme" runs
    Then the second update changes nothing and verify exits 0
  @positive
  Scenario: AJ-133 Block shows one row per tier with cloudgpu not run
    Given a workspace without a cloudgpu run file
    When "report --update-readme" runs
    Then the block has rows for local, nvidia and nebius and the row "cloudgpu | not run"
    And hosted rows show "N/A" for energy and never "0"
  @negative
  Scenario: AJ-133 Manual edit inside the markers is detected
    Given an updated README whose results block has one number edited by hand
    When "report --verify-readme" runs
    Then the exit code is 7
  @negative
  Scenario: AJ-133 Missing markers are reported
    Given a README without the line "<!-- results:begin -->"
    When "report --update-readme" runs
    Then the exit code is 2 and the message names the missing marker
  @positive
  Scenario: AJ-133 Block with a partial cloudgpu run says partial
    Given a latest cloudgpu file with run.status "partial" and stopReason "cost-cap"
    When "report --update-readme" runs
    Then the cloudgpu row contains "partial"
  @negative
  Scenario: AJ-133 A hosted row with a zero energy value is rejected
    Given a fixture where a hosted step has energy value 0 and label "measured"
    When the results are validated
    Then validation fails and no README block is written
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-133-T1 | Write the failing feature for update, verify, idempotence and the not-run row | tests/AgentJoules.Specs/Features/Reporting/ReadmeResultsBlock.feature | 0.5 | AJ-130-T2, AJ-144-T6 |
| AJ-133-T2 | Add the partial-row and not-run-row cases that AJ-144 does not cover | tests/AgentJoules.UnitTests/ReadmeBlockTests.cs | 0.5 | AJ-133-T1 |
| AJ-133-T3 | Run `AJ report --in results/ --out docs/report/ --update-readme` on the final results and inspect the block | README.md, docs/report/summary.md, docs/report/joules-per-step.svg, docs/report/index.html | 0.5 | AJ-133-T2 |
| AJ-133-T4 | Run verify, edit one number, confirm exit 7, restore, confirm exit 0, record the exit codes | docs/build-log.md | 0.25 | AJ-133-T3 |
Test mapping: ReadmeBlockTests; tests/AgentJoules.Specs/Features/Reporting/ReadmeResultsBlock.feature
Docs impact: README.md results block (generated); docs/report/*; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the generated block is committed together with the results files it was generated from
Demo relevance: yes: shot 8 shows the results block and the honesty sentence.

### AJ-134: Write and render the three LinkedIn post drafts
Covers: REQ-026
User story: As the author posting to LinkedIn, I want three post variants whose numbers are placeholders resolved from the latest results files, so that every number I post traces to a committed file and no variant exceeds the length limit.
Acceptance criteria:
1. `content/linkedin-posts.template.md` has exactly three headings `## Variant: launch`, `## Variant: lesson-learned` and `## Variant: hackathon`: `grep -c "^## Variant: " content/linkedin-posts.template.md` prints 3.
2. Each variant contains at least one `{{results.` placeholder, the string `GPU`, the unresolved placeholder `{{repo.url}}` exactly once, and the scope sentence of the prompt (REQ-026): a variant that cites only laptop numbers contains `GPU-board energy measured on one laptop`; a variant that contains a `{{results.cloudgpu.` placeholder contains `GPU-board energy measured on one laptop and on one Nebius AI Cloud GPU, reported separately`. The template test accepts exactly these two sentences and fails any variant that carries a cloudgpu placeholder with the first one only.
3. No quantity is typed by hand: after removing every `{{...}}` token, no text matching `[0-9]+(\.[0-9]+)? ?(J|W|ms|tokens|%|EUR|USD)` remains in the template.
4. Every placeholder that resolves to a metric value is followed within 40 characters by its label word (`measured`, `estimated` or `model-judged`) and a unit; a value that resolves to null renders as the label text `N/A` or `not-reported`, never `0`, `null` or an empty string.
5. `AJ report --render-content` exits 0 and writes `content/linkedin-posts.md` whose first line starts `Sources:` and lists run files that exist: `grep -c "{{results\." content/linkedin-posts.md` prints 0 and `grep -c "{{repo.url}}" content/linkedin-posts.md` prints 3.
6. Each rendered variant is at most 1 300 characters: the `awk` split on `## Variant:` piped to `wc -m` prints three values of 1300 or less (`ContentRenderingTests`).
7. A placeholder path missing from the results file makes `AJ report --render-content` exit 2 naming the placeholder. A variant that cites a cloudgpu placeholder while no completed or partial cloudgpu file exists fails the same way, and the cut-off plan of M7 removes those placeholders.
8. The opening hook of each variant is one of the three candidate hooks of prompt section 1 or a refinement of one, and contains no number. Check: the template test matches the first sentence of each variant against the three hooks of prompt section 1 or a refinement recorded in `docs/build-log.md`, and `grep -c "[0-9]"` on the three hook lines prints 0.
BDD scenarios:
```gherkin
@unit @AJ-S134 @REQ-026
Feature: AJ-134 LinkedIn post drafts
  @positive
  Scenario: AJ-134 Three variants render within the length limit
    Given a workspace with latest results files for local, nvidia and nebius and the committed template
    When "report --render-content" runs
    Then the exit code is 0
    And the output has three variants of 1300 characters or fewer
    And the first line starts with "Sources:" and names run files that exist
  @positive
  Scenario: AJ-134 Every number is a labelled placeholder
    Given the committed template
    When hand-typed quantities are searched outside placeholders
    Then none are found
    And each metric placeholder is followed by a unit and one of measured, estimated, model-judged
  @positive
  Scenario: AJ-134 A null hosted energy value renders as N/A
    Given a results fixture where the nvidia energy metric has value null and label "N/A"
    When a placeholder for that metric is rendered
    Then the output reads "N/A" and never "0", "null" or an empty string
  @negative
  Scenario: AJ-134 A placeholder path missing from the results file fails the render
    Given a template placeholder "{{results.local.totals.joulesPerToken.value}}" that the file lacks
    When "report --render-content" runs
    Then the exit code is 2 and the message names the placeholder
  @negative
  Scenario: AJ-134 A variant over 1300 characters fails the render
    Given a template variant whose rendered text is 1301 characters
    When "report --render-content" runs
    Then the exit code is 2 and the message names the variant and its length
  @negative
  Scenario: AJ-134 A hand-typed number in the template is rejected
    Given a copy of the template containing "uses 412 J per run"
    When the template check runs
    Then it fails with "number without source: 412 J"
  @negative
  Scenario: AJ-134 A cloudgpu placeholder without a cloudgpu run file fails
    Given a workspace without a completed cloudgpu run file and a variant citing a cloudgpu placeholder
    When "report --render-content" runs
    Then the exit code is 2 and the message names the cloudgpu placeholder
  @negative
  Scenario: AJ-134 A variant without the repo placeholder is rejected
    Given a copy of the template whose launch variant lacks "{{repo.url}}"
    When the template check runs
    Then it fails with "missing {{repo.url}} in variant: launch"
```
Resolved: prompt REQ-026 (as amended) defines both scope sentences (`GPU-board energy measured on one laptop`, and with cloudgpu `GPU-board energy measured on one laptop and on one Nebius AI Cloud GPU, reported separately`); the template test accepts exactly these two, no proposed wording of this plan is used.
Resolved: the rendering of a null metric value (label `N/A` or `not-reported`, addressed as `.value`) is defined by REQ-026 as amended on 2026-10-18 (the label text, never 0) and is implemented and tested in AJ-144; this story only adds template-level cases.
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-134-T1 | Write the failing feature and the failing unit cases for render, length, missing path and null value | tests/AgentJoules.Specs/Features/Reporting/LinkedInPosts.feature, tests/AgentJoules.UnitTests/ContentRenderingTests.cs | 0.5 | AJ-130-T2, AJ-144-T4 |
| AJ-134-T2 | Add the hand-typed-quantity and label-adjacency checks to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.5 | AJ-134-T1 |
| AJ-134-T3 | Add the template-level null-value and cloudgpu-absent cases against the AJ-144 renderer | tests/AgentJoules.UnitTests/ContentRenderingTests.cs | 0.5 | AJ-134-T1, AJ-144-T4 |
| AJ-134-T4 | Write the `launch` variant with its placeholders, verified paths and the required sentence | content/linkedin-posts.template.md | 0.5 | AJ-134-T2 |
| AJ-134-T5 | Write the `lesson-learned` and `hackathon` variants | content/linkedin-posts.template.md | 0.75 | AJ-134-T4 |
| AJ-134-T6 | Render the posts with `AJ report --render-content`, trim any variant above 1 300 characters, and commit the rendered file | content/linkedin-posts.md | 0.5 | AJ-134-T3, AJ-134-T5, AJ-144-T5 |
| AJ-134-T7 | Record each placeholder path and its source file in the build log | docs/build-log.md | 0.25 | AJ-134-T6 |
Test mapping: ContentRenderingTests; tests/AgentJoules.Specs/Features/Reporting/LinkedInPosts.feature
Docs impact: content/linkedin-posts.template.md, content/linkedin-posts.md; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the rendered file keeps `{{repo.url}}` three times for the user to fill before posting (user action in M7)
Demo relevance: no.

### AJ-135: Demo program end to end
Covers: REQ-027, REQ-028
User story: As a hackathon judge, I want one defined demo that runs from probe to a published report, so that I see exactly which commands produce which committed artifacts and what the video shows.
Acceptance criteria:
1. The demo is defined in the Demo definition block below this story. `content/demo-script.md` has a section `## Demo program` with a fenced block holding the ten commands of the Demo definition, in that order, each starting with `dotnet run --project src/AgentJoules.Cli --` except step 5, which starts with `./AgentJoules.Cli`.
2. `AJ <command> --help` exits 0 for each of `probe`, `run`, `report`, `validate`, `calibrate`, `dashboard` (in-process through `CliHarness`; also by hand: six exit codes recorded in `docs/build-log.md`).
3. The task set is the three files `tasks/01-incident-brief.json`, `tasks/02-api-migration.json`, `tasks/03-energy-explainer.json`: `ls tasks/*.json | wc -l` prints 3. The tiers of step 3 are `local`, `nvidia` and `nebius`; each of those three run files has `run.repeat` 3 and 36 steps (3 tasks x 3 repeats x 4 agents), checked by parsing the committed files.
4. `AJ validate results/` exits 0 and prints `Validated <n> files` where `<n>` equals the number of committed run files matching `^[0-9]{8}T[0-9]{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$` (probe-* and calibration-* files are not counted, AJ-080 criterion 3).
5. Every artifact in the artifact table of the Demo definition exists (`test -f` loop exits 0) or, for the three cloudgpu artifacts, the section `If the cloudgpu tier is absent` exists in the script and `AJ report --in results/ --out docs/report/` exits 0 with a `cloudgpu | not run` row.
6. The Try it out URL is the report page: the script and `docs/devpost-checklist.md` (AJ-138) name `https://tomas-rampas.github.io/agent-joules/report/` (inferred from the standard GitHub Pages project URL, confirmed by the user after Pages is enabled), and `docs/report/index.html` exists with `grep -c "<script" docs/report/index.html` printing 0.
7. The shot table of the Demo definition appears in the script with the same order and seconds; AJ-136 owns its wording. Check: the `DemoDefinition` parser compares shot number, start and duration of both tables and exits 0 only when all nine rows are equal.
8. The dashboard shots work: `AJ run --tier local --dashboard --dry-run` prints a line starting `Dashboard:` containing `http://127.0.0.1:` and exits 0 without a browser; `AJ dashboard --replay results/<ts>-nebius.json --speed 50 --exit-after 5` exits 0 and emits one event per step of that file (replay is the fallback when no GPU or local runtime is available at recording time, and its banner is visible in shot 5). The script and the README state that the published results files were recorded without a browser open and only the demo recording shows the live page (the load of a browser tab on the measured GPU is a stated limitation).
9. The live run shown in the video writes to `artifacts/demo-run` and `git check-ignore artifacts/demo-run/x.json` prints the path; no file from it is under `results/`.
10. The rehearsal run with `--dashboard` produces a screenshot of the live dashboard page, taken by the user (USER ACTION, AJ-135-T6) and saved as `docs/report/dashboard.png`: `test -f docs/report/dashboard.png` exits 0 and `file docs/report/dashboard.png` reports a PNG image. Until the user delivers it the item is listed as an open user action, not as a failure, and the README carries no link to it (AJ-131 criterion 8).
BDD scenarios:
```gherkin
@unit @AJ-S135 @REQ-027 @REQ-028
Feature: AJ-135 Demo program end to end
  @positive
  Scenario: AJ-135 The demo section lists the ten commands in order
    Given the committed content/demo-script.md
    When the fenced block under "## Demo program" is parsed
    Then it holds ten commands whose verbs are probe, run, run, calibrate, cloudgpu, validate, report, report, run, dashboard in that order
  @positive
  Scenario: AJ-135 Replay of a committed hosted run emits one event per step
    Given a committed nebius run file fixture with 36 steps
    When "dashboard --replay <file> --speed 50 --exit-after 5" runs in process
    Then the exit code is 0 and 36 events were emitted, one per step
  @negative
  Scenario: AJ-135 A missing replay file is rejected
    Given no file at results/missing.json
    When "dashboard --replay results/missing.json" runs
    Then the exit code is not 0 and the message names the file
  @negative
  Scenario: AJ-135 A script without the browser-load statement is rejected
    Given a copy of the script without the sentence about recording without a browser open
    When the demo check runs
    Then it fails with "missing statement: published results recorded without a browser"
  @positive
  Scenario: AJ-135 Every demo command answers help
    Given the six CLI commands used by the demo
    When each is run with "--help" in process
    Then every exit code is 0
  @positive
  Scenario: AJ-135 The three laptop and hosted run files have the demo shape
    Given the latest committed run files for local, nvidia and nebius
    When run.repeat and the step counts are read
    Then each file has run.repeat 3 and 36 steps and no step energy is written as 0 where the label is N/A
  @positive
  Scenario: AJ-135 Without a cloudgpu run the report shows a not-run row
    Given a workspace with run files for local, nvidia and nebius only
    When "report --in results/ --out docs/report/" runs
    Then the exit code is 0 and summary.md contains the row "cloudgpu | not run"
  @negative
  Scenario: AJ-135 A demo command with an unknown verb is rejected
    Given a copy of the script whose block contains "dotnet run --project src/AgentJoules.Cli -- bench"
    When the demo check runs
    Then it fails with "unknown command: bench"
  @negative
  Scenario: AJ-135 A missing demo artifact is reported by path
    Given a workspace where docs/report/index.html is deleted
    When the artifact check runs
    Then it fails with "missing artifact: docs/report/index.html"
  @negative
  Scenario: AJ-135 A run file with fewer than three repeats is rejected
    Given a latest local run file with run.repeat 1
    When the demo shape check runs
    Then it fails with "run.repeat 1 is below 3: local"
  @negative
  Scenario: AJ-135 A script without the cloudgpu fallback section is rejected
    Given a copy of the script without the heading "If the cloudgpu tier is absent"
    When the demo check runs
    Then it fails with "missing heading: If the cloudgpu tier is absent"
  @positive
  Scenario: AJ-135 The rehearsal screenshot is a PNG at the fixed path
    Given a workspace where docs/report/dashboard.png exists and starts with the PNG signature
    When the demo artifact check runs
    Then it passes for the screenshot
  @negative
  Scenario: AJ-135 A screenshot saved under another name is rejected
    Given a workspace where the screenshot is saved as docs/report/dash.png and docs/report/dashboard.png is absent
    When the demo artifact check runs
    Then it reports "open user action: docs/report/dashboard.png" and the README has no dashboard link

@integration @AJ-S135 @REQ-027 @REQ-028
Feature: AJ-135 Demo rehearsal with the laptop and keys
  @positive
  Scenario: AJ-135 Dry run of all enabled tiers prints OK
    Given NVIDIA_API_KEY and NEBIUS_API_KEY are set and the local runtime is up
    When "run --tier all --dry-run" runs
    Then the exit code is 0 and each enabled tier prints its endpoint, matched model IDs and "OK"
  @negative
  Scenario: AJ-135 Dry run without a key exits 3 and names the variable
    Given NEBIUS_API_KEY is unset
    When "run --tier all --dry-run" runs
    Then the exit code is 3 and the output names NEBIUS_API_KEY and prints no key value
```
Conflict: REQ-027 and design section 10 show `AJ run --tier local --task energy-explainer --repeat 1 --dashboard` in the video. With the default `--out results/` that command writes a repeat-1 run file that, being the newest, becomes the "latest results file for tier local" used by README, posts and the story, and it breaks the Definition of Done box "Run files carry run.repeat >= 3 for every tier". Proposed resolution: the command shown in the video and listed in the script adds `--out artifacts/demo-run` (keeping `--dashboard`); `artifacts/` is git-ignored (AJ-135-T2 checks `.gitignore`); the Demo definition lists it as an illustrative run that is the source of no published figure.
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-135-T1 | Write the failing demo feature (order, help, shape, fallback, missing artifact) | tests/AgentJoules.Specs/Features/Reporting/DemoProgram.feature | 0.5 | AJ-130-T2 |
| AJ-135-T2 | Add `DemoDefinition` parsing and the artifact and run-shape checks to DocumentChecks; ensure `artifacts/` is in `.gitignore` | tests/AgentJoules.Specs/Support/DocumentChecks.cs, .gitignore | 0.75 | AJ-135-T1 |
| AJ-135-T3 | Write the `## Demo program` section: ten commands, task set, tiers, artifact table, order of the video, fallback section | content/demo-script.md | 1.0 | AJ-135-T2 |
| AJ-135-T4 | Rehearse steps 2, 6, 7, 8, the dashboard dry run, the replay and the six `--help` calls, then record the exit codes | docs/build-log.md | 0.75 | AJ-135-T3, AJ-144-T6, AJ-133-T3 |
| AJ-135-T5 | Check that every artifact in the table exists or is covered by the fallback and record the result | docs/build-log.md | 0.25 | AJ-135-T4 |
| AJ-135-T6 | USER ACTION: take a screenshot of the live dashboard page during the rehearsal run with `--dashboard` and save it as `docs/report/dashboard.png` | docs/report/dashboard.png (user) | 0.25 | AJ-135-T4 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/DemoProgram.feature; ReportRenderingTests (not-run row)
Docs impact: content/demo-script.md; docs/build-log.md; .gitignore
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] steps 2, 6, 7, 8 and 10 and the dashboard dry run of step 9 rehearsed with exit codes in `docs/build-log.md`; steps 1, 3, 4, 5 verified through their committed artifacts
Demo relevance: yes: this story defines the demo.

Demo definition (binding for AJ-135, AJ-136, AJ-138 and AJ-141):

| Step | Command (`AJ` = `dotnet run --project src/AgentJoules.Cli --`) | Produces | Evidence label |
|---|---|---|---|
| 1 | `AJ probe --json --save` | `results/probe-<ts>.json` | measured |
| 2 | `AJ run --tier all --dry-run` | console line per enabled tier: endpoint, matched model IDs, `OK`; no file | measured (console) |
| 3 | `AJ run --tier all` | `results/<ts>-local.json`, `results/<ts>-nvidia.json`, `results/<ts>-nebius.json` | measured (laptop), tokens and latency measured (hosted), energy N/A (hosted) |
| 4 | `AJ calibrate` | `results/calibration-<ts>.json` | measured |
| 5 (on the VM, if M5 ran) | `./AgentJoules.Cli probe --json --save --suffix cloudgpu`, then `./AgentJoules.Cli run --tier cloudgpu`, then `./AgentJoules.Cli calibrate` | `results/probe-<ts>-cloudgpu.json`, `results/<ts>-cloudgpu.json`, `results/calibration-<ts>-cloudgpu.json` | measured (VM GPU) |
| 6 | `AJ validate results/` | `Validated <n> files`, exit 0 | measured (console) |
| 7 | `AJ report --update-readme --render-content` | `docs/report/summary.md`, `docs/report/joules-per-step.svg`, `docs/report/index.html`, `docs/.nojekyll`, README results block, `content/linkedin-posts.md` | derived from files |
| 8 | `AJ report --verify-readme` | exit 0 | measured (console) |
| 9 (video) | `AJ run --tier local --task energy-explainer --repeat 1 --out artifacts/demo-run --dashboard` | live page at the printed `Dashboard:` URL; the run file lands in the git-ignored `artifacts/demo-run` and is not a published result | illustrative |
| 10 (fallback) | `AJ dashboard --replay results/<ts>-nebius.json --speed 4 --exit-after 60` | the same page replaying a committed hosted run, replay banner visible; nothing written | derived from a committed file |

Task set and tiers used. Tasks: the three files in `tasks/` (names read from their `name` field; expected `incident-brief`, `api-migration`, `energy-explainer`, assumed from the file names until the builder reads them). Tiers: `local` (runtime per the NIM decision, Ollama expected), `nvidia`, `nebius` from the laptop in step 3; `cloudgpu` on the VM in step 5 only when M5 produced a run file. Defaults: `--repeat 3`, `--mode serialized`, baseline 30 s, four agents (Planner, Researcher, Writer, Reviewer), so 36 workflow steps and 36 judge entries per tier. A Qwen3 8B baseline run (`-baseline` file) is optional and is not shown in the video.

Try it out URL. The report page `https://tomas-rampas.github.io/agent-joules/report/` (GitHub Pages from the `docs/` folder of `main`; URL pattern inferred, confirmed by the user). Second Try it out link: `https://github.com/tomas-rampas/agent-joules`. Fallback order if the verified rules reject a static page: GitHub Release v0.1.0, then the public video URL.

What the video shows, in order (165 s planned; the limit of the prompt is 170 s; the script wording is AJ-136; this shot order is the demo definition):

| Shot | Start (s) | Duration (s) | On screen |
|---|---|---|---|
| 1 | 0 | 12 | title card with the tagline and the repository URL |
| 2 | 12 | 15 | README Mermaid architecture diagram |
| 3 | 27 | 13 | terminal: `AJ probe` on the laptop GPU |
| 4 | 40 | 20 | terminal: `AJ run --tier local --task energy-explainer --repeat 1 --out artifacts/demo-run --dashboard`, per-step lines and the printed `Dashboard:` URL (recorded, then cut to fit; illustrative) |
| 5 | 60 | 30 | browser on a second screen: the live dashboard (power line against the dashed idle baseline, agent cards filling with net joules, tokens, latency); fallback when no GPU or local runtime is available at recording time: `AJ dashboard --replay results/<ts>-nebius.json --speed 4 --exit-after 60` with the replay banner visible |
| 6 | 90 | 30 | browser: the report page, bar chart, tier rows, `N/A` markers on hosted tiers |
| 7 | 120 | 17 | cloudgpu row in the report and README `Other Nebius tools or services used` (fallback: row `cloudgpu | not run`) |
| 8 | 137 | 18 | README results block header, the sentence `Energy is GPU-board energy only.`, the five labels, the note that the published results were recorded without a browser open |
| 9 | 155 | 10 | repository URL, MIT license, Try it out URL |

Artifacts the demo produces (all committed unless marked): the files of the table above plus `docs/report/` and `content/linkedin-posts.md`; `artifacts/demo-run/*.json` is git-ignored.

If the cloudgpu tier is absent. Step 5 is skipped and no cloudgpu file exists; step 3 still runs three tiers; `AJ report` exits 0 and renders `cloudgpu | not run` in the table and the chart; shot 7 shows that row for 10 s and the README `Other Nebius tools or services used` sentence `None besides Token Factory.` for 5 s (shot total 15 s, the rest of the budget is unused); the Devpost pitch is candidate A-local (AJ-141); the story, README and posts drop every cloudgpu claim (AJ-140, AJ-132, AJ-134).

Honesty. Every number on screen comes from a committed results file; the live run in shots 4 and 5 is illustrative and is the source of no published figure; the narration states that. The published results files were recorded without a browser open on the laptop (a browser tab adds compositing load to the measured GPU; the dashboard server itself adds no GPU work); only the demo recording shows the live page. The statement appears in the script, in the README and in `docs/methodology.md`.

### AJ-136: Write the demo video script and shot list
Covers: REQ-027
User story: As the person recording the video, I want a shot list with start seconds, durations, on-screen content, narration and exact commands, so that I can record a public YouTube video of under three minutes without improvising numbers.
Acceptance criteria:
1. The header of `content/demo-script.md` contains the lines `Upload target: public YouTube video`, `Budget: at most 170 seconds` and `Planned total: 165 seconds`.
2. The `## Shot list` table has the columns `Shot`, `Start (s)`, `Duration (s)`, `On-screen content`, `Narration`. The durations sum to 170 or less: `awk -F'|' '/^\| [0-9]/{s+=$4} END{print s}' content/demo-script.md` prints 165 (the same awk sum prints a value of 170 or less for the fallback variant).
3. Each start second equals the previous start plus the previous duration, and the last start plus duration equals the planned total. Check: an awk pass over the table asserts start[n] = start[n-1] + duration[n-1] for every row, prints `contiguous` and exits 0.
4. Narration has at most 2.5 words per second of its shot (shot 1: 30 words or fewer). Check: the parser (`awk` over the shot table) divides the word count of each narration cell by its duration, prints the highest ratio and exits 0 only when it is 2.5 or less.
5. The `## Commands shown` fenced block lists the exact commands shown (shots 3, 4 and 5: probe, `run ... --dashboard`, `dashboard --replay`): `AJ <command> --help` exits 0 for each command named there, and the script mentions `--dashboard` and `dashboard --replay`.
6. Every number in the on-screen or narration cells is followed by `(results/<file>)` naming an existing committed file; shot 4 and shot 5 narration contain the words `illustrative` and `not the source of any published figure`, and shot 5 or shot 8 states that the published results were recorded without a browser open.
7. Shot 8 contains the sentence `Energy is GPU-board energy only.` and names the five labels measured, estimated, model-judged, N/A, not-reported.
8. The script gives the fallback text for shot 7 when the cloudgpu tier is absent and the replay fallback text for shot 5; narration mentions Nebius AI Cloud only in the variant where the cloudgpu file exists; narration of shots 1 to 9 makes no ranking claim (`greener`, `more efficient than`, `beats`, `outperforms` absent).
BDD scenarios:
```gherkin
@unit @AJ-S136 @REQ-027
Feature: AJ-136 Demo video script
  @positive
  Scenario: AJ-136 Shot durations fit the budget and are contiguous
    Given the committed content/demo-script.md
    When the shot list is parsed
    Then the durations sum to 165 and are at most 170
    And each start second equals the previous start plus the previous duration
  @positive
  Scenario: AJ-136 Narration is speakable in each shot
    Given the parsed shot list
    When words per second are computed for each narration cell
    Then no shot exceeds 2.5 words per second
  @positive
  Scenario: AJ-136 Commands shown all answer help
    Given the commands listed under "Commands shown"
    When each CLI command is run with "--help" in process
    Then every exit code is 0
  @negative
  Scenario: AJ-136 A script over three minutes is rejected
    Given a copy of the script whose durations sum to 181
    When the script check runs
    Then it fails with "total 181 s exceeds 170 s"
  @negative
  Scenario: AJ-136 A script over the 170 second budget but under three minutes is rejected
    Given a copy of the script whose durations sum to 175
    When the script check runs
    Then it fails with "total 175 s exceeds 170 s"
  @negative
  Scenario: AJ-136 Narration that is too long for its shot is rejected
    Given a copy of the script whose 12 second shot 1 has 40 narration words
    When the script check runs
    Then it fails with "shot 1: 3.3 words per second exceeds 2.5"
  @negative
  Scenario: AJ-136 A number without a results file is rejected
    Given a copy of the script whose shot 5 narration says "123 J" with no results file
    When the script check runs
    Then it fails with "number without source: 123 J"
  @negative
  Scenario: AJ-136 A command that does not exist is rejected
    Given a copy of the script that shows "AJ bench"
    When the script check runs
    Then it fails with "unknown command: bench"
  @negative
  Scenario: AJ-136 A script without the browser statement is rejected
    Given a copy of the script without the sentence that published results were recorded without a browser open
    When the script check runs
    Then it fails with "missing statement: recorded without a browser"
  @negative
  Scenario: AJ-136 A script without the dashboard commands is rejected
    Given a copy of the script that never mentions "--dashboard" or "dashboard --replay"
    When the script check runs
    Then it fails with "missing command: dashboard"
  @negative
  Scenario: AJ-136 A script without the upload target is rejected
    Given a copy of the script without the line "Upload target: public YouTube video"
    When the script check runs
    Then it fails with "missing header: Upload target"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-136-T1 | Write the failing feature for budget, contiguity, words per second, commands and sources | tests/AgentJoules.Specs/Features/Reporting/DemoScript.feature | 0.5 | AJ-135-T2 |
| AJ-136-T2 | Add the shot-list parser, sum check, words-per-second check and command check to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.75 | AJ-136-T1 |
| AJ-136-T3 | Write the script header and the `## Shot list` table from the shot order of AJ-135 | content/demo-script.md | 0.75 | AJ-136-T2, AJ-135-T3 |
| AJ-136-T4 | Write the narration cells with file-named numbers read from the committed results and the browser statement | content/demo-script.md | 0.75 | AJ-136-T3 |
| AJ-136-T5 | Write `## Commands shown` and the shot 6 fallback variant | content/demo-script.md | 0.5 | AJ-136-T4 |
| AJ-136-T6 | Time a read-aloud of the narration against the table and record the result | docs/build-log.md | 0.25 | AJ-136-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/DemoScript.feature
Docs impact: content/demo-script.md; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the shot list total is 170 or less for both the cloudgpu-present and the cloudgpu-absent variants
Demo relevance: yes: this is the script of the video.

### AJ-137: Write the long-form article
Covers: REQ-027
User story: As a LinkedIn reader who wants depth, I want a long-form article that explains the method, the prior art, what is new and the limitations, with a results file named for every number, so that I can verify each claim.
Acceptance criteria:
1. `content/article.md` contains the strings `2407.16893`, `TokenPowerBench`, `2604.09048` and `greentoken`, each with a link: `grep -c "2407.16893" content/article.md` prints 1 or more, likewise for the other three.
2. A heading `Limitations` exists once; its section contains `GPU-only`, `display`, `power limit`, `sampling`, `offload`, `hosted` and `cloud`, and the sentence `Energy is GPU-board energy only.`
3. A section `What is new` names per-agent-step attribution, .NET, and Microsoft Agent Framework plus Nemotron, and scopes "none of the prior art does this" to the search of 2026-10-17.
4. Every line with a measured quantity (`[0-9]+(\.[0-9]+)? ?(J|W|ms|tokens|%|EUR|USD)`) contains a `results/` file name that exists, or `docs/cloudgpu-run.md`, and a label word (`measured`, `estimated`, `model-judged`, `N/A`, `not-reported`).
5. GPU name, driver version, run date (UTC), local runtime and model tags with precision appear next to every results table in the article, copied from the run file named. Check: `ArticleChecks` compares each table header with the strings read from the named run file and exits 0 only when all equal.
6. No ranking claim: `grep -ciE "greener|more efficient than|leaderboard|outperforms|beats" content/article.md` prints 0; cloudgpu and laptop numbers are in separate rows and never averaged.
7. When no cloudgpu run file exists, the article contains no cloudgpu result and one sentence stating that the cloudgpu tier did not run. Check: `grep -i cloudgpu content/article.md` prints only lines containing `did not run` or `not run` in that case.
8. Length is between 800 and 2 500 words (`wc -w`), ASCII only, LF endings.
BDD scenarios:
```gherkin
@unit @AJ-S137 @REQ-027
Feature: AJ-137 Long-form article
  @positive
  Scenario: AJ-137 The article cites the required prior art and has a Limitations section
    Given the committed content/article.md
    When the citations and headings are checked
    Then it contains 2407.16893, TokenPowerBench, 2604.09048, greentoken and one "Limitations" heading
    And the Limitations section contains GPU-only, display, power limit, sampling, offload, hosted and cloud
  @positive
  Scenario: AJ-137 Every quantity names an existing results file and a label
    Given the committed article
    When each line with a measured quantity is checked
    Then each names an existing results file and one label word
  @positive
  Scenario: AJ-137 The article is within the length bounds
    Given the committed article
    When the words are counted
    Then the count is between 800 and 2500
  @negative
  Scenario: AJ-137 A quantity without a results file is rejected
    Given a copy of the article with the line "The Writer used 55 J."
    When the article check runs
    Then it fails with "number without source: 55 J"
  @negative
  Scenario: AJ-137 A missing Limitations heading is rejected
    Given a copy of the article without the heading "Limitations"
    When the article check runs
    Then it fails with "missing heading: Limitations"
  @negative
  Scenario: AJ-137 A ranking claim is rejected
    Given a copy of the article saying "the local tier is greener than the hosted tier"
    When the article check runs
    Then it fails with "ranking claim: greener"
  @negative
  Scenario: AJ-137 A missing citation is rejected
    Given a copy of the article without "greentoken"
    When the article check runs
    Then it fails with "missing citation: greentoken"

@integration @AJ-S137 @REQ-027
Feature: AJ-137 Article citations resolve
  @positive
  Scenario: AJ-137 Each cited link answers
    Given the four links of the article
    When each link is requested
    Then each answers HTTP 200 and the date is recorded in docs/build-log.md
  @negative
  Scenario: AJ-137 A link that answers 404 is reported
    Given a cited link that answers HTTP 404
    When the link check runs
    Then it fails naming the URL
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-137-T1 | Write the failing feature for citations, sections, sources, labels and length | tests/AgentJoules.Specs/Features/Reporting/Article.feature | 0.5 | AJ-134-T2 |
| AJ-137-T2 | Add the citation, ranking-claim and word-count checks to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.5 | AJ-137-T1 |
| AJ-137-T3 | Write the outline, `Prior art` with checked links and `What is new` | content/article.md | 1.0 | AJ-137-T2 |
| AJ-137-T4 | Write `How it works` (attribution by construction, counter versus sampling, baseline, the serialized gate) from docs/methodology.md | content/article.md | 0.75 | AJ-137-T3 |
| AJ-137-T5 | Write `Results` with each number copied from a named results file, label and environment header | content/article.md | 1.5 | AJ-137-T4 |
| AJ-137-T6 | Write `Limitations`, `Reproduce` and `What is next` | content/article.md | 0.75 | AJ-137-T5 |
| AJ-137-T7 | Re-check the four links and record the date | docs/build-log.md | 0.25 | AJ-137-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/Article.feature
Docs impact: content/article.md; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the TokenPowerBench and greentoken URLs were found by search and opened by the builder, not typed from memory
Demo relevance: no.

### AJ-138: Write the Devpost checklist: rules, fields and demo URL
Covers: REQ-028
User story: As the user submitting to Devpost, I want a checklist that records the verified rules and maps every submission field and judging requirement to a repository artifact, so that I copy from the repository and miss nothing before the deadline.
Acceptance criteria:
1. `docs/devpost-checklist.md` exists and has the section `Source and verification` with the URL `https://nebiusglobalaihackathon.devpost.com/` and either `Verified: <date>` (date 2026-10-17 or later, by a human or by the builder's fetch) or the string `not verified` with the reason (the page returned HTTP 403 to automated fetching on 2026-10-17): `grep -c "nebiusglobalaihackathon.devpost.com" docs/devpost-checklist.md` prints 1 or more.
2. The section `Rules` has the 13 items of prompt section 3, each as a line `- <item>: <value> (evidence: verified | user-stated, not verified | aggregator, unverified)`: eligibility, required or judged technologies, submission fields, video length limit, license requirement, repository visibility requirement, judging criteria and weights, tracks and selection rule, Builder Program or credit offers, whether a demo or test URL is required, required NVIDIA model families, required Nebius product, deadline with time zone. `grep -c "(evidence: " docs/devpost-checklist.md` prints 13 or more.
3. The deadline appears exactly as `2026-10-30 10:00 PDT (17:00 UTC)`.
4. The section `Conflicts` lists every conflict between the verified rules and the prompt with its resolution, or the line `None found on <date>`; if the rules contradict the prompt on license, required technology or video length, the verified rule wins (prompt section 3).
5. The section `Fields to artifacts` is a checkbox list `- [ ] <field> -> <artifact>` with at least these fields: project name, elevator pitch (200 characters), About the project, Built-with tags (25 maximum), Try it out links, YouTube video link, track, team members, repository URL, OSI license, README with setup instructions, Nemotron usage highlights, Token Factory highlights, other Nebius tools, Nemotron and Nebius feedback questionnaire. The About the project row names `docs/devpost-story.md`. Every artifact path exists (`test -f`) or is marked `(user)`.
6. One line starts `Demo URL:`, names the chosen satisfier (default: the GitHub Pages report page `https://tomas-rampas.github.io/agent-joules/report/`, then the v0.1.0 release, then the public video URL) and gives the reason; if the verified rules require a live hosted service the line reads `Demo URL: BLOCKED` with the missing decision.
7. The Built-with list holds at most 25 tags including C#, .NET, Microsoft Agent Framework, NVIDIA Nemotron, Nebius Token Factory and NVML; `Nebius AI Cloud` is present only when a completed or partial cloudgpu run file exists; the line `Tags: <n>` equals the count.
8. If a tier or field cannot be verified, the line says `not verified` and never `verified`.
BDD scenarios:
```gherkin
@unit @AJ-S138 @REQ-028
Feature: AJ-138 Devpost checklist rules and fields
  @positive
  Scenario: AJ-138 The checklist records the 13 rule items with an evidence class
    Given the committed docs/devpost-checklist.md
    When the Rules section is parsed
    Then it has 13 items and each ends with an evidence class
  @positive
  Scenario: AJ-138 Every field maps to an existing artifact
    Given the Fields to artifacts checkbox list
    When each artifact path is checked
    Then every path exists or is marked "(user)"
    And the About the project row names "docs/devpost-story.md"
  @positive
  Scenario: AJ-138 The Demo URL line names a satisfier and a reason
    Given the committed checklist
    When the line starting "Demo URL:" is read
    Then it names the GitHub Pages report page and gives a reason
  @positive
  Scenario: AJ-138 The deadline is written in both zones
    Given the committed checklist
    When the deadline line is read
    Then it contains "2026-10-30 10:00 PDT (17:00 UTC)"
  @negative
  Scenario: AJ-138 A checklist without a Demo URL line is rejected
    Given a copy of the checklist without the line "Demo URL:"
    When the checklist check runs
    Then it fails with "missing line: Demo URL:"
  @negative
  Scenario: AJ-138 A field that maps to a missing file is rejected
    Given a copy of the checklist mapping "About the project" to "docs/missing-story.md"
    When the checklist check runs
    Then it fails with "artifact not found: docs/missing-story.md"
  @negative
  Scenario: AJ-138 More than 25 Built-with tags are rejected
    Given a copy of the checklist whose Built-with list has 26 tags
    When the checklist check runs
    Then it fails with "26 tags exceed 25"
  @negative
  Scenario: AJ-138 A rule item without an evidence class is rejected
    Given a copy of the checklist with the line "- license requirement: MIT"
    When the checklist check runs
    Then it fails with "no evidence class: license requirement"
  @negative
  Scenario: AJ-138 A Nebius AI Cloud tag without a cloudgpu run is rejected
    Given a workspace with no completed or partial cloudgpu run file and a Built-with list containing "Nebius AI Cloud"
    When the checklist check runs
    Then it fails with "tag without evidence: Nebius AI Cloud"

@integration @AJ-S138 @REQ-028
Feature: AJ-138 Official rules page fetch
  @positive
  Scenario: AJ-138 The rules page answers or the checklist says not verified
    Given network access
    When the builder requests https://nebiusglobalaihackathon.devpost.com/rules
    Then either the text is recorded with the fetch date or the checklist says "not verified" with the HTTP status
  @negative
  Scenario: AJ-138 A 403 answer is recorded and the user is asked to paste the rules
    Given the page answers HTTP 403
    When the checklist is written
    Then the Source and verification section contains "HTTP 403" and "not verified" and asks the user to paste the Rules tab text
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-138-T1 | Write the failing checklist feature (rules, fields, Demo URL, deadline, tags) | tests/AgentJoules.Specs/Features/Configuration/DevpostChecklist.feature | 0.5 | AJ-130-T2 |
| AJ-138-T2 | Add the checklist parser (rule items, checkbox list, tag count, path existence) to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.75 | AJ-138-T1 |
| AJ-138-T3 | Fetch the official rules page, record the status and fetch date, and ask the user to paste the text on failure | docs/devpost-checklist.md | 0.5 | AJ-138-T2 |
| AJ-138-T4 | Write the `Rules` items and the `Conflicts` section from the fetched or pasted text and the aggregator evidence | docs/devpost-checklist.md | 1.0 | AJ-138-T3 |
| AJ-138-T5 | Write `Fields to artifacts`, the Built-with list and the `Demo URL:` line | docs/devpost-checklist.md | 1.0 | AJ-138-T4, AJ-135-T3 |
| AJ-138-T6 | Record the verification outcome and any rule conflict in the build log | docs/build-log.md | 0.25 | AJ-138-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/DevpostChecklist.feature
Docs impact: docs/devpost-checklist.md; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the file carries a verification date or the `not verified` statement
Demo relevance: no.

### AJ-139: Write the user-action sections and commands of the Devpost checklist
Covers: REQ-028
User story: As the user, I want the registration lines, track choice, judging-criteria mapping, credit steps and the exact push, Pages and release commands in one file, so that I do each user action in order without reading the prompt.
Acceptance criteria:
1. The file has the headings `Registration`, `Tracks`, `Judging criteria`, `Credits` once each (`grep -c "^#\+ Registration$" docs/devpost-checklist.md` prints 1, likewise for the others).
2. `Registration` contains exactly the four lines `Devpost handle: <user fills>`, `Joined on: <user fills>`, `Team: solo unless stated` and `Credits requested on: <user fills>`; the builder never fills the three `<user fills>` values.
3. `Tracks` lists the form's four tracks (Coding and agentic engineering, Best apps and agents, Personal AI, Physical AI), recommends `Coding and agentic engineering` with one reason, and states `Final pick: the user decides`.
4. `Judging criteria` maps Technological Implementation, Design, Potential Impact and Quality of the Idea (equal weights, researched 2026-10-17 from aggregator pages, unverified) each to a README section or artifact that exists.
5. `Credits` states the USD 25 Token Factory credits through the Resources-tab form with the activation code, the USD 25 for joining the Nebius Builders Program, that both are user actions, and that they do not apply to Nebius AI Cloud.
6. Three fenced blocks each preceded by the line `User action (the builder never runs this):` hold: `git push -u origin main`; the Pages settings (Settings, Pages, Deploy from a branch, branch `main`, folder `/docs`, plus the `gh api` form); and `gh release create v0.1.0 --repo tomas-rampas/agent-joules --target main --title "Agent Joules v0.1.0" --notes-file docs/release-notes-v0.1.0.md` followed by the three asset paths `artifacts/agent-joules-v0.1.0-fdd.zip`, `artifacts/agent-joules-v0.1.0-results.zip`, `artifacts/agent-joules-v0.1.0-report.zip`. `grep -c "git push" docs/devpost-checklist.md` prints 1 or more.
7. The Pages URL `https://tomas-rampas.github.io/agent-joules/report/` is stated with the label `inferred from the standard project-pages pattern`.
8. A `Before you push` list holds three checks with their commands: `AJ report --verify-readme` exits 0; `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` returns nothing; `git status --short` is empty.
BDD scenarios:
```gherkin
@unit @AJ-S139 @REQ-028
Feature: AJ-139 Devpost checklist user actions
  @positive
  Scenario: AJ-139 Registration lines are present and left for the user
    Given the committed docs/devpost-checklist.md
    When the Registration section is read
    Then it has the four exact lines and three values read "<user fills>"
  @positive
  Scenario: AJ-139 The three user commands are in labelled fenced blocks
    Given the committed checklist
    When the fenced blocks are extracted
    Then blocks contain "git push", "gh release create" and the Pages settings
    And each is preceded by "User action (the builder never runs this):"
  @positive
  Scenario: AJ-139 Judging criteria map to existing artifacts
    Given the Judging criteria section
    When each mapped artifact is checked
    Then the four criteria map to paths or README headings that exist
  @positive
  Scenario: AJ-139 Tracks name a recommendation and leave the pick to the user
    Given the Tracks section
    When it is read
    Then it recommends one track with one reason and says "Final pick: the user decides"
  @negative
  Scenario: AJ-139 A filled Devpost handle is rejected
    Given a copy of the checklist with "Devpost handle: tomas-rampas"
    When the checklist check runs
    Then it fails with "builder filled a user field: Devpost handle"
  @negative
  Scenario: AJ-139 A missing release block is rejected
    Given a copy of the checklist without the string "gh release create"
    When the checklist check runs
    Then it fails with "missing command block: gh release create"
  @negative
  Scenario: AJ-139 A command block not labelled as a user action is rejected
    Given a copy of the checklist where the push block lacks the user-action label
    When the checklist check runs
    Then it fails with "block not marked as user action: git push"
  @negative
  Scenario: AJ-139 A missing Credits heading is rejected
    Given a copy of the checklist without the heading "Credits"
    When the checklist check runs
    Then it fails with "missing heading: Credits"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-139-T1 | Write the failing feature for headings, registration lines, labelled command blocks | tests/AgentJoules.Specs/Features/Configuration/DevpostUserActions.feature | 0.5 | AJ-138-T2 |
| AJ-139-T2 | Write `Registration`, `Tracks` and `Credits` | docs/devpost-checklist.md | 0.75 | AJ-139-T1, AJ-138-T4 |
| AJ-139-T3 | Write `Judging criteria` mapped to README sections and artifacts | docs/devpost-checklist.md | 0.5 | AJ-139-T2, AJ-132-T5 |
| AJ-139-T4 | Write the push, Pages and release blocks and the `Before you push` list | docs/devpost-checklist.md | 0.75 | AJ-139-T3 |
| AJ-139-T5 | Record in the build log that no command of the checklist was run by the builder | docs/build-log.md | 0.25 | AJ-139-T4 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/DevpostUserActions.feature
Docs impact: docs/devpost-checklist.md; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `git log origin/main..HEAD --oneline` still lists every commit and no push was made (checked again in AJ-156)
Demo relevance: no.

### AJ-140: Fill the Devpost story placeholders and delete its checklist
Covers: REQ-028
User story: As the user submitting the About the project field, I want every placeholder in `docs/devpost-story.md` replaced by a measured value or artifact reference, so that the pasted story states only what the committed files show.
Acceptance criteria:
1. `grep -cF "[[FILL:" docs/devpost-story.md` prints 0 (nine placeholders replaced) and `grep -ci "Fill-after-build" docs/devpost-story.md` prints 0; the file ends with the paragraph "A LinkedIn post and a long-form article follow, with every number traced to a results file."
2. Each filled passage follows its checklist row (see the mapping table below) and names the results file or document it came from in parentheses, a unit and one label word (`measured`, `estimated`, `model-judged`, `N/A`, `not-reported`).
3. Every other sentence is byte-identical to the pre-fill file unless a measured fact contradicts it; each change has a build-log entry `Story change: <before> -> <after> (fact: <file>)`. The comparison runs `git diff` of the story against the last pre-fill commit and every changed line outside the nine passages and the deleted section has an entry.
4. The cloud passage has its own sentence with preset, GPU, driver, precision, repeats and observed cost, and the laptop and cloud figures are never merged or averaged. When no completed or partial cloudgpu run file exists, the cloud passage reads `The cloudgpu tier did not run in this submission` plus the reason from `docs/cloudgpu-run.md` or `docs/build-log.md`, and the sentences naming the Nebius AI Cloud instance or the L40S elsewhere in the story are rewritten and logged.
5. The comparison passage states cost per currency (EUR for local, USD for hosted), `N/A` with the reason for a tier without a price, the model on each tier, and ends with the scope `on this workload`; it contains no ranking word (`greener`, `better`, `faster than`, `cheaper than`).
6. The calibration passage quotes `relativeDifference` with its label or states `N/A` with the reason from the calibration file.
7. `docs/devpost-checklist.md` names `docs/devpost-story.md` for the About the project field.
BDD scenarios:
```gherkin
@unit @AJ-S140 @REQ-028
Feature: AJ-140 Devpost story fill
  @positive
  Scenario: AJ-140 All nine placeholders are replaced and the checklist is gone
    Given the committed docs/devpost-story.md
    When placeholders and headings are counted
    Then "[[FILL:" occurs 0 times and no heading contains "Fill-after-build"
  @positive
  Scenario: AJ-140 Each filled passage names its source file and a label
    Given the nine filled passages
    When each is checked
    Then each names an existing results file or document, a unit and one label word
  @positive
  Scenario: AJ-140 Unchanged sentences are byte-identical to the pre-fill story
    Given the pre-fill story and the filled story
    When the lines outside the nine passages are diffed
    Then every difference has a "Story change:" entry in docs/build-log.md
  @positive
  Scenario: AJ-140 Without a cloudgpu run the story drops its cloudgpu claims
    Given a workspace with no completed or partial cloudgpu run file
    When the story is checked
    Then the cloud passage says "The cloudgpu tier did not run in this submission"
    And no sentence claims a measured Nebius AI Cloud or L40S figure
  @negative
  Scenario: AJ-140 A leftover placeholder is rejected
    Given a copy of the story with one "[[FILL: ... ]]" left
    When the story check runs
    Then it fails with "leftover placeholder: 1"
  @negative
  Scenario: AJ-140 A number without a source is rejected
    Given a copy of the story where the local median is written as "212 J" with no results file
    When the story check runs
    Then it fails with "number without source: 212 J"
  @negative
  Scenario: AJ-140 An unlogged change to an existing sentence is rejected
    Given a copy of the story where the sentence "Energy here means GPU-board energy, not CPU, system memory or wall power." is edited
    When the diff check runs without a build-log entry
    Then it fails with "unlogged story change"
  @negative
  Scenario: AJ-140 A leftover Fill-after-build section is rejected
    Given a copy of the story that still has the heading "Fill-after-build checklist"
    When the story check runs
    Then it fails with "checklist section not deleted"
  @negative
  Scenario: AJ-140 A merged laptop and cloud figure is rejected
    Given a copy of the story with "the average of the laptop and the cloud instance"
    When the story check runs
    Then it fails with "merged tiers"
```
Conflict: REQ-028 and the story checklist ask for "median net joules per run of the demo workflow" with a minimum to maximum range, but `AJ report` aggregates by (tier, task, agent) and the results file stores steps and one `totals.netJoules` for the whole file; no per-run sum exists in any committed artifact. Proposed resolution: a run is the sum of the four steps' `netJoules` for one (task, repeat) pair with outcome `ok`; the builder commits the exact expression in `docs/build-log.md` (for example `jq '[.steps | group_by([.task,.repeat])[] | map(.netJoules.value) | add] | sort' results/<ts>-local.json`), takes median, minimum and maximum from its output, states `n = tasks x repeats` and labels the figure `measured` (sum of measured values); if any step value is null the run is skipped and the count of skipped runs is stated.
Fill mapping (the rows of the story's own checklist, expanded):

| # | Placeholder topic | Source field or document | Rule |
|---|---|---|---|
| 1 | agent names and fan-out shape | distinct `steps.agent` of the latest local file | one sentence: Planner, then Researcher and Writer in parallel branches, join at Reviewer; names must equal the file's set |
| 2 | counter support and method | `results/probe-<ts>.json` fields `energyCounterSupported`, `method` | one sentence with both values |
| 3 | NIM attempt outcome | `docs/nim-local-attempt.md` sections `Outcome`, `Decision` | two or three sentences, whichever way it ended |
| 4 | hardest problem | entries of `docs/build-log.md` | one entry, named by date, with its resolution; no invented problem |
| 5 | local median net joules per run | latest local file, per-run expression above | median, range, n, GPU, driver, UTC date, runtime, `gpuOffloadPercent`, model tag |
| 6 | cloud median net joules per run | latest cloudgpu file and `docs/cloudgpu-run.md` sections `Instance`, `Cost` | same method; preset, GPU, driver, precision, repeats, observed cost in USD; separate sentence |
| 7 | largest share of local net energy | latest local file | agent net joules sum over all steps divided by total, as a percentage; scoped `on this workload on this laptop` |
| 8 | local versus hosted cost and latency | `totals.costByCurrency`, `totals.wallClockMs`, `steps.modelId` of the local, nvidia and nebius files | one sentence per currency; model named per tier; `N/A` with reason where no price; no ranking |
| 9 | calibration | `results/calibration-<ts>.json` | relative difference with label, or `N/A` and the reason |

Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-140-T1 | Write the failing story feature (placeholders, sources, byte-identical remainder, cloudgpu absent) | tests/AgentJoules.Specs/Features/Configuration/DevpostStory.feature | 0.5 | AJ-138-T2 |
| AJ-140-T2 | Add the story checker with the pre-fill diff against the build-log entries to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.75 | AJ-140-T1 |
| AJ-140-T3 | Commit the per-run median and share expressions and their output as a build-log entry | docs/build-log.md | 0.75 | AJ-140-T2 |
| AJ-140-T4 | Replace placeholders 1, 2, 3, 4 and 9 | docs/devpost-story.md | 0.75 | AJ-140-T3 |
| AJ-140-T5 | Replace placeholders 5, 6, 7 and 8 | docs/devpost-story.md | 1.0 | AJ-140-T3 |
| AJ-140-T6 | Review every other sentence against the measured facts and log each change | docs/devpost-story.md, docs/build-log.md | 0.75 | AJ-140-T4, AJ-140-T5 |
| AJ-140-T7 | Delete the Fill-after-build section and run the story checks | docs/devpost-story.md | 0.25 | AJ-140-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/DevpostStory.feature
Docs impact: docs/devpost-story.md; docs/build-log.md; docs/devpost-checklist.md (About the project row)
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] after any new run file the numbers of passages 5 to 8 are regenerated from the recorded expressions and the story check is rerun
Demo relevance: no.

### AJ-141: Apply the tagline rule: candidate A, A-local or the GPU-name swap
Covers: REQ-028
User story: As the user pasting the Devpost elevator pitch, I want the pitch to match what actually ran, so that the 200-character field never claims a cloudgpu measurement that does not exist.
Acceptance criteria:
1. The rule is decided by a command: a completed or partial cloudgpu run file exists when `grep -lE '"status": "(completed|partial)"' results/*Z-cloudgpu.json` prints a file (probe and calibration files excluded by the glob and name pattern `^[0-9]{8}T[0-9]{6}Z-cloudgpu\.json$`); the decision and the command output are recorded in `docs/build-log.md`.
2. File exists and `grep -o '"gpuName": "[^"]*"'` on it contains `L40S`: the elevator pitch is candidate A unchanged (197 characters); `docs/devpost-checklist.md` names `docs/devpost-pitch.md` candidate A for the elevator-pitch field, holds the text verbatim in a fenced block and the line `Characters: 197`.
3. File exists and the GPU name lacks `L40S`: the token matching `[A-Z]+[0-9]+[A-Z]*` of the name replaces `L40S` in candidate A (in `docs/devpost-pitch.md`, in the checklist block), the result is 200 characters or fewer, and `docs/build-log.md` has a `Pitch change:` entry with before and after. A change of GPU class needs a user decision (prompt section 2); the swap renames only.
4. No such file: the pitch is candidate A-local (192 characters), the checklist says so with the reason `cloudgpu tier produced no committed run file`, and the text contains neither `Nebius AI Cloud` nor `L40S`.
5. The short tagline row of the checklist reads `Agent Joules: energy per agent step, measured on your GPUs` (58) when the cloudgpu file exists and `Agent Joules: energy per agent step, measured on your GPU` (57) when not.
6. `wc -m` of the checklist's pitch block is 200 or less in every case, and the block equals the chosen candidate in `docs/devpost-pitch.md` byte for byte.
BDD scenarios:
```gherkin
@unit @AJ-S141 @REQ-028
Feature: AJ-141 Devpost pitch rule
  @positive
  Scenario: AJ-141 A cloudgpu file naming an L40S keeps candidate A
    Given a workspace with a completed cloudgpu run file whose gpuName is "NVIDIA L40S"
    When the pitch rule is applied
    Then the chosen pitch is candidate A and is 197 characters
    And the checklist block equals candidate A byte for byte
  @positive
  Scenario: AJ-141 No cloudgpu file selects A-local
    Given a workspace with no completed or partial cloudgpu run file
    When the pitch rule is applied
    Then the chosen pitch is A-local and is 192 characters
    And it contains neither "Nebius AI Cloud" nor "L40S"
  @positive
  Scenario: AJ-141 A cloudgpu file naming another GPU swaps the GPU token
    Given a workspace with a completed cloudgpu run file whose gpuName is "NVIDIA H100 80GB HBM3"
    When the pitch rule is applied
    Then candidate A has "L40S" replaced by "H100" and is 200 characters or fewer
    And docs/build-log.md has a "Pitch change:" entry
  @negative
  Scenario: AJ-141 A pitch over 200 characters is rejected
    Given a pitch candidate of 201 characters
    When the pitch check runs
    Then it fails with "201 characters exceed 200"
  @negative
  Scenario: AJ-141 A-local used although a cloudgpu file exists is rejected
    Given a workspace with a completed cloudgpu run file and a checklist block equal to A-local
    When the pitch check runs
    Then it fails with "A-local chosen but cloudgpu run exists"
  @negative
  Scenario: AJ-141 Candidate A used without a cloudgpu file is rejected
    Given a workspace with no cloudgpu run file and a checklist block equal to candidate A
    When the pitch check runs
    Then it fails with "candidate A chosen without cloudgpu run"
  @negative
  Scenario: AJ-141 A checklist block that differs from the pitch file is rejected
    Given a checklist pitch block that differs from candidate A by one character
    When the pitch check runs
    Then it fails with "pitch block differs from docs/devpost-pitch.md"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-141-T1 | Write the failing pitch-rule feature (three outcomes and four rejections) | tests/AgentJoules.Specs/Features/Configuration/DevpostTagline.feature | 0.5 | AJ-138-T2 |
| AJ-141-T2 | Add the pitch selector, character counter and byte-equality check to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.5 | AJ-141-T1 |
| AJ-141-T3 | Apply the rule: record the command output, write the pitch block and character line in the checklist | docs/devpost-checklist.md, docs/build-log.md | 0.5 | AJ-141-T2, AJ-138-T5 |
| AJ-141-T4 | Swap the GPU token in candidate A only when the GPU name lacks L40S, and log it | docs/devpost-pitch.md, docs/build-log.md | 0.25 | AJ-141-T3 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/DevpostTagline.feature
Docs impact: docs/devpost-checklist.md; docs/devpost-pitch.md (swap case only); docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the decision is rerun after any change to the set of cloudgpu run files
Demo relevance: yes: shot 1 shows the tagline chosen here.

### AJ-142: Fill the builder placeholders of the feedback file and list the user ones
Covers: REQ-028
User story: As the user answering the Nemotron and Nebius questionnaire, I want every placeholder that a results file can answer to be filled and the seven that need my opinion to be listed, so that I only write my own ratings and opinions.
Acceptance criteria:
1. `grep -cF "owner: builder" docs/devpost-feedback.md` prints 0 (eight placeholders replaced).
2. `grep -cF "owner: user" docs/devpost-feedback.md` prints 7 and those seven lines are byte-identical to the pre-fill file (diff against `git show <pre-fill commit>:docs/devpost-feedback.md | grep -F "owner: user"` is empty).
3. `docs/devpost-checklist.md` has the heading `Feedback form, user fills` with seven bullets, one per user placeholder (Q2 rating, Q4 other models, Q5 most valuable capability, Q6 recommendation, Q7 rating, Q8 features, Q9 hopes), each naming `docs/devpost-feedback.md` and the question number.
4. The `Fill checklist` table of the feedback file keeps the seven user rows and no builder rows: `grep -cE "^\| .* \| builder \|" docs/devpost-feedback.md` prints 0 and `grep -cE "^\| .* \| user \|" docs/devpost-feedback.md` prints 7.
5. Each filled passage names its source file and labels: Q2 judge scores `model-judged` with outcome and retry counts per tier; Q4 baseline comparison or the sentence `No Qwen3 8B baseline run was made.`; Q4 cross-tier cost (per currency) and latency with the model named on each tier; Q5 verified Nebius model IDs, base URL and verification date from `docs/endpoints.md`; Q5 AI Cloud instance from `docs/cloudgpu-run.md` or `The Nebius AI Cloud VM was not used in this submission` plus reason; Q5 credits; Q7 latency, TTFT, outcome, retries and wait time; Q7 NIM attempt.
6. The Q5 credits passage uses only what the user reported in `docs/build-log.md`; if the user reported nothing it reads `Not reported: the builder cannot see the Nebius billing page.` (never an invented answer).
7. The design statements of Q1, Q3 and Q5 are checked against `docs/endpoints.md` and the results files (model on Token Factory, local tag, reasoning mode, base URL, preset); each change is logged as `Feedback change:` in `docs/build-log.md`.
8. No cross-tier efficiency or ranking claim appears; every comparison names model and tier and is scoped to this workload. Check: `grep -niE "greener|more efficient than|outperforms|beats " docs/devpost-feedback.md` prints nothing and the feedback check requires a model ID and a tier name in every comparison sentence.
BDD scenarios:
```gherkin
@unit @AJ-S142 @REQ-028
Feature: AJ-142 Devpost feedback answers
  @positive
  Scenario: AJ-142 Builder placeholders are gone and user placeholders are untouched
    Given the committed docs/devpost-feedback.md and its pre-fill version
    When the owner markers are counted and the user lines compared
    Then "owner: builder" occurs 0 times and "owner: user" occurs 7 times
    And the seven user lines are byte-identical to the pre-fill file
  @positive
  Scenario: AJ-142 The checklist lists the seven user placeholders
    Given the committed docs/devpost-checklist.md
    When the heading "Feedback form, user fills" is read
    Then it has seven bullets naming Q2, Q4, Q5, Q6, Q7, Q8 and Q9 and docs/devpost-feedback.md
  @positive
  Scenario: AJ-142 Filled passages name sources and labels
    Given the eight filled passages
    When each is checked
    Then each names an existing file and a label word or an explicit not-run statement
  @positive
  Scenario: AJ-142 Without a baseline file the baseline passage says so
    Given a workspace with no baseline run file
    When the Q4 baseline passage is checked
    Then it reads "No Qwen3 8B baseline run was made."
  @negative
  Scenario: AJ-142 A leftover builder placeholder is rejected
    Given a copy of the feedback file with one "owner: builder" placeholder left
    When the feedback check runs
    Then it fails with "builder placeholder left: 1"
  @negative
  Scenario: AJ-142 A user placeholder filled by the builder is rejected
    Given a copy of the feedback file where the Q6 user placeholder is replaced by "9"
    When the feedback check runs
    Then it fails with "user placeholder modified: Q6"
  @negative
  Scenario: AJ-142 A missing checklist heading is rejected
    Given a copy of the checklist without "Feedback form, user fills"
    When the feedback check runs
    Then it fails with "missing heading: Feedback form, user fills"
  @negative
  Scenario: AJ-142 An invented credits answer is rejected
    Given a feedback file claiming the credits covered all runs and no user statement in docs/build-log.md
    When the feedback check runs
    Then it fails with "credits answer without a user statement"
  @negative
  Scenario: AJ-142 A passage with a number and no source is rejected
    Given a copy of the Q7 passage with "latency 842 ms" and no results file
    When the feedback check runs
    Then it fails with "number without source: 842 ms"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-142-T1 | Write the failing feedback feature (markers, byte-identical user lines, checklist heading, credits) | tests/AgentJoules.Specs/Features/Configuration/DevpostFeedback.feature | 0.5 | AJ-138-T2 |
| AJ-142-T2 | Add the owner-marker counter and the user-line byte comparison to DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 0.5 | AJ-142-T1 |
| AJ-142-T3 | Fill Q2 and Q4 builder placeholders from the three run files and the optional baseline file | docs/devpost-feedback.md | 1.0 | AJ-142-T2, AJ-140-T3 |
| AJ-142-T4 | Fill Q5 builder placeholders from endpoints.md, cloudgpu-run.md and the user's credits statement | docs/devpost-feedback.md | 0.75 | AJ-142-T3 |
| AJ-142-T5 | Fill Q7 builder placeholders from the run files and nim-local-attempt.md | docs/devpost-feedback.md | 0.75 | AJ-142-T4 |
| AJ-142-T6 | Check the Q1, Q3 and Q5 design sentences against the facts and log each change | docs/devpost-feedback.md, docs/build-log.md | 0.5 | AJ-142-T5 |
| AJ-142-T7 | Remove the builder rows from the Fill checklist table and write the `Feedback form, user fills` section | docs/devpost-feedback.md, docs/devpost-checklist.md | 0.5 | AJ-142-T6, AJ-139-T2 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/DevpostFeedback.feature
Docs impact: docs/devpost-feedback.md; docs/devpost-checklist.md; docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the seven user placeholders remain for the user (user action in M7 AJ-158)
Demo relevance: no.

### AJ-143: Audit every content artifact against the honesty rules
Covers: REQ-025, REQ-026, REQ-027, REQ-028
User story: As a reader of any public Agent Joules text, I want every number traced to a results file with its label and no hidden zero, so that I can trust the claims.
Acceptance criteria:
1. The audit covers `README.md` (outside the generated results block), `content/linkedin-posts.md`, `content/demo-script.md`, `content/article.md`, `docs/devpost-story.md`, `docs/devpost-feedback.md`, `docs/devpost-checklist.md` and `docs/devpost-pitch.md`, and exits 0 through `dotnet test` over the `ContentHonesty` feature.
2. Rule 1 (source): every quantity matching `[0-9]+(\.[0-9]+)? ?(J|W|kWh|ms|tokens|%|EUR|USD)` names an existing `results/` file or `docs/cloudgpu-run.md` in the same sentence or table row.
3. Rule 2 (label): that sentence or row also holds one label word from the vocabulary. Check: the audit prints `finding: no label` for each quantity without a label word and exits 0 when it prints none.
4. Rule 3 (no hidden zero): no hosted-tier energy is written as `0`; `N/A` or `not-reported` is written instead; `grep -nE "(energy|joules).{0,20}[^0-9.]0 ?(J|joules)" <files>` prints nothing.
5. Rule 4 (scope sentence): README, the article, the demo script, every post variant and the story each contain `GPU-board energy`.
6. Rule 5 (no ranking): `grep -niE "greener|more efficient than|leaderboard|outperforms|beats |model [A-Za-z0-9-]+ is greener" <files>` prints nothing.
7. Rule 6 (no key material): `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` returns no hits, and with the three keys set `git grep -nF "$NVIDIA_API_KEY"`, `git grep -nF "$NEBIUS_API_KEY"`, `git grep -nF "$NGC_API_KEY"` each return no hits (@integration; exit codes recorded in `docs/build-log.md`).
8. Rule 7 (format): `grep -nP "[^\x00-\x7F]" <files>` prints nothing (no em-dash), `grep -c $'\r' <files>` prints 0 for each.
9. Rule 8 (cloudgpu consistency): with no completed or partial cloudgpu run file, no audited file except the feedback design paragraph of Q5 and the non-chosen pitch candidates mentions `Nebius AI Cloud` or `L40S` outside a sentence containing `not run` or `did not run`.
10. Rule 9 (browser note): README, `content/demo-script.md` and `docs/methodology.md` each contain the statement that the published results were recorded without a browser open.
BDD scenarios:
```gherkin
@unit @AJ-S143 @REQ-025 @REQ-026 @REQ-027 @REQ-028
Feature: AJ-143 Content honesty audit
  @positive
  Scenario: AJ-143 The committed content set passes every rule
    Given the audited files and the committed results files
    When the honesty audit runs
    Then it reports 0 findings for the rules source, label, zero, scope, ranking, format and cloudgpu
  @positive
  Scenario: AJ-143 A cloudgpu-free submission passes the consistency rule
    Given a workspace with no cloudgpu run file and content updated by the fallback paths
    When the honesty audit runs
    Then it reports 0 findings
  @negative
  Scenario: AJ-143 A number without a source is a finding
    Given a copy of the article with "The Planner used 40 J."
    When the honesty audit runs
    Then a finding reads "number without source: 40 J"
  @negative
  Scenario: AJ-143 A hosted energy value written as zero is a finding
    Given a copy of the README with "nvidia energy 0 J"
    When the honesty audit runs
    Then a finding reads "hidden zero: nvidia energy 0 J"
  @negative
  Scenario: AJ-143 A ranking claim is a finding
    Given a copy of a post saying "local beats the cloud on energy"
    When the honesty audit runs
    Then a finding reads "ranking claim"
  @negative
  Scenario: AJ-143 A key-shaped string is a finding
    Given a copy of a file containing a runtime-built "nvapi-" plus 16 characters value
    When the honesty audit runs
    Then a finding reads "key material"
  @negative
  Scenario: AJ-143 An em-dash is a finding
    Given a copy of a file containing the character U+2014
    When the honesty audit runs
    Then a finding reads "non-ASCII character"
  @negative
  Scenario: AJ-143 A cloudgpu claim without a run file is a finding
    Given a workspace with no cloudgpu run file and a story sentence "the Nebius AI Cloud L40S used 90 J"
    When the honesty audit runs
    Then a finding reads "cloudgpu claim without run file"
  @negative
  Scenario: AJ-143 A post variant without the GPU-board sentence is a finding
    Given a copy of the posts where the hackathon variant lacks "GPU-board energy"
    When the honesty audit runs
    Then a finding reads "scope sentence missing: hackathon"

@integration @AJ-S143 @REQ-025 @REQ-026 @REQ-027 @REQ-028
Feature: AJ-143 Key scan with the real keys
  @positive
  Scenario: AJ-143 No tracked line contains a key value
    Given NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY are set
    When "git grep -nF" runs for each value
    Then each returns exit code 1 and prints no line
  @negative
  Scenario: AJ-143 A tracked line containing a key value fails the scan
    Given a temporary tracked file containing the value of NGC_API_KEY
    When "git grep -nF" runs for that value
    Then it prints the file name without the value and the scan fails
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-143-T1 | Write the failing honesty feature (nine rule scenarios) | tests/AgentJoules.Specs/Features/Reporting/ContentHonesty.feature | 0.5 | AJ-134-T2 |
| AJ-143-T2 | Implement the eight audit rules as `HonestyAudit` in DocumentChecks | tests/AgentJoules.Specs/Support/DocumentChecks.cs | 1.5 | AJ-143-T1 |
| AJ-143-T3 | Run the audit over all files and fix each finding at its source | README.md, content/linkedin-posts.template.md, content/demo-script.md, content/article.md, docs/devpost-story.md, docs/devpost-feedback.md, docs/devpost-checklist.md | 1.5 | AJ-143-T2, AJ-140-T7, AJ-142-T7, AJ-137-T6, AJ-136-T5 |
| AJ-143-T4 | Run the key scan with the three keys set and record the commands and exit codes | docs/build-log.md | 0.5 | AJ-143-T3 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/ContentHonesty.feature; RedactionTests (key redaction, M0 to M2)
Docs impact: docs/build-log.md
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the audit is rerun after the last results file or text change before M7 starts
Demo relevance: no.

## Risks

| Risk | Trigger | Mitigation | Owner |
|---|---|---|---|
| Estimate exceeds the window: 55.0 task hours against about 18 working hours (2026-10-27 to 2026-10-29) | the sum in `## Estimate` is above 18 | front-load the stories that need no final results (AJ-130, AJ-131, AJ-135 T1 to T3, AJ-136 T1 to T3, AJ-137 T1 to T4, AJ-138, AJ-139, AJ-134 T1 to T5, AJ-144, about 29 hours) into the waiting time of M4 and M5; run result-dependent stories (AJ-133, AJ-134-T6, AJ-136-T4, AJ-137-T5, AJ-140, AJ-141, AJ-142, AJ-143) as soon as the last run file lands; apply the drop order of M7 AJ-159 if still late | builder |
| Official rules page unreadable (HTTP 403 on 2026-10-17) | fetch returns 403 or a login wall | record `not verified` with the status, mark the 13 rule items `user-stated, not verified`, ask the user to paste the Rules tab text; never write `verified` without a date | builder, user |
| cloudgpu tier absent or partial | no completed or partial `results/*Z-cloudgpu.json` by 2026-10-28 | fallback paths in AJ-132, AJ-134, AJ-135, AJ-140, AJ-141, AJ-142: `cloudgpu | not run` row, `None besides Token Factory.`, pitch A-local, singular tagline, no cloudgpu sentence anywhere; AJ-143 rule 8 enforces it | builder |
| A new run file lands after the text was filled | a results file newer than the one a passage cites | rerun `AJ report --update-readme --render-content`, re-extract AJ-140 numbers from the recorded expressions, rerun AJ-143 | builder |
| Video live run overwrites the latest local file | `AJ run` without `--out artifacts/demo-run` | Conflict line of AJ-135; `artifacts/` git-ignored; AJ-150 re-checks `run.repeat` of the latest files | builder |
| Per-run median is not in any committed artifact | AJ-140 placeholders 5 and 6 | Conflict line of AJ-140: recorded `jq` expression, `n` stated, label `measured` (sum of measured values) | builder |
| Prior-art link dead or paywalled | HTTP 404 or 403 on a citation | keep the arXiv id and title, drop the link, say so in `docs/build-log.md`; never type a URL from memory | builder |
| Video longer than 170 s | recorded take above the shot list | the user cuts shot 5 first (30 s, illustrative), then shot 2; the script totals 165 s | user |
| Pages URL differs from the inferred one | Pages shows another host name | the user pastes the real URL; the builder edits the `Demo URL:` line and the script (AJ-158 step) | user |
| Seven user placeholders unfilled at submission time | `grep -cF "owner: user" docs/devpost-feedback.md` is above 0 on 2026-10-30 | the Devpost form questions are answered by the user in the browser; the file is a draft, not an input of the form (AJ-158 cut-off plan) | user |

## Estimate

Note (2026-10-18): any deferral or descope candidates named in this section are informational; the drop order in docs/plan/plan.md section 3.4 is the single source and classifies stories as MUST, SHOULD or COULD.

| Story | Tasks | Hours |
|---|---|---|
| AJ-130 README pitch and diagram | 5 | 2.25 |
| AJ-131 README quick start, CLI, reproduce | 6 | 3.75 |
| AJ-132 README Devpost-form sections | 6 | 3.5 |
| AJ-133 README results block | 4 | 1.75 |
| AJ-134 LinkedIn post drafts | 7 | 3.5 |
| AJ-135 Demo program end to end | 6 | 3.5 |
| AJ-136 Demo video script | 6 | 3.5 |
| AJ-137 Long-form article | 7 | 5.25 |
| AJ-138 Devpost checklist rules and fields | 6 | 4.0 |
| AJ-139 Devpost checklist user actions | 5 | 2.75 |
| AJ-140 Devpost story fill | 7 | 4.75 |
| AJ-141 Pitch rule | 4 | 1.75 |
| AJ-142 Feedback fill | 7 | 4.5 |
| AJ-143 Honesty audit | 4 | 4.0 |
| AJ-144 report flags and content renderer | 7 | 6.25 |
| Total | 87 | 55.0 |

Capacity of the window: about 18 hours (three days at six hours). The gap and its mitigation are the first risk above.
