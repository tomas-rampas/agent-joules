# Agent Joules methodology
Verified: 2026-10-25

This document states what Agent Joules measures, how, and with which limits, so that no energy figure is read as more than it is. Every number below is copied from a named committed file or a named design or build-log line and says whether it is measured, inferred or a placeholder. Statements are scoped to this workload on one laptop; nothing here ranks models.

## Measurement

NVML reports GPU-board energy only. Two methods read it, and both are used on this laptop (design 3.1, the NVML function table):

- Counter method: `nvmlDeviceGetTotalEnergyConsumption` returns millijoules since driver load. A window's gross energy is the counter difference between the two window edges, divided by 1000 to give joules (design 3.3, `ReadingCalculator`).
- Sampling method: `nvmlDeviceGetPowerUsage` returns milliwatts. A background loop stores one power sample per period, and the window's gross energy is the trapezoid integral of the samples, with the first sample held back to the window start and the last held to the window stop (design 3.3). The loop runs at `Meter:SampleRateHz`, 10 in `config/appsettings.json`, so a nominal sample rate of 10 samples per second. Integration uses the actual sample timestamps, never the nominal interval.
- Method selection: `Meter:Method` is `auto` in `config/appsettings.json`. Auto uses the counter when the startup probe finds it supported and otherwise falls back to sampling; the fallback sets the `sampling-fallback` flag on the reading (design 3.3 flags; a forced `sampling` is not a fallback). Average power, peak power and the sample count always come from the samples, for either method (design 3.3).
- Resolution: a window shorter than 2 divided by the sample rate carries the `low-resolution` flag and is kept, not dropped (design 3.3, EDGE-003); at 10 samples per second that is a window under 0.2 s (derived).

Where a window sits relative to the HTTP call (design 3.3, order per call): the call first waits for the meter gate, then `meter.OpenWindow()` is taken, then the inner chat call runs, and in a `finally` `window.Close()` is taken. Opening and closing a window each take an immediate power sample and a counter read, so every window has edge samples at its exact boundaries (design 3.3, energy meter). The design states: "Gate wait is outside the window and outside latency." Retry waits are inside the window: "The energy window, `startUtc` and `endUtc` span the whole call including waits while `latencyMs` and `ttftMs` exclude them" (design 3.3, AJ-045 amendment). The design does not describe the window in terms of bytes on the wire; the window is the span of one HTTP chat call as the client issues it, and a streamed call closes when the stream ends.

Calibration (AJ-109): `AJ calibrate` measures one window of at least `--seconds` seconds (default 60; the window closes at the end of the repeat in flight, so the committed file stores windowSeconds 69.02235230000001) with both methods at once, while the task workflow repeats on pipelines that carry no metered client, so no step reading, results file or judge call is produced; the calibration is the one window (`CalibrationService` remarks, design 3.4 and the Calibration paragraph). The relative difference is (counter minus sampling) divided by counter, and it is N/A with a reason when either total is missing, so nothing is estimated.

Calibration: the committed file results/calibration-20261025T105451Z.json stores relativeDifference -0.004684735625631162 (the counter minus the sampling integral, over the counter).

## Attribution

Attribution is by construction, not by correlation (design 3.3, behaviour item 1):

