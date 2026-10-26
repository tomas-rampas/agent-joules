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

The demo workflow has four agents, Planner, Researcher, Writer and Reviewer, where the Planner runs first, the Researcher and the Writer run in parallel branches, and the Reviewer joins them (4 distinct steps.agent values, measured; results/20261025T131930Z-local.json).

The command line has `probe`, `run`, `report`, `validate` and `calibrate`. Each local run issues one unmetered warm-up call, records an idle baseline with the model resident, and repeats every task. `report` writes a static HTML page and an SVG chart. It shows the median and the minimum to maximum range over the repeats, puts net joules per step in one chart, draws N/A as a text marker instead of an empty bar, and shows every flag next to its value, so a missing measurement cannot be read as zero.

The target laptop has an RTX 3080 Ti Laptop GPU with 16 GB, driver 616.92, an idle baseline of 20.11 W (measured, flagged baseline-unstable) and an enforced power limit of 105 W (measured) embedded in the run file, under which every step carried the throttled flag (results/20261025T131930Z-local.json); the probe taken minutes before the run recorded a 96.39 W limit (results/probe-20261025T131800Z.json, measured), so the enforced limit changed between the probe and the run (inferred). The idle baseline of each run is measured separately. NVML power readings may be driver-side averages, the GPU also drives the display, and a laptop power limit applies, so I document these as limitations.

## Challenges I ran into

The laptop GPU, an NVIDIA GeForce RTX 3080 Ti Laptop GPU on driver 616.92, supports the NVML energy counter: the probe reports energyCounterSupported true and method counter (measured; results/probe-20261025T131800Z.json).

I gave the NIM container on this laptop a 90-minute box, and it served GET /v1/models with HTTP 200 after 32 minutes 6 seconds (measured; docs/nim-local-attempt.md, Outcome). The working configuration is the BF16 vllm profile with NIM_MAX_MODEL_LEN=16384, so the decision is `Local runtime: nim` and the Ollama fallback was not needed (docs/nim-local-attempt.md, Decision). It rests on one successful start that I have not repeated.

The hardest problem was getting the cloud GPU, not the code. On 2026-10-26 the first virtual machine I created, platform gpu-l40s-d with preset 2gpu-64vcpu-384gb in eu-north1, did not start. No run was made on the L40S because both Start operations of that machine ended "Resource exhausted" (docs/build-log.md, entry AJ-125 of 2026-10-26), so it has no energy figure (N/A). I stopped and chose the single H100 preset of the same region instead, because the NIM support matrix lists both the L40S and the H100, the region stayed eu-north1 and the energy counter is present. That H100 machine produced the cloud run below (docs/build-log.md, entry AJ-125 of 2026-10-26; docs/cloudgpu-run.md, Instance).

Research set two expectations before I started: the larger Nemotron tag I considered for the local tier is marginal for 16 GB of GPU memory and may offload part of the model to the CPU, and NIM-on-RTX packaging officially lists only newer RTX generations than this laptop's GPU.

## Accomplishments that I'm proud of

On the laptop (NVIDIA GeForce RTX 3080 Ti Laptop GPU, driver 616.92, run date 2026-10-25 UTC, NIM 1.8.5, GPU offload 100 percent, model nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 in BF16, 3 repeats), the median net energy per run is 2872.28 J (measured), with a range of 2706.95 J to 5738.61 J (measured) over 9 runs. A run here is one task in one repeat (3 tasks x 3 repeats), and its energy is the sum of the netJoules of its four steps; no run was skipped, and the median, the range and the count are taken from the measured per-step values (results/20261025T131930Z-local.json, measured). That run's idle baseline of 20.11 W (measured) carries the baseline-unstable flag, with a coefficient of variation of 0.347 against the limit of 0.1, and every step carries the throttled flag, because the laptop's software power cap applied.

