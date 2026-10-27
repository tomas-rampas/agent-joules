# Agent Joules demo video script

Upload target: public YouTube video
Budget: at most 170 seconds
Planned total: 165 seconds

In the commands below, the program is `dotnet run --project src/AgentJoules.Cli --`. Shots 3, 4 and 5 show the commands listed under Commands shown. The narration never states a number without the results file it came from.

## Demo program

```text
dotnet run --project src/AgentJoules.Cli -- probe --json --save
dotnet run --project src/AgentJoules.Cli -- run --tier all --dry-run
dotnet run --project src/AgentJoules.Cli -- run --tier all
dotnet run --project src/AgentJoules.Cli -- calibrate
./AgentJoules.Cli probe --json --save --suffix cloudgpu && ./AgentJoules.Cli run --tier cloudgpu && ./AgentJoules.Cli calibrate --suffix cloudgpu
dotnet run --project src/AgentJoules.Cli -- validate results/
dotnet run --project src/AgentJoules.Cli -- report --update-readme --render-content
dotnet run --project src/AgentJoules.Cli -- report --verify-readme
dotnet run --project src/AgentJoules.Cli -- run --tier local --task energy-explainer --repeat 1 --out artifacts/demo-run --dashboard
dotnet run --project src/AgentJoules.Cli -- dashboard --replay results/20261025T133440Z-nebius.json --speed 4 --exit-after 60
```

Step 5 runs on the cloud GPU VM (the M5 run used `--seconds 60 --suffix cloudgpu`). Step 9 is the live run of the video: it is illustrative and writes to the git-ignored `artifacts/demo-run`. Step 10 is the fallback for shot 5.

### Task set and tiers

Tasks: `tasks/01-incident-brief.json`, `tasks/02-api-migration.json` and `tasks/03-energy-explainer.json`, named `incident-brief`, `api-migration` and `energy-explainer`.

Tiers: `local` (the laptop GPU), `cloudgpu` (one Nebius AI Cloud GPU VM, step 5 only), `nvidia` and `nebius` (hosted, from the laptop in step 3). Defaults: `--repeat 3`, serialized mode, a 30 s idle baseline, four agents (Planner, Researcher, Writer, Reviewer), so 36 workflow steps per tier. Hosted tiers report tokens, latency and, where a price is configured, cost; their energy is N/A.

### Artifacts

