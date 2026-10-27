# Agent Joules v0.2.0

Agent Joules v0.2.0 is the documentation follow-up to v0.1.0. The code of the CLI is unchanged except one header label of the embedded dashboard page (listed below). The CLI is published from the commit the v0.2.0 tag marks, and `dotnet artifacts/fdd/AgentJoules.Cli.dll --version` prints 1.0.0 plus that commit's hash, so the binary and the tag name the same commit.

## Changes since v0.1.0 (commits 5aa3a1f, 01dfcf4, 8bb1bf0 and the release documentation commit on main)

- docs/report/dashboard.png: the screenshot of the live dashboard during the demo rehearsal (dark theme, PNG 1907 x 1525), and the README link to it that `AJ report --update-readme` writes.
- README.md, the section `How this project uses NVIDIA Nemotron`: the cloudgpu bullet names the tier's GPU, one NVIDIA H100 80GB (preset 1gpu-16vcpu-200gb), beside the model nvidia/nemotron-3-nano. The four model IDs of the section are unchanged and match the `modelId` fields of the four run files in results/.
- docs/build-log.md and the M7 story file: the M7 exit addendum (the publication of main, the CI run, the Pages source, the tag and the release of v0.1.0); the Acceptance matrix row REQ-032 is MET and Definition of Done box 1 is ticked; the M7 exit addendum records both changes.
- tests/AgentJoules.Specs: the report fixture copies the screenshot into its hermetic workspace, so the AJ-152 determinism scenario stays green with the file present.
- The release documentation commit: docs/release-notes-v0.2.0.md (this file), README.md (the Hackathon submission paragraph), docs/devpost-checklist.md, docs/plan/plan.md, docs/plan/stories/M7-hardening-and-submission.md and the docs/build-log.md record name v0.2.0 as the submitted release; v0.1.0 marks the M7 exit snapshot 98cc246.
- The same commit: the dashboard header label that lists the agent-to-model pairs reads `Agent models` instead of `Models` (the page is embedded in AgentJoules.Dashboard.dll, so the fdd asset carries it), and the README status line and the Devpost story call Agent Joules a hackathon prototype.

Everything else, the features, the known limitations, the honesty rules and the verification steps, is as described in the v0.1.0 release notes (docs/release-notes-v0.1.0.md at this commit).

## Assets

- agent-joules-v0.2.0-fdd.zip: the framework-dependent CLI, the output of `dotnet publish src/AgentJoules.Cli -c Release --no-self-contained -o artifacts/fdd` from the commit the v0.2.0 tag marks; 64 files (measured on the 8bb1bf0 build), the same file set as the v0.1.0 asset; every AgentJoules assembly differs from the v0.1.0 asset by the embedded commit hash, and AgentJoules.Dashboard.dll also by the header label above.
- agent-joules-v0.2.0-results.zip: byte-identical to the v0.1.0 asset (sha256 1a2f3267...); the results/ folder did not change.
- agent-joules-v0.2.0-report.zip: the contents of docs/report/, that is index.html, summary.md, joules-per-step.svg and dashboard.png.

Energy is GPU-board energy only.
