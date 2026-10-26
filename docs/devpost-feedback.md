# Agent Joules: Nemotron and Nebius feedback answers

This file holds my answers to the Nemotron and Nebius feedback questionnaire on the Devpost submission form. The builder-owned passages were filled on 2026-10-26 from the named results files and documents, and each filled passage names its source. The seven user placeholders remain for me to fill with my own ratings and opinions; they are listed in docs/devpost-checklist.md under `Feedback form, user fills`. The Fill checklist below keeps the seven user rows.

## Q1. Which model(s) did you use, and why did you choose that size/variant? (e.g., Nemotron-4 340B, Nemotron-Mini, etc.)

This answer was drafted before the build; the model sentences were updated on 2026-10-26 from the run files.

The primary hosted model is Nemotron 3 Nano 30B-A3B. I use it on Nebius Token Factory, which lists it as nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B, so I did not need Nemotron 3 Super 120B-A12B there. On build.nvidia.com the model I used is nvidia/nemotron-3-nano-omni-30b-a3b-reasoning.

I chose this size for two reasons. First, it is a mixture-of-experts model that activates only a small share of its parameters per token (the A3B in the name). My design assumption is that this keeps latency and cost per agent step low, which is what the project measures. I treat that as an assumption to test, not as a result. Second, the same family can run on my laptop, although the laptop served the 4B-class model nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1, so the four tiers did not run one model and none of the figures below is a like-for-like comparison.

The laptop has an RTX 3080 Ti Laptop GPU with 16 GB of memory. The local tier ran an NVIDIA NIM container (version 1.8.5) with the 4B-class model nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 in BF16 and a GPU offload of 100 percent (results/20261025T131930Z-local.json, measured), and Ollama stayed the fallback. Research on 2026-10-17 found the 30B-A3B tag marginal for 16 GB, so part of it may be offloaded to the CPU, and the report records the offload percentage.

I do not target the larger variants, Nemotron 3 Super 120B-A12B and Nemotron 3 Ultra 550B-A55B, because the local tier cannot host them. Super was only the fallback on Nebius and was not used. A non-Nemotron model, Qwen3 8B, was pulled for a local baseline, and no baseline run was made. A hosted Nemotron model also acts as the quality judge, and the report discloses it when the judge and the step model are the same.

The cloud tier ran nvidia/nemotron-3-nano in FP8 on one H100 80 GB of a Nebius AI Cloud virtual machine, a different model from the laptop's, so the laptop and the datacenter energy figures describe two models at two precisions, each in its own row.

Exact model IDs are verified with GET /v1/models on each endpoint and recorded in docs/endpoints.md after the build.

## Q2. How would you rate Nemotron's output quality for your use case? (1 = Poor, 10 = Excellent) Please share what worked well or fell short, e.g., reasoning, instruction-following, domain knowledge, multilingual support.

Quality is scored by a judge model on an integer scale against a rubric committed at config/judge-rubric.md, labelled model-judged and reported per tier.

[[FILL: my rating from 1 (Poor) to 10 (Excellent), and what worked well or fell short in reasoning, instruction-following, domain knowledge and multilingual support, from my own reading of the outputs | source: the user | owner: user]]

The judge model in all four run files is nvidia/nemotron-3-nano-omni-30b-a3b-reasoning on the nvidia tier, and it scores each step from 1 to 5 (model-judged). Every tier had 36 of 36 steps with outcome ok. Local, running nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 (results/20261025T131930Z-local.json): mean quality 3.72 points over 32 scored steps (12 scored 3, 17 scored 4, 3 scored 5; model-judged), 4 steps N/A after judge errors, 0 step retries; the judge had 32 ok and 4 error outcomes, 33 retries and 46284 ms of waiting (measured). Nebius Token Factory, running nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B (results/20261025T133440Z-nebius.json): mean quality 4.09 points over 34 scored steps (31 scored 4, 3 scored 5; model-judged), 2 steps N/A, 0 step retries; the judge had 34 ok and 2 error outcomes, 18 retries and 17695 ms of waiting (measured). build.nvidia.com, running nvidia/nemotron-3-nano-omni-30b-a3b-reasoning (results/20261025T135121Z-nvidia.json): mean quality 4.14 points over 36 scored steps (31 scored 4, 5 scored 5; model-judged), 0 steps N/A, 16 step retries with 14925 ms of waiting (measured); the judge had 36 ok outcomes, 14 retries and 11375 ms of waiting (measured), and this run is self-judged (run.selfJudged true), because the judge model is the step model. Nebius AI Cloud, running nvidia/nemotron-3-nano (results/20261026T124721Z-cloudgpu.json): mean quality 4.09 points over 33 scored steps (1 scored 3, 28 scored 4, 4 scored 5; model-judged), 3 steps N/A, 0 step retries; the judge had 33 ok and 3 error outcomes, 35 retries and 50652 ms of waiting (measured). Each mean belongs to its own tier and model, on this workload.

