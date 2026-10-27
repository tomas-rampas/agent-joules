Sources: 20261025T133440Z-nebius.json, 20261026T124721Z-cloudgpu.json, 20261027T095519Z-local.json

<!-- Placeholders resolve from the latest committed results files via: report --render-content -->

## Variant: launch
Tokens are not a cost model. Joules are.

Agent Joules puts a power meter on a multi-agent workflow and reports energy per step, per agent and per run. On the local tier the whole workflow used 33134 J measured net of idle, with 25770 tokens measured in and 17556 tokens measured out. The hosted tiers report tokens, latency and, where a price is configured, cost, and their energy is N/A, because nobody outside the provider can meter it.

GPU-board energy measured on one laptop. Scope: this workload, this machine, one run set. No tier is ranked and nothing is averaged across tiers.

Code, method and every results file: {{repo.url}}

## Variant: lesson-learned
I put a power meter on every agent in my workflow.

The first lesson: a single step is not the whole story. The Planner step of the incident-brief task, repeat one, on the NVIDIA GeForce RTX 3080 Ti Laptop GPU drew 65.7 W measured on average, took 9023 ms measured, and cost 398 J measured net of idle. A model judge scored that step 3 model-judged, and that score is a judgment, not a measurement.

The idle baseline of the card was 22.4 W measured, flagged baseline-unstable in the file, and it is subtracted before any step is credited.

GPU-board energy measured on one laptop. Hosted tiers report tokens, latency and, where a price is configured, cost; their energy is N/A.

Method and files: {{repo.url}}

## Variant: hackathon
Which of my agents burns the most energy? Now I know, per step.

I built Agent Joules for the Nebius and NVIDIA hackathon. It runs one four-agent workflow on separate tiers and keeps each tier on its own row. The laptop used 33134 J measured net. One Nebius AI Cloud NVIDIA H100 80GB HBM3 used 10390 J measured net, with 25379 tokens measured in and 15889 tokens measured out and 16364 J measured gross.

The Token Factory run reports 26554 tokens measured in; their energy is N/A.

GPU-board energy measured on one laptop and on one Nebius AI Cloud GPU, reported separately. Two machines, two rows, no comparison claimed.

Repository: {{repo.url}}