On the Nebius AI Cloud instance, one NVIDIA H100 80GB HBM3 (preset 1gpu-16vcpu-200gb, region eu-north1, driver 580.173.02, FP8, 3 repeats, counter method), the median net energy per run is 929.87 J (measured), with a range of 648.52 J to 1803.81 J (measured) over 9 runs. A run here is one task in one repeat, and its energy is the sum of the netJoules of its steps; the median, the range and the median per step of 233.29 J (measured) are taken from the measured per-step values in results/20261026T124721Z-cloudgpu.json. The cost is about USD 7 (estimated: 1.509 GPU-hours at USD 4.63 per hour; the balance difference USD 5.92 is a lower bound; docs/cloudgpu-run.md, Teardown). This row stays separate from the laptop row.

On this workload on this laptop, the Researcher used the largest share of local net energy: 9482.36 J, 28.1 percent of the 33716.77 J summed over all steps (measured, derived from the measured per-step values in results/20261025T131930Z-local.json). The Reviewer used 27.2 percent, the Writer 23.8 percent and the Planner 20.8 percent, each rounded to 0.1.

Cost and latency on the four tiers come from the four committed run files (results/20261025T131930Z-local.json, results/20261025T133440Z-nebius.json, results/20261025T135121Z-nvidia.json, results/20261026T124721Z-cloudgpu.json; fields totals.costByCurrency, totals.wallClockMs, steps.modelId, steps.latencyMs and steps.ttftMs), and each cost stays in its own currency. The local tier, running nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 on NIM 1.8.5, cost 0.0028 EUR (estimated, from the electricity price assumption of docs/methodology.md), with a wall clock of 465607 ms, a median step latency of 12224 ms and a median time to first token of 234 ms (all measured). In USD, Nebius Token Factory, running nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B, cost 0.0057 USD (estimated), and the Nebius AI Cloud instance, running nvidia/nemotron-3-nano in FP8, cost 0.0647 USD (estimated: the hourly price times the step time). build.nvidia.com, running nvidia/nemotron-3-nano-omni-30b-a3b-reasoning, has no cost figure (N/A: no price is configured for the free endpoint). The Token Factory run had a wall clock of 70139 ms, a median step latency of 1721 ms and a median time to first token of 275 ms (all measured). The build.nvidia.com run had a wall clock of 358368 ms, a median step latency of 8371 ms and a median time to first token of 353 ms (all measured), and 16 step retries with 14925 ms of waiting after overload rejections (HTTP 429, 503 or 500 ResourceExhausted; the run file records the counts, not the status; results/20261025T135121Z-nvidia.json, measured). The Nebius AI Cloud run had a wall clock of 50734 ms, a median step latency of 1142 ms and a median time to first token of 65 ms (all measured). The medians are over the 36 steps of each run, taking the mean of the two middle values. The four tiers ran four different models, so each figure belongs to its own tier and model, on this workload.

The code is open source, with a license file in the repository root. The repository and the demo video are linked in this project's Try it out and video fields.

## What I learned

Attribution is a design decision, not a statistic. One metered client per agent, plus a gate that serializes steps, gives each per-step number a known source. Inference from logs would not.

The hosted endpoints do not expose the provider's GPU power, so hosted energy is N/A rather than an estimate.

NVML offers two paths, a counter and power sampling. When both are available, a calibration run measures the same window with both and commits both totals and their relative difference. When only one is available, the committed file says so.

On the Nebius AI Cloud H100 the energy counter is supported, and the calibration run (60 s calibration, 62.18 s window) measured 20796.42 J with the counter and 20679.15 J with sampling, a relative difference of 0.0056 (0.56 percent, measured; results/calibration-20261026T124311Z-cloudgpu.json).

On the laptop the calibration run measured a relative difference of -0.0047 (-0.47 percent, measured) between the counter total of 5753.404 J and the sampling total of 5780.357 J (results/calibration-20261025T105451Z.json).

## What's next for Agent Joules

I want an energy budget guardrail that reroutes a step from the local tier to a hosted tier when a joule budget runs out. I also want a quantization comparison of joules per output token, and a second datacenter GPU class next to the H100 for a like-for-like energy comparison.

A LinkedIn post and a long-form article follow, with every number traced to a results file.
