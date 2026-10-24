This file records every endpoint and model ID the configuration may use (REQ-018). Line formats the validator parses: `## <tier>` for local, cloudgpu, nvidia and nebius; `Verified: YYYY-MM-DD`; `- model: <id>`; `Status: <text>`. Other lines are prose.

## local

Base URL: http://localhost:8000/v1 (NVIDIA NIM container agent-joules-nim, image nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1:1.8.5, started by the AJ-103 attempt recorded in docs/nim-local-attempt.md)
Verified: 2026-10-24
Command: curl -sS http://127.0.0.1:8000/v1/models (no key: the local runtime has none)
Total models returned: 1
- model: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Runtime facts (measured, AJ-103): NIM 1.8.5, profile vllm-bf16-tp1-pp1 (precision BF16), NIM_MAX_MODEL_LEN 16384 (the models response reports max_model_len 16384), GPU memory 13449 MiB of 16384 MiB while serving.
Chosen: Planner: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Chosen: Researcher: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Chosen: Writer: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Chosen: Reviewer: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Chosen: Judge: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning (Judge:Tier is nvidia; the judge model is listed under ## nvidia, not on this endpoint)
Fallback record (not the chosen runtime; REQ-020): Ollama 0.40.10 at http://localhost:11434/v1, GET /v1/models on 2026-10-24 listed one tag, nemotron-3-nano:4b (GET /api/tags: size 2837597147 bytes, quantization Q4_K_M, parameter size 4.0B, family nemotron_h, measured); the user's 2026-10-24 choice of this tag over nemotron-3-nano:30b (partial offload on the 16 GB GPU) applies only if the local tier falls back to Ollama; AJ-107 completes the local record.
- model: nemotron-3-nano:4b

## cloudgpu

Status: pending (verified in M5, AJ-126)

## nvidia

Base URL: https://integrate.api.nvidia.com/v1
Verified: 2026-10-24
Command: curl -sS -H "Authorization: Bearer ****" https://integrate.api.nvidia.com/v1/models
Total models returned: 85 (every Nemotron and Qwen entry is listed below; the other 65 entries are truncated; no Qwen entry was returned)
- model: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning
- model: nvidia/nemotron-3-super-120b-a12b
- model: nvidia/nemotron-3-ultra-550b-a55b
- model: nvidia/nemotron-3.5-bolt-e4b
- model: nvidia/nemotron-3.5-lightning-30b-a3b
- model: nvidia/nemotron-3.5-super-vl-120b-a12b
- model: nvidia/nemotron-nano-3-30b-a3b
- model: nvidia/llama-3.1-nemotron-51b-instruct
- model: nvidia/llama-3.1-nemotron-70b-instruct
- model: nvidia/llama-3.1-nemotron-safety-guard-8b-v3
- model: nvidia/llama-3.1-nemotron-ultra-253b-v1
- model: nvidia/llama-nemotron-embed-vl-1b-v2
- model: nvidia/nemotron-3-embed-1b
- model: nvidia/nemotron-3.5-content-safety
- model: nvidia/nemotron-3.5-embed-1b
- model: nvidia/nemotron-3.5-embed-8b
- model: nvidia/nemotron-4-340b-instruct
- model: nvidia/nemotron-4-340b-reward
- model: nvidia/nemotron-parse
- model: nvidia/nemotron-parse-2.0
Chosen: Planner: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning
Chosen: Researcher: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning
Chosen: Writer: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning
Chosen: Reviewer: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning
Chosen: Judge: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning (Judge:Tier is nvidia)
Judge: self-judged (the judge model ID equals the agents' model ID on this tier; REQ-010 disclosure)
Chat check (2026-10-24, measured): one buffered request to nvidia/nemotron-3-nano-omni-30b-a3b-reasoning with max_tokens 8 and the system message /no_think returned HTTP 200 in 1338 ms with usage prompt_tokens 23, completion_tokens 3, total_tokens 26 and the content "OK".
Note: nvidia/nemotron-nano-3-30b-a3b is listed but answered HTTP 404 "Function ... Not found for account" to every chat request from this account at the M3 exit (2026-10-24); user decision 2 chose the omni-reasoning sibling.

## nebius

Base URL: https://api.tokenfactory.nebius.com/v1 (prompt section 12 step 5; confirmed by the listing call below)
Verified: 2026-10-24
Command: curl -sS -H "Authorization: Bearer ****" https://api.tokenfactory.nebius.com/v1/models
Onboarding started: 2026-10-24T17:15:52Z
Onboarding ended: 2026-10-24T17:21:18Z
First attempt at 17:15:52Z: HTTP 401 (the build session held a stale copy of NEBIUS_API_KEY after a key rotation; the user-scope value answered HTTP 200 at 17:21:07Z; no key value appears anywhere).
Total models returned: 30 (every Nemotron and Qwen entry is listed below; the other 20 entries are truncated)
- model: Qwen/Qwen3-235B-A22B-Instruct-2507
- model: Qwen/Qwen3-30B-A3B-Instruct-2507
- model: Qwen/Qwen3-Embedding-8B
- model: Qwen/Qwen3.5-397B-A17B
- model: Qwen/Qwen3.8-27B
- model: nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B
- model: nvidia/Nemotron-3-Ultra-550b-a55b
- model: nvidia/Nemotron-3.5-Super-VL
- model: nvidia/Nemotron-3_5-Lightning
- model: nvidia/nemotron-3-super-120b-a12b
Chosen: Planner: nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B
Chosen: Researcher: nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B
Chosen: Writer: nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B
Chosen: Reviewer: nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B
Chosen: Judge: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning (Judge:Tier is nvidia; the judge model is listed under ## nvidia, not on this endpoint)
Model choice (AJ-101 criterion 2): the nvidia model nvidia/nemotron-3-nano-omni-30b-a3b-reasoning is not listed here, so the smallest listed Nemotron 3 model with a readable size, nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B (30B-A3B), is chosen; nvidia/Nemotron-3-Ultra-550b-a55b and nvidia/nemotron-3-super-120b-a12b are larger, and Nemotron-3.5-Super-VL and Nemotron-3_5-Lightning are 3.5 models. The judge runs on the nvidia tier (Judge:Tier nvidia); the models differ between the tiers and the report discloses it (REQ-012).
Chat check (2026-10-24, measured): one buffered request to nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B with max_tokens 8 and the system message /no_think returned HTTP 200 in 783 ms with usage prompt_tokens 26, completion_tokens 8, total_tokens 34, finish_reason length, empty content and 30 characters of reasoning: the /no_think prefix does not disable reasoning on this endpoint; AJ-108 verifies the reasoning toggle before any published run.
