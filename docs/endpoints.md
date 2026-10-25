This file records every endpoint and model ID the configuration may use (REQ-018). Line formats the validator parses: `## <tier>` for local, cloudgpu, nvidia and nebius; `Verified: YYYY-MM-DD`; `- model: <id>`; `Status: <text>`. Other lines are prose.

## local

Base URL: http://localhost:8000/v1 (NVIDIA NIM container agent-joules-nim, image nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1:1.8.5, started by the AJ-103 attempt recorded in docs/nim-local-attempt.md)
Verified: 2026-10-24
Command: curl -sS http://127.0.0.1:8000/v1/models (no key: the local runtime has none)
Total models returned: 1
- model: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Runtime facts (measured, AJ-103): NIM 1.8.5, profile vllm-bf16-tp1-pp1 (precision BF16), NIM_MAX_MODEL_LEN 16384 (the models response reports max_model_len 16384), GPU memory 13449 MiB of 16384 MiB while serving.
Re-verified 2026-10-25 (AJ-107, measured): GET /v1/models lists one model, nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 (max_model_len 16384); GET /v1/version {"release":"1.8.5","api":"3.1.0"}; the container agent-joules-nim (NIM image llama3.1-nemotron-nano-4b-v1.1:1.8.5) has run since 2026-10-24T20:19:44Z; validate --config exit 0 (Validated 4 model IDs); run --tier local --dry-run exit 0 (local http://localhost:8000/v1 nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1 OK). Criteria 1 and 2 of AJ-107 (the Ollama pull, show, ps and the 30b model choice rule) and the Qwen baseline entry do not apply under the nim decision; the live metered run of 2026-10-25 02:43Z (AJ-108, criterion 5) served the model at gpuOffloadPercent 100 and precision BF16; that 100 is a constant set by construction in NimRuntime.cs (the NIM serves the model fully on the GPU), not an observation.
Chosen: Planner: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Chosen: Researcher: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Chosen: Writer: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Chosen: Reviewer: nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1
Chosen: Judge: nvidia/nemotron-3-nano-omni-30b-a3b-reasoning (Judge:Tier is nvidia; the judge model is listed under ## nvidia, not on this endpoint)
Streaming usage: reported
Streaming tokens: input 26 measured, output 10 measured
Reasoning toggle: system message: detailed thinking off
Reasoning result: off
Reasoning evidence: probes of 2026-10-24 20:31Z (measured): the buffered baseline answered "4" in 2 completion tokens with no reasoning member and no think tag; a system message "detailed thinking on" produced a think block (64 tokens, cut); "detailed thinking off" gave "4" in 2 tokens; chat_template_kwargs enable_thinking false was accepted and changed nothing; a system message /no_think gave "4" in 2 tokens with no reasoning member and no think tag (re-measured 2026-10-25 03:16Z with the literal message; the 2026-10-24 probe had sent a shell-rewritten path instead of /no_think, so its 64-token result is withdrawn). The streaming call with stream_options include_usage returned 14 SSE data lines including [DONE] (13 JSON chunks) and a final chunk with empty choices carrying usage (prompt 26, completion 10 for the 16-token "OK" probe; the two Streaming tokens values above are the probe's prompt count and its completion count, see the build log for the metered run's labels).
Fallback record (not the chosen runtime; REQ-020): Ollama 0.40.10 at http://localhost:11434/v1, GET /v1/models on 2026-10-24 listed one tag, nemotron-3-nano:4b (GET /api/tags: size 2837597147 bytes, quantization Q4_K_M, parameter size 4.0B, family nemotron_h, measured); the user's 2026-10-24 choice of this tag over nemotron-3-nano:30b (partial offload on the 16 GB GPU) applies only if the local tier falls back to Ollama; AJ-107 completes the local record.
- model: nemotron-3-nano:4b
Fallback facts (measured 2026-10-25, AJ-107, Ollama 0.40.10): the OpenAI route reports streaming usage (a final chunk carries prompt_tokens and completion_tokens); it accepts max_completion_tokens with HTTP 200 but does not apply the cap (completion_tokens 30 for a cap of 8) while max_tokens is applied (completion_tokens 8, finish_reason length), so a fallback run needs Tiers:local:MaxTokensField max_tokens like the NIM; after a warm-up with keep_alive 30m a /v1 chat completion renewed the 30-minute window (expires_at moved to the chat time plus 30 min) with OLLAMA_KEEP_ALIVE unset; the live EDGE-009 checks of AJ-107 criterion 4 ran against this runtime through the configuration override (see docs/build-log.md, AJ-107).

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
Chat check (2026-10-24, measured): one buffered request to nvidia/nemotron-3-nano-omni-30b-a3b-reasoning with max_tokens 8 and the system message /no_think returned HTTP 200 in 1338 ms with usage prompt_tokens 23, completion_tokens 3, total_tokens 26 and the content "OK". Re-measured 2026-10-25 03:16Z (an AJ-108 gate finding: the 2026-10-24 probe shell may have rewritten /no_think as a path; no body was saved): with the literal system message /no_think and max_tokens 64 the answer was "4" in 3 completion tokens with a 2-character reasoning_content member; the check stands.
Note: nvidia/nemotron-nano-3-30b-a3b is listed but answered HTTP 404 "Function ... Not found for account" to every chat request from this account at the M3 exit (2026-10-24); user decision 2 chose the omni-reasoning sibling.
Streaming usage: reported
Streaming tokens: input 23 measured, output 43 measured
Reasoning toggle: {"chat_template_kwargs":{"enable_thinking":false}}
Reasoning result: off
Reasoning evidence: probes of 2026-10-24 20:31Z to 20:33Z (measured): the buffered baseline answered "4" in 36 completion tokens with a reasoning_content member of 115 characters; with chat_template_kwargs enable_thinking false the answer was "\n4" in 3 tokens and a null reasoning_content member (the first attempt met HTTP 503 ResourceExhausted and the retry after 10 s succeeded); a system message /no_think gave "4" in 3 tokens but a 2-character reasoning_content member stayed (re-measured 2026-10-25 03:16Z with the literal message, the same numbers; the 2026-10-24 probe had sent a shell-rewritten path instead of /no_think), and the M3-exit prefix was replaced by the body toggle, one mechanism per tier; enable_thinking true gave 32 tokens with reasoning. The streaming probes of 2026-10-24 20:31Z, 20:36Z and 22:02Z each returned HTTP 200 with a single ResourceExhausted error chunk (the saturated free-tier worker; the AJ-104 handler now ends such a step as a provider error and retries 503 and 500 ResourceExhausted); the retry of 2026-10-25 02:28Z (the same 16-token "OK" probe body without the toggle) returned 45 SSE data lines including [DONE] (44 JSON chunks), reasoning_content deltas in 39 of them, content "OK", finish_reason stop and a final chunk carrying usage (prompt 23, completion 43, the reasoning tokens counted in the completion), so the usage is reported once the worker has capacity.

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
Chat check (2026-10-24, measured): one buffered request to nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B with max_tokens 8 and the system message /no_think returned HTTP 200 in 783 ms with usage prompt_tokens 26, completion_tokens 8, total_tokens 34, finish_reason length, empty content and 30 characters of reasoning: the /no_think prefix does not disable reasoning on this endpoint; AJ-108 verifies the reasoning toggle before any published run. Re-measured 2026-10-25 03:16Z (the same gate finding): with the literal system message /no_think and max_tokens 64 the answer was "\n4" in 44 completion tokens with 141 characters of reasoning; the conclusion stands.
Streaming usage: reported
Streaming tokens: input 23 measured, output 16 measured
Reasoning toggle: {"chat_template_kwargs":{"enable_thinking":false}}
Reasoning result: off
Reasoning evidence: probes of 2026-10-24 20:31Z to 20:33Z (measured): the buffered baseline answered "\n4" in 42 completion tokens with a reasoning member of 138 characters; with chat_template_kwargs enable_thinking false the answer was "4" in 2 tokens and a null reasoning member; a system message /no_think left reasoning present (re-measured 2026-10-25 03:16Z with the literal message: "\n4" in 44 tokens with 141 characters of reasoning; the 2026-10-24 probe had sent a shell-rewritten path instead of /no_think); enable_thinking true gave 47 tokens with reasoning. The streaming call with stream_options include_usage returned 18 SSE data lines including [DONE] (17 JSON chunks), a reasoning member in 15 deltas, empty content at the 16-token cap, and a final chunk carrying usage (prompt 23, completion 16).
