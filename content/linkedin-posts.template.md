<!-- Placeholders resolve from the latest committed results files via: report --render-content -->
## Variant: launch
Tokens are not a cost model. Joules are.

Agent Joules puts a power meter on a multi-agent workflow and reports energy per step, per agent and per run. On the local tier the whole workflow used {{results.local.totals.netJoules.value|F0}} J measured net of idle, with {{results.local.totals.inputTokens.value}} tokens measured in and {{results.local.totals.outputTokens.value}} tokens measured out. The hosted tiers report tokens, latency and, where a price is configured, cost, and their energy is N/A, because nobody outside the provider can meter it.

GPU-board energy measured on one laptop. Scope: this workload, this machine, one run set. No tier is ranked and nothing is averaged across tiers.

Code, method and every results file: {{repo.url}}

## Variant: lesson-learned
I put a power meter on every agent in my workflow.

The first lesson: a single step is not the whole story. The Planner step of the incident-brief task, repeat one, on the {{results.local.environment.gpuName}} drew {{results.local.steps[0].avgW.value|F1}} W measured on average, took {{results.local.steps[0].latencyMs.value|F0}} ms measured, and cost {{results.local.steps[0].netJoules.value|F0}} J measured net of idle. A model judge scored that step {{results.local.steps[0].quality.value}} model-judged, and that score is a judgment, not a measurement.

The idle baseline of the card was {{results.local.baseline.meanW.value|F1}} W measured, flagged baseline-unstable in the file, and it is subtracted before any step is credited.

GPU-board energy measured on one laptop. Hosted tiers report tokens, latency and, where a price is configured, cost; their energy is N/A.

Method and files: {{repo.url}}

## Variant: hackathon
Which of my agents burns the most energy? Now I know, per step.

I built Agent Joules for the Nebius and NVIDIA hackathon. It runs one four-agent workflow on separate tiers and keeps each tier on its own row. The laptop used {{results.local.totals.netJoules.value|F0}} J measured net. One Nebius AI Cloud {{results.cloudgpu.environment.gpuName}} used {{results.cloudgpu.totals.netJoules.value|F0}} J measured net, with {{results.cloudgpu.totals.inputTokens.value}} tokens measured in and {{results.cloudgpu.totals.outputTokens.value}} tokens measured out and {{results.cloudgpu.totals.grossJoules.value|F0}} J measured gross.

The Token Factory run reports {{results.nebius.totals.inputTokens.value}} tokens measured in; their energy is N/A.

GPU-board energy measured on one laptop and on one Nebius AI Cloud GPU, reported separately. Two machines, two rows, no comparison claimed.

Repository: {{repo.url}}