- One metered client per agent. Each of the four agents, Planner, Researcher, Writer and Reviewer, gets its own `MeteredChatClient` whose identity (agent, model, tier, task, repeat) is a constructor argument. There is no `AsyncLocal`, ambient context or log correlation anywhere. A fresh set of clients is built per tier, task and repeat.
- One window per call. A second concurrent call on the same client instance throws, so a client has one window in flight at a time (design 3.3, item 1).
- Why no ambient context: the agents exchange text only, and a step's energy is the GPU work of that one call above the idle baseline. Nothing outside the call is assigned to it.
- Serialized mode (the default) holds the meter gate for the whole call, so windows never overlap and no `shared-window` flag appears. Concurrent mode takes no gate; a step whose window overlaps another gets the `shared-window` flag from the run assembler (design 3.3 gate, 3.4 step 4). Hosted steps stay exempt from `shared-window`, because the hosted meter returns N/A `hosted-tier` and no window is shared (the M3 kickoff decision (4) in docs/build-log.md; the reason is inferred).
- Judge calls are metered by clients of their own, write to a separate sink, and never enter a step's energy, tokens, cost or totals; the judge's tokens and cost are summed separately in `totals.judge` (design 3.3 sink, 3.4 step 4).
- The warm-up and the model load are unmetered. The warm-up call (Ollama `keep_alive` 30m, or one short chat completion on the NIM) runs before the baseline, and its wall time is recorded as `environment.modelLoadSeconds` (design 3.4, AJ-106 amendment). A cloudgpu run records no such warm-up (the M5 run of 2026-10-26 recorded none: the warm-up through the cloudgpu client is carried to M6; the engine had served the endpoint checks and the calibration minutes before, see docs/cloudgpu-run.md; design 3.4, AJ-106 amendment).
- Replay caveat: a replayed run is not a trace. The results file stores no power trace, so `AJ dashboard --replay` emits one power event per step start and end carrying the step's recorded average watts, and its banner reads `power shown as recorded per-step average` (design 3.10, Replay; the M3 exit carry of AJ-092 in docs/build-log.md).

## Baseline

The idle baseline is measured before the tasks, on the local tier after the warm-up so that the model is resident (design 3.4 step 1; prompt REQ-021). Its length is `Baseline:Seconds`, 30 in `config/appsettings.json`; its mean power is subtracted from every step. The baseline window starts after the warm-up call returns and the runtime version call that follows it, plus the configured settle interval Baseline:SettleSeconds (default 0, as in the published runs). With no settle interval the warm-up's power tail can inflate the baseline mean and so understate net joules. Run files written from M5 on record the settle interval as baseline.settleSeconds; the published M4 files predate it and ran with none; the run file stores the baseline summary, not its samples.

- Gross and net: gross joules are the window's energy as measured; net joules are gross minus baseline mean power times the window length in seconds (design 3.3, `ReadingCalculator`).
- Below-baseline: a negative net is kept as a measured value and flagged `below-baseline`, never clamped (design 3.3). Its cost is N/A `below-baseline` and its `joulesPerQualityPoint` is N/A `below-baseline` (design 3.2, AJ-072 and AJ-074 amendments). A kept negative net lowers the summed net joules and therefore the `totals.gco2e` that is computed from them (the M3 exit carry of AJ-072 on below-baseline nets in docs/build-log.md); when the net energy total itself is negative the total is N/A `below-baseline` (design 3.2, AJ-072 amendment).
- Baseline-unstable: the baseline carries `baseline-unstable` when its coefficient of variation exceeds `Baseline:MaxCoefficientOfVariation`, 0.10 in `config/appsettings.json` (design 3.3 flags). Measured example (the published local run results/20261025T131930Z-local.json, started 2026-10-25 13:19:30Z, NIM resident, values rounded to three decimals from the file): mean 20.109 W, standard deviation 6.971 W, 302 samples over 30.003 s, coefficient of variation 0.347, flagged `baseline-unstable`. That is a measured fact about this laptop with the model resident, and the flag stays visible beside every net value derived from it.
- Non-publishable: a baseline shorter than 30 s prints a warning and sets the run flag `non-publishable`, so the file is never selected as the latest (prompt REQ-021, design 3.3 and 9.2 C-16, AJ-106).
- Concurrent-mode union totals: `totals.netJoules` and `totals.gco2e` come from the union of the step windows, where overlapping windows are merged and each merged segment is counted once, and net subtracts the baseline times the union duration (design 3.3, AJ-047 amendment; prompt REQ-013). Per-step energy therefore does not sum to the total in a concurrent file. Per-step costs each count their own window and `totals.costByCurrency` sums them, so overlapping windows are counted once per step in cost but once in joules and gco2e; a cloudgpu run can bill a VM second more than once. This is design conflict C-17, and the interim rule is that serialized mode is the default and a concurrent file's cost per joule must not be compared with a serialized one (design 9.2, C-17).
- Grid factor: `totals.gco2e` is net kWh times `Cost:GCo2ePerKwh`, labelled `estimated`, and it is N/A `not-configured` while that factor is null (design 3.2, AJ-072 amendment). The code applies one factor to net energy with no tier qualifier (the M3 exit carry of AJ-072 on the grid factor in docs/build-log.md). The methodology decision is that a rented VM in a named region does not carry the laptop's factor: for the cloudgpu tier the factor stays N/A `not-configured` until a region factor is configured, which AJ-120 in M5 decides.
- Median rule: a report row is (tier, variant, task, agent) over the `ok` steps. Its value cells show the median over the repeats with the minimum to maximum range and `n` (`n=<n> of <total>` when some repeats lack a value). A cell with no value keeps its label and is never zero (design 3.5, AJ-082 amendment; the M3 exit carry of AJ-082 in docs/build-log.md). For an even count the median falls between the two middle values, which is why quality may print one decimal (build-log AJ-082 entry, P-11).

