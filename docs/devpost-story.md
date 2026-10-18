## Inspiration

My agents report tokens and latency. They do not report energy. When a workflow chains several agents and fans out to parallel workers, I could not say which step draws the power.

Before building, I researched prior art. Published work I found includes arXiv 2407.16893 "The Price of Prompting", TokenPowerBench, "Watt Counts" arXiv 2604.09048 and the Python greentoken CLI. None of them attributes energy to an individual agent step, and none works in .NET. A search on 2026-10-17 found no published sample that combines Microsoft Agent Framework with NVIDIA NIM or Nemotron in C#.

## What it does

Agent Joules is a .NET 10 command line tool and library that meters each step of a Microsoft Agent Framework workflow. It wraps each agent's chat client in metering middleware. For every model call it records tokens, latency and cost, and on a GPU I control it also records joules and watts. A judge model adds a quality score.

Every metric value carries exactly one of five labels: measured, estimated, model-judged, N/A or not-reported. A missing value is never written as zero.

The same workflow runs on four tiers: locally on an NVIDIA GPU with a Nemotron model through a NIM container first and Ollama as the fallback, on a Nebius AI Cloud GPU instance where a Nemotron NIM container and the meter share one H100, on Nebius Token Factory serving NVIDIA Nemotron 3, and on build.nvidia.com.

Energy is measured on the two GPUs I control, the laptop and the Nebius AI Cloud instance, and the two are reported in separate rows, never merged. The hosted API tiers report tokens and latency, and cost where a published price exists. Their energy is N/A. Energy here means GPU-board energy, not CPU, system memory or wall power.

The aim is energy-aware agent design. A developer can see which step to shrink, cache or move to another tier, and can set the estimated local electricity cost next to the hosted token cost on one workload, each in its own currency. Every result comes from committed files that others can regenerate on their own GPU.

## How I built it

The core is a Microsoft.Extensions.AI `IChatClient` middleware. Each agent gets its own metered client, created with the agent's name, so attribution is by construction.

NVML is bound through P/Invoke. On Linux the same code loads libnvidia-ml.so.1, so the meter runs unchanged on the cloud instance. When the GPU supports the energy counter, the meter reads it directly. NVML reports millijoules, so

$$E = \frac{C_{\text{end}} - C_{\text{start}}}{1000}$$

gives joules. Otherwise the meter samples power and integrates it with the real sample timestamps:

$$E_{\text{gross}} = \sum_i P_i \Delta t_i$$

A measured idle baseline is subtracted to give net energy over a window of duration T:

$$E_{\text{net}} = E_{\text{gross}} - P_{\text{idle}} T$$

I also report joules per quality point and cost per quality point, each computed only when both inputs exist.

By default, fan-out branches run one after another behind one process-wide meter gate, so only one metered window is open at a time. A concurrent mode runs them together, flags overlapping readings `shared-window`, and computes run energy over the union of the windows, so per-step energy does not sum to the run total.

The workflow is built with Microsoft Agent Framework. Each tier is a base URL, a model ID and a key variable in configuration, so a configuration change switches the tier. By default a hosted Nemotron model judges each step's output on an integer scale from one to five against a committed rubric. Scores are labelled model-judged. Judge calls are metered separately and stay out of the workflow totals.

[[FILL: the agent names and the fan-out shape of the demo workflow | source: distinct values of steps.agent in results/TIMESTAMP-local.json]]

The command line has `probe`, `run`, `report`, `validate` and `calibrate`. Each local run issues one unmetered warm-up call, records an idle baseline with the model resident, and repeats every task. `report` writes a static HTML page and an SVG chart. It shows the median and the minimum to maximum range over the repeats, puts net joules per step in one chart, draws N/A as a text marker instead of an empty bar, and shows every flag next to its value, so a missing measurement cannot be read as zero.

The target laptop has an RTX 3080 Ti Laptop GPU with 16 GB, driver 616.92, idle draw 23.13 W and an enforced power limit of 94.62 W (all measured 2026-10-17 with `nvidia-smi`). The idle baseline of each run is measured separately. NVML power readings may be driver-side averages, the GPU also drives the display, and a laptop power limit applies, so I document these as limitations.

## Challenges I ran into

[[FILL: whether this GPU supports the NVML energy counter and which method the meter chose | source: results/probe-TIMESTAMP.json fields energyCounterSupported and method]]

[[FILL: outcome of the time-boxed NIM container attempt on this laptop, whichever way it ended, and why | source: docs/nim-local-attempt.md sections Outcome and Decision]]

[[FILL: the hardest problem met during the build and how I resolved it | source: the entries of docs/build-log.md]]

