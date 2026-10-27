# Agent Joules v0.1.0

Agent Joules is a .NET 10 command line tool and library that meters each step of a Microsoft Agent Framework workflow: tokens, latency and cost for every model call, and, on a GPU you control, joules and watts read through NVML. This is the first release, built for the Nebius x NVIDIA Global AI Hackathon. Energy is GPU-board energy only.

Every number below names the results file, docs/cloudgpu-run.md or, for the NIM-local attempt, docs/nim-local-attempt.md it comes from, in the same sentence or table row, and carries one of the labels measured, estimated, model-judged or N/A. The figures are copied from the README results block, which `report --verify-readme` checks against the run files.

## Features

- `probe` prints the GPU, driver, power limit and energy method that will be measured, and exits 0 with `no GPU` when NVML or a device is absent (prompt REQ-003); `--json` prints one JSON object and `--save` writes it to `results/`.
- `run` executes the four-agent demo workflow (Planner, Researcher, Writer, Reviewer) on the tiers `local` (a NIM container on the laptop GPU), `cloudgpu` (a NIM container on one Nebius AI Cloud GPU virtual machine), `nvidia` (the hosted NVIDIA endpoint) and `nebius` (Nebius Token Factory), in that fixed order. It has a serialized mode, where one step's energy is not mixed with another's, and a concurrent mode, where calls overlap and a measured step whose window overlaps another's carries the `shared-window` flag. `--repeat` sets the repeats (default 3), `--task` selects one task, and `--dry-run` validates the configuration, keys and model IDs and makes no completion call.
- `calibrate` measures one window with the energy counter and with power sampling at once and writes both totals and their relative difference to `results/`.
- `validate` checks run files against the results schema and the results model, and with `--config` checks the configured model IDs against docs/endpoints.md.
- `report` writes the report page (docs/report/index.html), a summary and a chart from the run files, and with `--update-readme`, `--verify-readme` and `--render-content` it regenerates the README results block, checks it byte for byte, and resolves the post templates from the committed run files.
- `dashboard` has two views of the same page: `run --dashboard` serves a live page on loopback during a run (estimates on it are labelled estimated and never enter a run file), and `dashboard --replay <run file>` replays a committed run on its recorded timeline without a GPU.
- The results schema is `docs/results-schema.json`, generated from the results model and compared with the model by a test.
- The CI workflow `.github/workflows/ci.yml` restores, builds with warnings as errors, runs the unit tests and runs `probe --json` on `windows-latest` and `ubuntu-latest`, and references no secret.

Tiers that ran, with their run files:

- local: results/20261027T095519Z-local.json (run date 2026-10-27, status completed).
- cloudgpu: results/20261026T124721Z-cloudgpu.json (run date 2026-10-26, status completed; the rental is recorded in docs/cloudgpu-run.md).
- nvidia: results/20261025T135121Z-nvidia.json (run date 2026-10-25, status completed).
- nebius: results/20261025T133440Z-nebius.json (run date 2026-10-25, status completed).

Tiers that did not run: none. The earlier local campaign of 2026-10-25 stays in results/20261025T131930Z-local.json as history.

Headline figures, copied from the README results block (each one scoped to this workload, three tasks with three repeats, on the named machine):

- local: results/20261027T095519Z-local.json on the NVIDIA GeForce RTX 3080 Ti Laptop GPU, driver 616.92, NIM 1.8.5, BF16: net 33134.2 J (run total, measured), gross 43906.6 J (run total, measured), 25770 input tokens and 17556 output tokens (measured), cost 0.0028 EUR (estimated).
- cloudgpu: results/20261026T124721Z-cloudgpu.json on the NVIDIA H100 80GB HBM3, driver 580.173.02, FP8: net 10389.6 J (run total, measured), gross 16364.1 J (run total, measured), 25379 input tokens and 15889 output tokens (measured), cost 0.0647 USD (estimated). The rental of the virtual machine is estimated at about USD 7 in docs/cloudgpu-run.md (estimated from the hourly price, not the bill).
- nvidia: results/20261025T135121Z-nvidia.json: energy N/A (hosted-tier), 25550 input tokens and 16548 output tokens (measured), cost N/A (no priced step).
- nebius: results/20261025T133440Z-nebius.json: energy N/A (hosted-tier), 26554 input tokens and 16991 output tokens (measured), cost 0.0057 USD (estimated).

The laptop and the cloud GPU are two machines on two rows; no comparison or average across tiers is claimed.

The NIM-local attempt (docs/nim-local-attempt.md, measured): a NIM container for Llama-3.1-Nemotron-Nano-4B-v1.1, version 1.8.5, served `GET /v1/models` on the fourth container start, 32 minutes 6 seconds after the attempt began. The default profile was killed during the engine build (exit 137, out of memory) and the default context length did not fit the KV cache of the 16 GB laptop GPU; the working configuration is the BF16 vllm profile with a context length of 16384. No Nemotron 3 Nano 4B NIM exists, so the local tier serves the 4B Nemotron model, not Nemotron 3 Nano.