## Q3. Did you fine-tune, prompt-engineer, or use Nemotron out of the box? What was your approach?

This answer is a design decision, written before the build.

I use Nemotron out of the box. I do not fine-tune: training or fine-tuning any model is out of scope for this project by design, because the project measures how a workflow behaves on models as they are served.

My only lever is prompt engineering. Each agent role gets one instruction. A judge rubric is committed at config/judge-rubric.md so that anyone can read how quality is scored. Temperature is fixed, and the same reasoning mode is used on every tier, set through the mechanism each endpoint documents and recorded per tier in the results as reasoningMode. A seed is passed when the endpoint supports one. Every call also uses the maximum output length set in the committed task file. Holding these constant is what lets results compare across tiers, and it means a stranger can rerun the same prompts against the same model IDs. If an endpoint cannot set the reasoning mode, the report header and README say so.

## Q4. How did Nemotron's performance compare to other models you've used for similar tasks?

Each sentence below is scoped to this workload on the tier it names, and each cost stays in its own currency.

No Qwen3 8B baseline run was made.

Cost and latency per tier come from results/20261025T131930Z-local.json, results/20261025T133440Z-nebius.json, results/20261025T135121Z-nvidia.json and results/20261026T124721Z-cloudgpu.json (fields steps.modelId, steps.latencyMs, steps.ttftMs and totals.costByCurrency), each cost in its own currency. The local tier with nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 cost 0.0028 EUR (estimated), with a median step latency of 12224 ms and a median time to first token of 234 ms (measured). The nebius tier (Nebius Token Factory) with nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B cost 0.0057 USD (estimated), with a median step latency of 1721 ms and a median time to first token of 275 ms (measured). The nvidia tier (build.nvidia.com) with nvidia/nemotron-3-nano-omni-30b-a3b-reasoning has a cost of N/A, because no price is configured for the free endpoint, with a median step latency of 8371 ms and a median time to first token of 353 ms (measured). The cloudgpu tier (Nebius AI Cloud H100, FP8) with nvidia/nemotron-3-nano cost 0.0647 USD (estimated: the hourly price times the step time), with a median step latency of 1142 ms and a median time to first token of 65 ms (measured). The medians are over the 36 steps of each run, taking the mean of the two middle values. The four tiers ran four different models, so each figure belongs to its own tier and model, on this workload.

[[FILL: how Nemotron compared with other models I have used for similar tasks outside this project | source: the user | owner: user]]

## Q5. Which Nebius platform capabilities were most valuable to your project and how? Please include detail on how you leveraged Nebius infrastructure to run Nemotron (e.g., GPU instance types, deployment setup, API access, scaling).

This answer was drafted before the build; the sentences that name what ran were updated on 2026-10-26 from the run files and the documents.

I use two Nebius products: the Token Factory hosted API and one AI Cloud GPU virtual machine. The planned setup is the OpenAI-compatible chat completions API, called from a .NET 10 program through the OpenAI SDK with a custom endpoint. Research on 2026-10-17 found that Token Factory serves Nemotron 3 Super through this API at https://api.tokenfactory.nebius.com/v1 and could not confirm whether Nano or Ultra are offered; the verified listing is below. I verify the model IDs and the base URL myself with GET /v1/models and record them in docs/endpoints.md. The API key is held in an environment variable or in dotnet user-secrets, and it is never committed. The Token Factory runs were to be paid with the hackathon Token Factory credits (whether the credits paid for them is not reported, see below); the AI Cloud VM was paid from the AI Cloud balance (docs/cloudgpu-run.md).

The AI Cloud part is one on-demand H100 80 GB virtual machine (platform gpu-h100-sxm, preset 1gpu-16vcpu-200gb, region eu-north1, image ubuntu24.04-cuda13.0) that I create in the console and delete after the runs, with a hard cap of USD 30 on total spend. A Nemotron 3 Nano 30B-A3B NIM container in FP8 and my meter run on that machine, so the GPU energy of the cloud tier is measured through NVML, not estimated. No autoscaling and no multi-GPU setup. Laptop and cloud numbers are reported in separate rows and never merged.

The hosted API tiers report tokens, latency, time to first token, cost where a price exists, outcome and retry counts. Energy is measured on the laptop GPU and on the AI Cloud GPU, and is N/A for the Token Factory API.

