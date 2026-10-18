# Agent Joules: Nemotron and Nebius feedback answers

This file holds my answers to the Nemotron and Nebius feedback questionnaire on the Devpost submission form. I drafted it before the build, so it states only design decisions I have already taken and leaves everything that needs a measurement or my own opinion as a placeholder. A placeholder is the word FILL inside double square brackets, followed by what goes there, where it comes from, and an owner, which is either builder (filled from a results file or a doc) or user (my own opinion or rating). Every placeholder has one row in the Fill checklist at the end.

## Q1. Which model(s) did you use, and why did you choose that size/variant? (e.g., Nemotron-4 340B, Nemotron-Mini, etc.)

This answer is a design decision, written before the build.

The primary hosted model is Nemotron 3 Nano 30B-A3B. I use it on build.nvidia.com, and on Nebius Token Factory if Token Factory offers it. If Token Factory does not offer it, I use Nemotron 3 Super 120B-A12B there instead, and the report says so next to every number from that tier.

I chose this size for two reasons. First, it is a mixture-of-experts model that activates only a small share of its parameters per token (the A3B in the name). My design assumption is that this keeps latency and cost per agent step low, which is what the project measures. I treat that as an assumption to test, not as a result. Second, the same family can run on my laptop, so per-step comparisons across tiers can stay like-for-like where the project's model-parity rule requires it.

The laptop has an RTX 3080 Ti Laptop GPU with 16 GB of memory. The local tier uses the Ollama tag nemotron-3-nano:30b or a smaller Nemotron tag, and I attempt an NVIDIA NIM container first with a 4B-class Nemotron model. Research on 2026-10-17 found the 30B-A3B tag marginal for 16 GB, so part of it may be offloaded to the CPU, and the report records the offload percentage.

I do not target the larger variants, Nemotron 3 Super 120B-A12B and Nemotron 3 Ultra 550B-A55B, because the local tier cannot host them. Super appears only as the fallback on Nebius. A non-Nemotron model, Qwen3 8B, is already pulled and runs only as a local baseline, labelled as such. A hosted Nemotron model also acts as the quality judge, and the report discloses it when the judge and the step model are the same.

Exact model IDs are verified with GET /v1/models on each endpoint and recorded in docs/endpoints.md after the build.

## Q2. How would you rate Nemotron's output quality for your use case? (1 = Poor, 10 = Excellent) Please share what worked well or fell short, e.g., reasoning, instruction-following, domain knowledge, multilingual support.

Quality is scored by a judge model on an integer scale against a rubric committed at config/judge-rubric.md, labelled model-judged and reported per tier.

[[FILL: my rating from 1 (Poor) to 10 (Excellent), and what worked well or fell short in reasoning, instruction-following, domain knowledge and multilingual support, from my own reading of the outputs | source: the user | owner: user]]

[[FILL: the judge quality score per tier with its model-judged label, plus the outcome and retry counts per tier | source: results/TIMESTAMP-nebius.json, results/TIMESTAMP-nvidia.json and results/TIMESTAMP-local.json fields steps.quality, steps.outcome and steps.retries | owner: builder]]

## Q3. Did you fine-tune, prompt-engineer, or use Nemotron out of the box? What was your approach?

This answer is a design decision, written before the build.

I use Nemotron out of the box. I do not fine-tune: training or fine-tuning any model is out of scope for this project by design, because the project measures how a workflow behaves on models as they are served.

My only lever is prompt engineering. Each agent role gets one instruction. A judge rubric is committed at config/judge-rubric.md so that anyone can read how quality is scored. Temperature is fixed, and the same reasoning mode is used on every tier, set through the mechanism each endpoint documents and recorded per tier in the results as reasoningMode. A seed is passed when the endpoint supports one. Every call also uses the maximum output length set in the committed task file. Holding these constant is what lets results compare across tiers, and it means a stranger can rerun the same prompts against the same model IDs. If an endpoint cannot set the reasoning mode, the report header and README say so.

## Q4. How did Nemotron's performance compare to other models you've used for similar tasks?

Every comparison is scoped to this workload on this laptop, and each cost stays in its own currency.

[[FILL: if the baseline run was made, the Qwen3 8B baseline against the Nemotron local model on quality, latency and net joules, on the same local tier | source: the results/TIMESTAMP-local.json run files, one per model tag, fields steps.modelId, steps.quality, steps.latencyMs and steps.netJoules | owner: builder]]

[[FILL: cost and latency across the three tiers, with the model named on each tier | source: results/TIMESTAMP-local.json, results/TIMESTAMP-nvidia.json and results/TIMESTAMP-nebius.json fields steps.modelId, steps.latencyMs, steps.ttftMs and totals.costByCurrency | owner: builder]]

[[FILL: how Nemotron compared with other models I have used for similar tasks outside this project | source: the user | owner: user]]

## Q5. Which Nebius platform capabilities were most valuable to your project and how? Please include detail on how you leveraged Nebius infrastructure to run Nemotron (e.g., GPU instance types, deployment setup, API access, scaling).