## Labels

Every metric is an object with a value and exactly one label (prompt section 6, honesty rules 2 and 3):

- `measured`: read from the meter, the clock or the provider's usage report.
- `estimated`: computed from a configured price or factor, for example a cost from `config/prices.json`.
- `model-judged`: produced by the judge model, or derived from a judged value.
- `N/A`: the value cannot exist for this step, with a `reason` (for example `hosted-tier`, `free-tier-no-price`).
- `not-reported`: the value should exist but the source did not supply it, with a `reason` (for example `provider-omitted-usage`).

A missing value is null and is never written as 0. What each metric carries:

- Tokens: `measured` when the provider reports usage; otherwise `not-reported` with `provider-omitted-usage` (design 3.3, item 5). Streaming usage is requested and reported on all three endpoints recorded in `docs/endpoints.md` (the `Streaming usage: reported` lines).
- Latency and time to first token: `measured`; the time to first token is N/A `buffered-call` for a buffered call (design 3.3).
- Energy (gross, net, average and peak power): `measured` on the local tier, N/A `hosted-tier` for nvidia and nebius (design 3.3, `UnmeteredEnergyMeter`).
- Cost: `estimated`. Hosted tiers use the committed price list; the nvidia free tier is N/A `free-tier-no-price`. The local tier uses net joules over 3.6e6 times `Cost:ElectricityPricePerKwh`, 0.30 in `config/appsettings.json`, in `Cost:Currency` (EUR). The cloudgpu tier uses `CloudGpu:HourlyPriceUsd` times step wall seconds over 3600 (design 3.2, AJ-072 amendment).
- Quality: `model-judged`, an integer from 1 to 5 scored by the judge (design 3.5, schema rules). `run.rubricSha256` is an optional lowercase hex SHA-256 that names the judge rubric a run used (the M3 exit carry of AJ-079 in docs/build-log.md).
- `joulesPerQualityPoint`: net joules over the quality score, `model-judged`, N/A `missing-input` or `below-baseline` otherwise. `costPerQualityPoint`: cost over the quality score, `estimated` in the cost's currency since decision K0 (design 3.2, AJ-074 and K0 amendments).

Flags, one sentence each (names from the prompt label vocabulary and design 3.3, 3.4 and 9.2 C-16):

- `sampling-fallback`: `auto` fell back from the counter to sampling.
- `low-resolution`: the window was shorter than 2 divided by the sample rate.
- `throttled`: a power or thermal clock-event reason was seen in the window; the reasons are recorded per step in `throttleReasons`.
- `below-baseline`: the net energy is negative.
- `baseline-unstable`: the baseline coefficient of variation exceeded the configured maximum.
- `non-publishable`: a run flag for a baseline under 30 s; such a file is never the latest.
- `partial-offload`: a run flag when the local model is below 100 percent on the GPU.
- `duplicate-output`: an `ok` step produced the same output hash as an earlier step of the same task and repeat (design 3.2, AJ-104 amendment).
- `shared-window`: the step's window overlapped another in concurrent mode.
- `max-tokens-exceeded`: the reported output tokens exceeded the call's limit (design 3.3, AJ-042 amendment).
- `counter-reset`: the counter went backwards inside a window; the vocabulary lists it but the writer never emits it (design 9.2 C-16).

## Limitations

