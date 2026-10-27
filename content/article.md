# I put a power meter on every agent in my workflow

Which of my agents burns the most energy? Agent Joules answers per step: a metered `IChatClient` middleware records, for every model call an agent makes on a GPU you control, the joules the GPU spent, the power profile, the tokens, the latency, the cost and a quality score. This article explains the method, the prior art, what is new, the results with the file behind each number, and the limits. Energy is GPU-board energy only, and every statement is scoped to this workload on this machine.

## Prior art

Published work on LLM inference energy measures the model or the request. I checked the links on 2026-10-26.

- [The Price of Prompting: Profiling Energy Use in Large Language Models Inference](https://arxiv.org/abs/2407.16893), arXiv 2407.16893, profiles the energy use of LLM inference.
- [TokenPowerBench: Benchmarking the Power Consumption of LLM Inference](https://ojs.aaai.org/index.php/AAAI/article/view/40535), AAAI-26 (preprint [arXiv 2512.03024](https://arxiv.org/abs/2512.03024)), is a benchmark for the power consumption of LLM inference, including joules per token.
- [Watt Counts: Energy-Aware Benchmark for Sustainable LLM Inference on Heterogeneous GPU Architectures](https://arxiv.org/abs/2604.09048), arXiv 2604.09048, is a preprint under review that benchmarks inference energy across GPU architectures.
- [greenToken](https://github.com/abidedavana/greenToken) (installed as the `greentoken` package and CLI, per its README) is a Python library and command-line tool that measures energy, CO2e and cost per token of LLM inference through NVML, nvidia-smi and RAPL. The repository is early-stage.

## What is new

None of these attributes energy to an individual agent step of a multi-agent workflow, as far as the search of 2026-10-17 found. That is the gap. Agent Joules does per-agent-step attribution in .NET: each agent of a Microsoft Agent Framework workflow gets its own metered `IChatClient`, and the workflow runs Nemotron models on four tiers: a local NVIDIA NIM on a laptop GPU, one rented Nebius AI Cloud GPU VM, Nebius Token Factory, and build.nvidia.com. The claim is narrow: it is a claim about the search date, not about all work everywhere.

## How it works

**Attribution by construction.** The four agents, Planner, Researcher, Writer and Reviewer, each get a `MeteredChatClient` whose identity (agent, model, tier, task, repeat) is a constructor argument. There is no ambient context and no log correlation. One client has one energy window in flight, and a second concurrent call on it throws.

**Serialized gate.** By default a call waits for a meter gate before its window opens, so windows never overlap and no step shares energy with another. The gate wait is outside the window.

**Counter versus sampling.** NVML reports GPU-board energy two ways. The counter method reads `nvmlDeviceGetTotalEnergyConsumption` at the window edges. The sampling method integrates `nvmlDeviceGetPowerUsage` samples taken at 10 samples per second. The meter uses the counter when the startup probe finds it supported and falls back to sampling, with a `sampling-fallback` flag, otherwise. A calibration window runs both at once so the gap is a number, not a guess.

**Idle baseline.** The baseline is measured for a fixed period after the model is warm and resident, and its mean power times the window length is subtracted from each step. Gross is the window as measured; net is gross minus that baseline. A negative net is kept and flagged, never clamped.

**Warm-up.** The model load and a first call run before the baseline and are not metered.

**Labels.** Every value carries exactly one of `measured`, `estimated`, `model-judged`, `N/A` or `not-reported`. A missing value is null, never zero. The judge model scores quality from one to five; judge calls are excluded from the workflow totals.

## Results

Four run files (local, cloudgpu, nvidia, nebius), each with three tasks x three repeats x four agents, that is thirty-six steps, all ok. Laptop and cloud rows are separate tables and are never averaged or divided by each other. Each table is preceded by its environment header, copied from the run file it names.

GPU: NVIDIA GeForce RTX 3080 Ti Laptop GPU; driver: 616.92; run date (UTC): 2026-10-25; local runtime: nim 1.8.5; model: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 (BF16); file: results/20261025T131930Z-local.json

| Figure | Value | Label | File |
|---|---|---|---|
| Net energy, run total | 33716.77 J | measured | results/20261025T131930Z-local.json |
| Gross energy, run total | 43061.1 J | measured | results/20261025T131930Z-local.json |
| Input tokens | 25770 tokens | measured | results/20261025T131930Z-local.json |
| Output tokens | 17556 tokens | measured | results/20261025T131930Z-local.json |
| Wall clock | 465607 ms | measured | results/20261025T131930Z-local.json |
| Cost | 0.0028 EUR | estimated | results/20261025T131930Z-local.json |
| Idle baseline mean (flag baseline-unstable) | 20.1 W | measured | results/20261025T131930Z-local.json |
| Net energy per task repeat, median (derived: the sum of the four steps of a task repeat) | 2872.28 J | measured | results/20261025T131930Z-local.json |
| Net energy per task repeat, minimum (derived) | 2706.95 J | measured | results/20261025T131930Z-local.json |
| Net energy per task repeat, maximum (derived) | 5738.61 J | measured | results/20261025T131930Z-local.json |
| Planner share of net energy (derived) | 20.8 % | measured | results/20261025T131930Z-local.json |
| Researcher share of net energy (derived) | 28.1 % | measured | results/20261025T131930Z-local.json |
| Reviewer share of net energy (derived) | 27.2 % | measured | results/20261025T131930Z-local.json |
| Writer share of net energy (derived) | 23.8 % | measured | results/20261025T131930Z-local.json |

A per-task-repeat figure is the sum of the four steps of one task and repeat; there are nine task repeats, and the median, minimum and maximum above are taken over the nine task repeats. The shares are derived from the measured step values, each as a share of the run total of 33716.77 J measured (results/20261025T131930Z-local.json). The first step (incident-brief, Planner, repeat one) is a worked example.

- Net energy 435.7 J measured, average power 65.7 W measured, latency 8743.6 ms measured, quality 3 model-judged (results/20261025T131930Z-local.json).

GPU: NVIDIA H100 80GB HBM3; driver: 580.173.02; run date (UTC): 2026-10-26; local runtime: N/A; model: nvidia/nemotron-3-nano (FP8); file: results/20261026T124721Z-cloudgpu.json

| Figure | Value | Label | File |
|---|---|---|---|
| Net energy, run total | 10389.62 J | measured | results/20261026T124721Z-cloudgpu.json |
| Gross energy, run total | 16364.05 J | measured | results/20261026T124721Z-cloudgpu.json |
| Input tokens | 25379 tokens | measured | results/20261026T124721Z-cloudgpu.json |
| Output tokens | 15889 tokens | measured | results/20261026T124721Z-cloudgpu.json |
| Wall clock | 50734 ms | measured | results/20261026T124721Z-cloudgpu.json |
| Cost (hourly price times step time) | 0.0647 USD | estimated | results/20261026T124721Z-cloudgpu.json |
| Net energy per task repeat, median (derived: the sum of the four steps of a task repeat) | 929.87 J | measured | results/20261026T124721Z-cloudgpu.json |
| Net energy per task repeat, minimum (derived) | 648.52 J | measured | results/20261026T124721Z-cloudgpu.json |
| Net energy per task repeat, maximum (derived) | 1803.81 J | measured | results/20261026T124721Z-cloudgpu.json |

The cloud run file records no local runtime; the engine was NIM 2.1.4 (docs/cloudgpu-run.md). The VM was a Nebius AI Cloud preset 1gpu-16vcpu-200gb in region eu-north1. Three of the thirty-six judge calls are N/A after an HTTP 429 (docs/cloudgpu-run.md records the RetriesExhaustedException), so their quality is N/A, not zero. The cloud run made no warm-up call before its baseline (the "Deviation, no warm-up call" entry of docs/cloudgpu-run.md): the engine had served the endpoint checks and the calibration minutes earlier, modelLoadSeconds is null in the file, and the first-token latencies of repeat 1 are disclosed there.

- Rental: 1.509 GPU-hours at USD 4.63 per hour, a whole-run cost of about USD 7 estimated from the console hourly price, not the bill (docs/cloudgpu-run.md).

The hosted tiers report tokens, latency and, where a price is configured, cost, and energy is N/A.

GPU: N/A; driver: N/A; run date (UTC): 2026-10-25; local runtime: N/A; model: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning (N/A); file: results/20261025T135121Z-nvidia.json

| Figure | Value | Label | File |
|---|---|---|---|
| Energy | N/A (hosted-tier) | N/A | results/20261025T135121Z-nvidia.json |
| Input tokens | 25550 tokens | measured | results/20261025T135121Z-nvidia.json |
| Output tokens | 16548 tokens | measured | results/20261025T135121Z-nvidia.json |
| Wall clock | 358368 ms | measured | results/20261025T135121Z-nvidia.json |
| Cost | N/A (no priced step) | N/A | results/20261025T135121Z-nvidia.json |

This run is self-judged: the judge is the same model as the workflow model, so its quality scores need that caveat (results/20261025T135121Z-nvidia.json).

GPU: N/A; driver: N/A; run date (UTC): 2026-10-25; local runtime: N/A; model: nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B (N/A); file: results/20261025T133440Z-nebius.json

| Figure | Value | Label | File |
|---|---|---|---|
| Energy | N/A (hosted-tier) | N/A | results/20261025T133440Z-nebius.json |
| Input tokens | 26554 tokens | measured | results/20261025T133440Z-nebius.json |
| Output tokens | 16991 tokens | measured | results/20261025T133440Z-nebius.json |
| Wall clock | 70139 ms | measured | results/20261025T133440Z-nebius.json |
| Cost | 0.0057 USD | estimated | results/20261025T133440Z-nebius.json |

The tiers run different models, so these tables answer how much each configuration spent on this workload, and nothing more.

Calibration of the two methods on the laptop:

- Counter 5753.404 J measured against sampling 5780.357 J measured, a relative difference of -0.0047 measured (results/calibration-20261025T105451Z.json).
- On the cloud GPU the counter read 20796.42 J measured and sampling 20679.15 J measured, a gap of 0.56 % measured (results/calibration-20261026T124311Z-cloudgpu.json).
- The startup probe found the counter supported, method counter (results/probe-20261025T131800Z.json).

## Limitations

Energy is GPU-board energy only. CPU, memory, storage, network and wall power are not measured, so this is a GPU-only figure.

- The laptop GPU also drives the display, so display compositing load sits in the idle baseline and in every window. The campaign ran with other GPU applications open, and the dashboard in a browser tab adds load of its own, so published runs are recorded without one.
- The laptop has a software power limit that moves with its power mode. The local run was throttled on every step by the software power cap (results/20261025T131930Z-local.json, measured flags).
- The power limit changes net energy, because a throttled GPU runs below its unthrottled clocks.
- The sampling method is coarse for short windows, and the driver power reading may be a driver-side average. The counter method does not have this problem, which is why the calibration above exists.
- The idle baseline of the laptop run is unstable: its coefficient of variation is 0.347 against a limit of 0.1 (results/20261025T131930Z-local.json, measured), so each net value there is read beside that flag.
- The NIM serves the laptop model fully on the GPU, so offload is 100 percent by construction (measured as `gpuOffloadPercent` in results/20261025T131930Z-local.json). A partly offloaded model would run partly on the CPU, unmeasured.
- The hosted tiers have no joules at all. No hosted figure is an energy claim.
- The cloud tier is a rented VM: its cost is the hourly price times time, and the observed cost comes from the billing page, not from metering. The laptop used NIM 1.8.5 and the cloud NIM 2.1.4, so engine versions differ as well as models and GPUs.
- Reasoning mode is off on every tier, and some answers stopped at the output cap, so quality describes a possibly truncated answer.
- The numbers describe one workload on four configurations (a laptop GPU, one cloud GPU, two hosted APIs), each with its own model. They are not a ranking of models.

## Reproduce

The README has a Reproduce section with ten commands: probe, a dry run, the full run, calibration, the cloud VM steps, validation, the report, the README verification, the live dashboard and a GPU-free replay of a committed hosted run. Without a GPU, replay a committed run and read every number from the files under `results/`. With a GPU you control, `run --tier local` writes a new file and `validate` checks it.

## What is next

The next step is more tasks and more repeats, a second laptop, a finish-reason flag for capped answers, and a region grid factor so the cloud tier can report CO2e. If you run it on your own GPU, I would like to see your results file.