| Artifact | Produced by | Committed |
|---|---|---|
| results/probe-20261025T131800Z.json | step 1 | yes |
| results/20261027T095519Z-local.json | step 3 | yes |
| results/20261025T135121Z-nvidia.json | step 3 | yes |
| results/20261025T133440Z-nebius.json | step 3 | yes |
| results/calibration-20261025T105451Z.json | step 4 | yes |
| results/probe-20261026T122330Z-cloudgpu.json | step 5 | yes |
| results/20261026T124721Z-cloudgpu.json | step 5 | yes |
| results/calibration-20261026T124311Z-cloudgpu.json | step 5 | yes |
| docs/report/summary.md | step 7 | yes |
| docs/report/joules-per-step.svg | step 7 | yes |
| docs/report/index.html | step 7 | yes |
| docs/.nojekyll | step 7 | yes |
| README.md (results block) | step 7 | yes |
| content/linkedin-posts.md | step 7 (rendered from content/linkedin-posts.template.md, written by this milestone) | yes |
| artifacts/demo-run/*.json | step 9 | no, git-ignored |

### Try it out

- Report page: `https://tomas-rampas.github.io/agent-joules/report/` (inferred from the standard GitHub Pages project URL, confirmed by the user after Pages is enabled).
- Repository: `https://github.com/tomas-rampas/agent-joules`

## If the cloudgpu tier is absent

Step 5 is skipped and no cloudgpu file exists; step 3 still runs three tiers; `AJ report` exits 0 and renders `cloudgpu | not run` in the table and the chart. Shot 7 shows that row for 10 s and the README `Other Nebius tools or services used` sentence `None besides Token Factory.` for 5 s (shot total 15 s, so the planned total is 163 s). The narration drops every cloudgpu claim and does not name Nebius AI Cloud. See the fallback shot list below.

## Shot list

| Shot | Start (s) | Duration (s) | On-screen content | Narration |
|---|---|---|---|---|
| 1 | 0 | 12 | Title card with the README tagline and the repository URL | Agent Joules puts a power meter on every agent in a multi-agent workflow and reports GPU energy per step. The repository link is on screen. |
| 2 | 12 | 15 | README Mermaid architecture diagram | Tasks go through four agents on a chosen tier. A meter reads the GPU board, and a reporter turns the run files into one report page. |
| 3 | 27 | 13 | Terminal: the probe command from Commands shown, on the laptop GPU | First, probe. It checks that the laptop GPU exposes a power reading. |
| 4 | 40 | 20 | Terminal: the run command with the dashboard flag from Commands shown, per-step lines and the printed Dashboard URL (recorded, then cut to fit) | Now one illustrative run of a single task. It is not the source of any published figure. It writes to a git-ignored folder, and the printed URL opens the live page. |
| 5 | 60 | 30 | Browser on a second screen: the live dashboard with the power line against the dashed idle baseline and agent cards filling with net joules, tokens and latency. Fallback when no GPU or local runtime is available: the replay command from Commands shown, replaying a committed hosted run with the replay banner visible | The live page is illustrative, and not the source of any published figure. Power rises above the dashed idle baseline as each agent works, and its card fills in. The published results were recorded without a browser showing the dashboard. When no GPU is free, the replay of a committed run, with its replay banner, stands in. |
| 6 | 90 | 30 | Browser: the report page, the bar chart, the tier rows and the N/A markers on the hosted tiers | The report page shows joules per step as bars, one row for each tier. Hosted tiers show N/A for energy, because only tokens, latency and, where a price is configured, cost are reported there. Nothing is averaged across tiers. |
| 7 | 120 | 17 | The cloudgpu row in the report and the README section Other Nebius tools or services used | The cloudgpu row is one Nebius AI Cloud GPU, measured and reported separately from the laptop. The README section Other Nebius tools or services used lists what else was used. |
| 8 | 137 | 18 | README results block header, the sentence Energy is GPU-board energy only., the five labels of the methodology summary, and the dashboard note below the block that the published results were recorded without a browser showing the dashboard | Energy is GPU-board energy only. Every value carries one label: measured, estimated, model-judged, N/A or not-reported. The published results were recorded without a browser showing the dashboard; the listed applications held GPU contexts; the older campaign had browsers open. |
| 9 | 155 | 10 | Repository URL, MIT license, Try it out URL | The code is MIT licensed. Try it out on the report page, and find the source on GitHub. |

## Shot list (fallback: cloudgpu tier absent)

| Shot | Start (s) | Duration (s) | On-screen content | Narration |
|---|---|---|---|---|
| F1 | 0 | 12 | Title card with the README tagline and the repository URL | Agent Joules puts a power meter on every agent in a multi-agent workflow and reports GPU energy per step. The repository link is on screen. |
| F2 | 12 | 15 | README Mermaid architecture diagram | Tasks go through four agents on a chosen tier. A meter reads the GPU board, and a reporter turns the run files into one report page. |
| F3 | 27 | 13 | Terminal: the probe command from Commands shown, on the laptop GPU | First, probe. It checks that the laptop GPU exposes a power reading. |
| F4 | 40 | 20 | Terminal: the run command with the dashboard flag from Commands shown, per-step lines and the printed Dashboard URL (recorded, then cut to fit) | Now one illustrative run of a single task. It is not the source of any published figure. It writes to a git-ignored folder, and the printed URL opens the live page. |
| F5 | 60 | 30 | Browser on a second screen: the live dashboard with the power line against the dashed idle baseline and agent cards filling with net joules, tokens and latency. Fallback when no GPU or local runtime is available: the replay command from Commands shown, replaying a committed hosted run with the replay banner visible | The live page is illustrative, and not the source of any published figure. Power rises above the dashed idle baseline as each agent works, and its card fills in. The published results were recorded without a browser showing the dashboard. When no GPU is free, the replay of a committed run, with its replay banner, stands in. |
| F6 | 90 | 30 | Browser: the report page, the bar chart, the tier rows and the N/A markers on the hosted tiers | The report page shows joules per step as bars, one row for each tier. Hosted tiers show N/A for energy, because only tokens, latency and, where a price is configured, cost are reported there. Nothing is averaged across tiers. |
| F7 | 120 | 15 | The cloudgpu row reading not run in the report (first two thirds of the shot), then the README sentence None besides Token Factory. under Other Nebius tools or services used (last third) | No cloud GPU run is part of this report, so that row reads not run. None besides Token Factory. |
| F8 | 135 | 18 | README results block header, the sentence Energy is GPU-board energy only., the five labels of the methodology summary, and the dashboard note below the block that the published results were recorded without a browser showing the dashboard | Energy is GPU-board energy only. Every value carries one label: measured, estimated, model-judged, N/A or not-reported. The published results were recorded without a browser showing the dashboard; the listed applications held GPU contexts; the older campaign had browsers open. |
| F9 | 153 | 10 | Repository URL, MIT license, Try it out URL | The code is MIT licensed. Try it out on the report page, and find the source on GitHub. |

## Commands shown

```text
dotnet run --project src/AgentJoules.Cli -- probe
dotnet run --project src/AgentJoules.Cli -- run --tier local --task energy-explainer --repeat 1 --out artifacts/demo-run --dashboard
dotnet run --project src/AgentJoules.Cli -- dashboard --replay results/20261025T133440Z-nebius.json --speed 4 --exit-after 60
```

## Honesty

Every number on screen comes from a committed results file. The live run in shots 4 and 5 is illustrative and is the source of no published figure, and the narration says so. The published results files were recorded without a browser showing the dashboard (a browser tab rendering it adds compositing load to the measured GPU; the dashboard server itself adds no GPU work); the published local run of 2026-10-27 had no browser open and the Claude desktop app, two Edge WebView2 hosts and a few system and utility applications still held GPU contexts, and 26 dotnet test processes ran on the CPU during the run, and, through the laptop's shared power and thermal budget, can also lower the GPU's power limit and clocks (inferred: the run-start limit was 78.157 W in results/20261027T095519Z-local.json against 105 W in results/20261025T131930Z-local.json, both measured, and every step was SwPowerCap-throttled) (docs/methodology.md, the Limitations item on open applications); the earlier 2026-10-25 campaign, with browsers open, stays in results/ as history; only the demo recording shows the live page. The README states the same conditions in its notes after the results block.