- GPU-only: the energy is GPU-board energy. CPU, memory, storage, the network and wall power are not measured (prompt REQ-022).
- display: the laptop GPU drives the display, so compositing load is part of the idle baseline and of every window.
- power limit: the laptop's limit changes with the power mode. The published local run results/20261025T131930Z-local.json embeds the probe taken at run time and records powerLimitW 105 (the run-time value); the campaign probe results/probe-20261025T131800Z.json, saved before the run at 13:18:00Z, records powerLimitW 96.39; the preflight probe results/probe-20261025T104744Z.json, saved at 10:47:44Z, recorded powerLimitW 87.393 (a dated history point only). The NVML enforced limit and the `nvidia-smi` limit may differ: the design records 85.059 W from NVML and 94.62 W from `nvidia-smi`, measured on 2026-10-18 and 2026-10-17 (design 9.2 C-11, measured), and attributes the difference to dynamic power management (inferred).
- sampling: power samples arrive at 10 per second (`Meter:SampleRateHz`), so short windows are coarse, and `nvmlDeviceGetPowerUsage` may be a driver-side average rather than an instantaneous value: the design records a 1 s driver average on Ampere and newer parts except GA100 (design 3.1) and states that this GPU's behaviour is unverified (docs/plan/plan.md risk R-02). The counter method is not subject to this, and the calibration quantifies the gap.
- offload: if the local model does not fit entirely on the GPU, part of the work runs on the CPU and is not measured. The run carries `partial-offload` and `environment.gpuOffloadPercent` records the share. The NIM serves the model fully on the GPU, so offload is 100 by construction, not by observation (design 3.4, AJ-105 amendment; gpuOffloadPercent 100 in results/20261025T131930Z-local.json).
- hosted: energy for the nvidia and nebius tiers is N/A `hosted-tier`. Only tokens, latency and cost are measured or estimated there, and no hosted figure is a joule claim.
- cloud: the cloudgpu tier is a dedicated VM GPU running the provider's driver and host power management. Its per-step cost is estimated from the hourly price, and the observed cost comes from the billing page, not from metering. Laptop joules and datacenter joules are never compared as efficiency claims, and the rows are never merged or averaged (prompt REQ-022 and the cloudgpu rule that laptop and cloud rows stay separate).
- laptop throttling (EDGE-013): the clock-event reasons API worked on this laptop. The published local run results/20261025T131930Z-local.json (started 2026-10-25 13:19:30Z) carried the `throttled` flag on 36 of its 36 steps with the reason SwPowerCap (the software power cap; peak power 95.4 to 106.1 W per step while its embedded probe recorded powerLimitW 105; the limit moves with the laptop's power management, see the power limit item), and the reasons are recorded per step. Net energy in a throttled step reflects a GPU running below its unthrottled clocks.
- output caps: each task caps the answer (`maxOutputTokens` in `tasks/*.json`: incident-brief 512, api-migration 768, energy-explainer 384). Counted from the published run files, steps that stopped exactly at the cap: 21 of 36 local steps (results/20261025T131930Z-local.json), 6 of 36 nebius steps (results/20261025T133440Z-nebius.json) and 6 of 36 nvidia steps (results/20261025T135121Z-nvidia.json). `max-tokens-exceeded` fires only above the cap, so a capped answer is judged as returned and carries no flag, and its quality and `joulesPerQualityPoint` describe a possibly truncated answer. A finish-reason flag is carried to M6 or M7.
- reasoning mode: every tier runs with reasoning off through a toggle verified per tier (`docs/endpoints.md` `Reasoning result: off` lines, AJ-108), so the energy is that of the answer, not of a thinking trace. A different reasoning setting would change tokens, latency and joules.
- The numbers describe one laptop, one workload and one model set. They are not a leaderboard and carry no general claim that one model is greener than another.

The dashboard statements (REQ-032):

The dashboard server adds no GPU work.
A browser tab adds compositing load, so published runs are recorded without one.
The 2026-10-25 campaign that produced the published local file ran with the user's other GPU applications open (the user's decision of 12:32Z recorded in the AJ-114 entry of docs/build-log.md), so that load sits in the baseline and in every local window.
Estimates shown on the live dashboard never enter results files.