This answer is a design decision, written before the build.

I use the Nebius Token Factory hosted API and nothing else on Nebius. The planned setup is the OpenAI-compatible chat completions API, called from a .NET 10 program through the OpenAI SDK with a custom endpoint. Research on 2026-10-17 found that Token Factory serves Nemotron 3 Super through this API at https://api.tokenfactory.nebius.com/v1/ and could not confirm whether Nano or Ultra are offered. I verify the model IDs and the base URL myself with GET /v1/models and record them in docs/endpoints.md. The API key is held in an environment variable or in dotnet user-secrets, and it is never committed. I plan to pay for the runs with the hackathon credits.

I do not use GPU instances, self-hosted deployment or autoscaling, so I have no instance types or scaling setup to report. Running a NIM container on a Nebius cloud GPU is a stretch goal, and it is not part of this submission unless I do it, in which case it is reported separately from the laptop numbers.

The hosted tiers report tokens, latency, time to first token, cost where a price exists, outcome and retry counts. Energy is measured only on the local GPU and is N/A for Nebius.

[[FILL: the model IDs and base URL verified on Nebius Token Factory, and the date they were verified | source: docs/endpoints.md section nebius | owner: builder]]

[[FILL: whether the hackathon credits covered all the Nebius runs | source: docs/build-log.md | owner: builder]]

[[FILL: which Nebius capability was most valuable to this project and how | source: the user | owner: user]]

## Q6. How likely are you to recommend running Nemotron on Nebius to other developers? (1 = Unlikely, 10 = Highly Likely) Please share why or provide additional feedback.

[[FILL: my likelihood to recommend from 1 (Unlikely) to 10 (Highly Likely), and why | source: the user | owner: user]]

## Q7. How would you rate your experience running Nemotron inference on Nebius compared to previous cloud or local development environments? (1 = Poor, 10 = Excellent) Please share why or provide additional feedback.

Latency and time to first token are measured per step, so the Nebius figures sit next to the same fields from build.nvidia.com and from the local tier.

[[FILL: my rating from 1 (Poor) to 10 (Excellent), and how the experience compared with previous cloud or local environments I have used | source: the user | owner: user]]

[[FILL: measured latency, time to first token, outcome, retry count and wait time on Nebius against build.nvidia.com and local | source: results/TIMESTAMP-nebius.json, results/TIMESTAMP-nvidia.json and results/TIMESTAMP-local.json fields steps.latencyMs, steps.ttftMs, steps.outcome, steps.retries and steps.waitMs | owner: builder]]

[[FILL: the outcome of the time-boxed NIM container attempt on the local laptop, whichever way it ended | source: docs/nim-local-attempt.md sections Outcome and Decision | owner: builder]]

## Q8. What additional features or improvements would have made the Nemotron on Nebius experience more effective for your project?

[[FILL: the features or improvements I wanted while building the project | source: the user | owner: user]]

## Q9. What do you most hope to see from the Nemotron team next?

[[FILL: what I most hope to see from the Nemotron team next | source: the user | owner: user]]

## Fill checklist

| Placeholder | Owner | What replaces it | Source artifact |
|---|---|---|---|
| Q2 rating and notes | user | My rating and what worked or fell short, in my words | The user |
| Q2 judge scores, outcomes, retries | builder | Per-tier quality scores with the model-judged label, and outcome and retry counts | results/TIMESTAMP-nebius.json, results/TIMESTAMP-nvidia.json, results/TIMESTAMP-local.json |
| Q4 Qwen3 8B baseline comparison | builder | Quality, latency and net joules for the baseline and the Nemotron local model, same tier | results/TIMESTAMP-local.json, one run per model tag |
| Q4 cross-tier cost and latency | builder | Cost and latency per tier with the model named, each cost in its own currency | results/TIMESTAMP-local.json, results/TIMESTAMP-nvidia.json, results/TIMESTAMP-nebius.json |
| Q4 other models I have used | user | My comparison with other models used for similar tasks | The user |
| Q5 verified IDs and base URL | builder | The verified Nebius model IDs, base URL and verification date | docs/endpoints.md |
| Q5 credits | builder | Whether the hackathon credits covered the runs | docs/build-log.md |
| Q5 most valuable capability | user | The Nebius capability that mattered most and how | The user |
| Q6 recommendation | user | My rating and the reasons | The user |
| Q7 rating and comparison | user | My rating and the comparison with previous environments | The user |
| Q7 measured Nebius behavior | builder | Latency, time to first token, outcome, retry and wait figures across the three tiers | results/TIMESTAMP-nebius.json, results/TIMESTAMP-nvidia.json, results/TIMESTAMP-local.json |
| Q7 NIM attempt | builder | Outcome and decision of the NIM attempt | docs/nim-local-attempt.md |
| Q8 features wanted | user | The features or improvements I wanted | The user |
| Q9 hopes | user | What I hope the Nemotron team does next | The user |

This section is deleted when the form is filled.