## Fixes

- The statement that published runs were recorded "without a browser open" was corrected to "recorded without a browser showing the dashboard", with the other open applications of the 2026-10-25 campaign disclosed next to the README results block (the M6 exit review fix in docs/build-log.md).
- The local run was repeated on 2026-10-27 with the other GPU applications closed (a data refresh, the K2 entry in docs/build-log.md): results/20261027T095519Z-local.json is now the published local run, and the README block, the report page, the posts, the article and the Devpost texts were regenerated or re-derived from it.

## Breaking changes

None (first release)

## Migration

None (first release)

## Known limitations

- GPU-only: the energy is GPU-board energy; CPU, memory, storage, the network and wall power are not measured (docs/methodology.md, Limitations).
- Display: the laptop GPU drives the display, so compositing load is part of the idle baseline and of every local window.
- Power limit: the laptop's limit changes with the power mode, and each run file embeds the probe taken at run time (results/20261027T095519Z-local.json).
- Sampling: power samples arrive at a fixed rate and the driver may average them, so short windows are coarse; the calibration files in results/ quantify the gap between the counter and the sampling.
- Offload: the NIM serves the model fully on the GPU, so offload is 100 percent by construction, not by observation (results/20261027T095519Z-local.json).
- Hosted tiers: energy for nvidia and nebius is N/A (hosted-tier), and no hosted figure is a joule claim.
- Cloud: the cloudgpu tier runs on the provider's driver and host power management, its per-step cost is estimated from the hourly price (docs/cloudgpu-run.md), and laptop and cloud joules are never compared as efficiency claims.
- Throttling: the local run carried the throttled flag on every step with the reason SwPowerCap (results/20261027T095519Z-local.json), so net energy there reflects a GPU running below its unthrottled clocks.
- Output caps: each task caps its answer, and steps that stopped at the cap are judged as returned and carry no flag (counted in docs/methodology.md from results/20261027T095519Z-local.json, results/20261025T133440Z-nebius.json and results/20261025T135121Z-nvidia.json).
- Reasoning mode: every tier runs with reasoning off, so the energy is that of the answer and not of a thinking trace (docs/endpoints.md).
- Judge coverage: the judge failed for some steps, and the quality of those steps is N/A with the reason recorded, never 0 (results/20261027T095519Z-local.json, results/20261026T124721Z-cloudgpu.json and results/20261025T133440Z-nebius.json).
- Scope: the numbers describe one laptop, one workload and one model set; they are not a leaderboard and carry no general claim that one model is greener than another.

## Honesty rules

- Every published number is measured on the build machine or returned by a hosted API during a run from it, and is traceable to a committed file under `results/`; the cloud rental figures come from docs/cloudgpu-run.md.
- Every value carries exactly one label from measured, estimated, model-judged, N/A and not-reported.
- A value that does not exist is N/A or not-reported with a null value, and is never written as 0.
- The NIM-local attempt is reported whichever way it ends, in docs/nim-local-attempt.md and in the README.
- Every published table is accompanied by the GPU name, driver version, run date (UTC), local runtime and model tags or IDs.
- Energy is GPU-board energy only; every surface that shows joules says so at least once.
- There is no leaderboard and no general claim that one model is greener than another; statements are scoped to this workload on this machine.

## Assets

The three assets are built under the git-ignored `artifacts/` folder and attached to the release by the user; none of them holds a `.raw` file, an API key or a private key.

- agent-joules-v0.1.0-fdd.zip: the framework-dependent CLI, the output of `dotnet publish src/AgentJoules.Cli -c Release --no-self-contained -o artifacts/fdd`; it lists `AgentJoules.Cli.dll` and needs the .NET 10 runtime.
- agent-joules-v0.1.0-results.zip: every `results/*.json` file, that is the four run files named above, the earlier local campaign file, the probe files and the calibration files.
- agent-joules-v0.1.0-report.zip: the contents of `docs/report/`, that is `index.html` (the static report page), `summary.md` and `joules-per-step.svg`.

## Verification

After the release is created, check that it holds the three assets:

```
gh release view v0.1.0 --repo tomas-rampas/agent-joules --json assets --jq '.assets[].name'
```

Smoke test of the framework-dependent build, after unzipping the fdd asset into `artifacts/fdd`:

```
dotnet artifacts/fdd/AgentJoules.Cli.dll probe --json
dotnet artifacts/fdd/AgentJoules.Cli.dll validate results/
```

Both exit 0; `probe --json` reports `gpuPresent` true on a machine with an NVIDIA GPU and false elsewhere, and `validate results/` checks every run file against `docs/results-schema.json`. The README results block is checked with `report --verify-readme` (exit 0).