Research set two expectations before I started: the larger Nemotron tag I considered for the local tier is marginal for 16 GB of GPU memory and may offload part of the model to the CPU, and NIM-on-RTX packaging officially lists only newer RTX generations than this laptop's GPU.

## Accomplishments that I'm proud of

[[FILL: median net joules per run of the demo workflow on the local tier, with the minimum to maximum range, the number of repeats, GPU, driver, date, runtime, GPU offload percentage and model tag | source: results/TIMESTAMP-local.json fields steps.netJoules, run.repeat and the environment block, as summarized in docs/report/summary.md]]

[[FILL: median net joules per run of the same workload on the Nebius AI Cloud instance, with instance preset, GPU, driver, precision, repeats and observed cost, in its own row next to the laptop figure | source: results/TIMESTAMP-cloudgpu.json and docs/cloudgpu-run.md sections Instance and Cost]]

[[FILL: which agent step used the largest share of local net energy, as a percentage of the summed step net joules and scoped to this workload on this laptop | source: results/TIMESTAMP-local.json fields steps.agent and steps.netJoules]]

[[FILL: how the local run compares with the Nebius Token Factory run, and with the build.nvidia.com run where it has a price, on cost and latency, with the model on each tier named and each cost in its own currency, scoped to this workload | source: results/TIMESTAMP-local.json, results/TIMESTAMP-nvidia.json and results/TIMESTAMP-nebius.json fields totals.costByCurrency, totals.wallClockMs and steps.modelId]]

The code is open source, with a license file in the repository root. The repository and the demo video are linked in this project's Try it out and video fields.

## What I learned

Attribution is a design decision, not a statistic. One metered client per agent, plus a gate that serializes steps, gives each per-step number a known source. Inference from logs would not.

The hosted endpoints do not expose the provider's GPU power, so hosted energy is N/A rather than an estimate.

NVML offers two paths, a counter and power sampling. When both are available, a calibration run measures the same window with both and commits both totals and their relative difference. When only one is available, the committed file says so.

[[FILL: what the calibration run showed on this GPU: the relative difference between the counter and sampling methods, or that the counter is unsupported and no comparison was possible | source: results/calibration-TIMESTAMP.json fields counterJoules, samplingJoules and relativeDifference with their labels and reasons]]

## What's next for Agent Joules

I want an energy budget guardrail that reroutes a step from the local tier to a hosted tier when a joule budget runs out. I also want a quantization comparison of joules per output token, and a second datacenter GPU class next to the H100 for a like-for-like energy comparison.

A LinkedIn post and a long-form article follow, with every number traced to a results file.

## Fill-after-build checklist

This section is not part of the submitted story. It lists what replaces each placeholder, and it is deleted when the placeholders are filled.

| Placeholder | What replaces it | Source artifact |
|---|---|---|
| the agent names and the fan-out shape of the demo workflow | One sentence naming each agent and where the workflow fans out and joins | results/TIMESTAMP-local.json |
| whether this GPU supports the NVML energy counter and which method the meter chose | One sentence stating the probe result | results/probe-TIMESTAMP.json |
| outcome of the time-boxed NIM container attempt on this laptop, whichever way it ended, and why | Outcome and decision in two or three sentences | docs/nim-local-attempt.md |
| the hardest problem met during the build and how I resolved it | A short account | docs/build-log.md |
| median net joules per run of the demo workflow on the local tier, with the minimum to maximum range, the number of repeats, GPU, driver, date, runtime, GPU offload percentage and model tag | The median with its label, the range, n and the environment | results/TIMESTAMP-local.json and docs/report/summary.md |
| median net joules per run of the same workload on the Nebius AI Cloud instance, with instance preset, GPU, driver, precision, repeats and observed cost, in its own row next to the laptop figure | The cloud median with its label, the environment and the observed cost | results/TIMESTAMP-cloudgpu.json and docs/cloudgpu-run.md |
| which agent step used the largest share of local net energy, as a percentage of the summed step net joules and scoped to this workload on this laptop | Agent name, its net joules and its percentage | results/TIMESTAMP-local.json |
| how the local run compares with the Nebius Token Factory run, and with the build.nvidia.com run where it has a price, on cost and latency, with the model on each tier named and each cost in its own currency, scoped to this workload | A scoped comparison sentence per currency, no general claims | results/TIMESTAMP-local.json, results/TIMESTAMP-nvidia.json and results/TIMESTAMP-nebius.json |
| what the calibration run showed on this GPU: the relative difference between the counter and sampling methods, or that the counter is unsupported and no comparison was possible | The measured percentage with its label, or the stated reason it is N/A | results/calibration-TIMESTAMP.json |