The Nebius Token Factory base URL is https://api.tokenfactory.nebius.com/v1, verified on 2026-10-24 with the listing call GET /v1/models, which returned 30 models (measured; docs/endpoints.md, section nebius). The Nemotron model IDs in that listing are nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B, nvidia/Nemotron-3-Ultra-550b-a55b, nvidia/Nemotron-3.5-Super-VL, nvidia/Nemotron-3_5-Lightning and nvidia/nemotron-3-super-120b-a12b, and the model I used is nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B.

The AI Cloud instance was a plain Nebius AI Cloud virtual machine reached over SSH, platform gpu-h100-sxm with preset 1gpu-16vcpu-200gb in region eu-north1, one NVIDIA H100 80GB HBM3 on driver 580.173.02, serving nvidia/nemotron-3-nano with NIM 2.1.4 in FP8, for 1.509 GPU-hours at USD 4.63 per hour, a whole-run cost of about USD 7 (estimated; docs/cloudgpu-run.md, sections Instance and Cost).

Not reported: the builder cannot see the Nebius billing page. Whether the hackathon Token Factory credits paid for the Token Factory runs is therefore unknown.

[[FILL: which Nebius capability was most valuable to this project and how | source: the user | owner: user]]

## Q6. How likely are you to recommend running Nemotron on Nebius to other developers? (1 = Unlikely, 10 = Highly Likely) Please share why or provide additional feedback.

[[FILL: my likelihood to recommend from 1 (Unlikely) to 10 (Highly Likely), and why | source: the user | owner: user]]

## Q7. How would you rate your experience running Nemotron inference on Nebius compared to previous cloud or local development environments? (1 = Poor, 10 = Excellent) Please share why or provide additional feedback.

Latency and time to first token are measured per step, so the Nebius figures sit next to the same fields from build.nvidia.com and from the local tier.

[[FILL: my rating from 1 (Poor) to 10 (Excellent), and how the experience compared with previous cloud or local environments I have used | source: the user | owner: user]]

These figures are per step, over the 36 steps of each run (medians as the mean of the two middle values; results/20261025T133440Z-nebius.json, results/20261025T135121Z-nvidia.json, results/20261025T131930Z-local.json and results/20261026T124721Z-cloudgpu.json). The nebius tier (Nebius Token Factory) with nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B had a median latency of 1721 ms and a median time to first token of 275 ms, 36 of 36 steps ok, 0 retries and 0 ms of waiting (measured). The nvidia tier (build.nvidia.com) with nvidia/nemotron-3-nano-omni-30b-a3b-reasoning had a median latency of 8371 ms and a median time to first token of 353 ms, 36 of 36 steps ok, 16 retries and 14925 ms of waiting after overload rejections (HTTP 429, 503 or 500 ResourceExhausted; the run file records the counts, not the status; results/20261025T135121Z-nvidia.json, measured). The local tier with nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 had a median latency of 12224 ms and a median time to first token of 234 ms, 36 of 36 steps ok, 0 retries and 0 ms of waiting (measured). The cloudgpu tier (Nebius AI Cloud H100) with nvidia/nemotron-3-nano had a median latency of 1142 ms and a median time to first token of 65 ms, 36 of 36 steps ok, 0 retries and 0 ms of waiting (measured). These are four different models, so each figure belongs to its own tier and model, on this workload.

I gave a NIM container on the local laptop a 90-minute box, and it served GET /v1/models with HTTP 200 after 32 minutes 6 seconds (measured; docs/nim-local-attempt.md, Outcome). The default profile did not start, because the process was killed during the engine build with exit 137 (a host memory kill), and the working configuration is the BF16 vllm profile with NIM_MAX_MODEL_LEN=16384. The decision is `Local runtime: nim`, so the Ollama fallback was not needed (docs/nim-local-attempt.md, Decision); it rests on one successful start that has not been repeated.

## Q8. What additional features or improvements would have made the Nemotron on Nebius experience more effective for your project?

[[FILL: the features or improvements I wanted while building the project | source: the user | owner: user]]

## Q9. What do you most hope to see from the Nemotron team next?

[[FILL: what I most hope to see from the Nemotron team next | source: the user | owner: user]]

## Fill checklist

| Placeholder | Owner | What replaces it | Source artifact |
|---|---|---|---|
| Q2 rating and notes | user | My rating and what worked or fell short, in my words | The user |
| Q4 other models I have used | user | My comparison with other models used for similar tasks | The user |
| Q5 most valuable capability | user | The Nebius capability that mattered most and how | The user |
| Q6 recommendation | user | My rating and the reasons | The user |
| Q7 rating and comparison | user | My rating and the comparison with previous environments | The user |
| Q8 features wanted | user | The features or improvements I wanted | The user |
| Q9 hopes | user | What I hope the Nemotron team does next | The user |

This section is deleted when the form is filled.
