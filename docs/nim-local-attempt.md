# NIM-local attempt (REQ-019)

Verified: 2026-10-24

Started: 2026-10-24T17:19:23Z
Ended: 2026-10-24T17:51:29Z

Elapsed: 32 minutes 6 seconds (measured from the two stamps). The box is 90 minutes. Host: Windows 11, Docker Desktop 29.6.1 (server 29.6.1, WSL2), NVIDIA GeForce RTX 3080 Ti Laptop GPU, 16384 MiB, driver 616.92, compute capability 8.6. `<scratch>` below stands for a scratch directory outside the repository. All times are UTC. Every number is measured unless marked otherwise.

## Commands

Preconditions (17:19:00Z to 17:19:17Z):

- 17:19:00Z `echo ${#NGC_API_KEY}` printed 70.
- 17:19:00Z `docker version`: client 29.6.1, server 29.6.1.
- 17:19:00Z `docker info --format '{{.Runtimes}} {{.OperatingSystem}}'`: runtimes io.containerd.runc.v2, nvidia, runc; operating system Docker Desktop.
- 17:19:00Z `df -h /c | tail -1`: size 1.9T, used 1.7T, available 190G.
- 17:19:00Z `nvidia-smi --query-gpu=name,driver_version,memory.used,memory.total --format=csv,noheader`: NVIDIA GeForce RTX 3080 Ti Laptop GPU, 616.92, 84 MiB, 16384 MiB.
- 17:19:00Z `docker run --rm --gpus all ubuntu nvidia-smi --query-gpu=name,memory.total --format=csv,noheader`: exit 0, output NVIDIA GeForce RTX 3080 Ti Laptop GPU, 16384 MiB. The GPU is visible to containers.

Catalog search and pull:

- 17:19:23Z `curl -sS 'https://api.ngc.nvidia.com/v2/search/catalog/resources/CONTAINER?q=%7B%22query%22%3A%22nemotron%22%2C%22pageSize%22%3A50%7D'`: HTTP 200 (first query shape worked, 50 distinct container entries; filtered with jq on `.results[].resources[]`).
- 17:19:39Z `curl -sS https://api.ngc.nvidia.com/v2/org/nim/team/nvidia/repos/<name>` and `.../repos/<name>/images` for the two 4B-class candidates: HTTP 401 Not Authenticated.
- 17:19:39Z WebFetch of https://docs.nvidia.com/nim/large-language-models/latest/supported-models.html returned empty content.
- 17:19:52Z `curl -sSL https://docs.nvidia.com/nim/large-language-models/latest/supported-models.html`: HTTP 200, 100 bytes, a meta-refresh redirect to reference/support-matrix.html.
- 17:20:26Z `curl -sSL https://docs.nvidia.com/nim/large-language-models/latest/reference/support-matrix.html`: HTTP 200, 508033 bytes.
- 17:20:50Z `curl -sSL https://build.nvidia.com/nvidia/nemotron-3-5-bolt-e4b` and `.../llama3_1-nemotron-nano-4b-v1_1`: HTTP 200, about 70 KB each; the static HTML holds no `docker run` command.
- 17:21:44Z `export DOCKER_CONFIG=<scratch>/dockercfg` (a fresh directory), then `printf '%s' "$NGC_API_KEY" | docker login nvcr.io --username '$oauthtoken' --password-stdin`: Login Succeeded.
- 17:21:59Z `docker manifest inspect nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1:1.8.5` and `docker buildx imagetools inspect` of the same reference: digests below.
- 17:22:05Z `docker pull nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1:1.8.5 > pull.log 2>&1 &` (detached, same DOCKER_CONFIG, polled with short checks of the log and an exit-code marker).
- 17:28:34Z the pull ended, exit code 0.
- 17:28:43Z `docker logout nvcr.io` (same DOCKER_CONFIG): Removing login credentials for nvcr.io.
- 17:28:43Z `unset DOCKER_CONFIG; rm -rf <scratch>/dockercfg`: the directory no longer exists (checked with `ls -d`).
- 17:28:43Z `docker image inspect --format '{{.Size}}' nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1:1.8.5`: 9641801263.

Run, attempt 1 (default profile), 17:28:52Z:

```
docker run -d --name agent-joules-nim --gpus all --shm-size=16GB -e NGC_API_KEY -e NIM_CACHE_PATH=/opt/nim/.cache -v "<scratch>/nim-cache:/opt/nim/.cache" -p 8000:8000 nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1:1.8.5
```

- 17:39:50Z `docker inspect agent-joules-nim`: status exited, exit code 137, OOMKilled true. `docker rm agent-joules-nim`.

Run, attempt 2 (profile named in the logs, BF16 vllm), 17:39:54Z:

```
docker run -d --name agent-joules-nim --gpus all --shm-size=16GB -e NGC_API_KEY -e NIM_CACHE_PATH=/opt/nim/.cache -e NIM_MODEL_PROFILE=4f904d571fe60ff24695b5ee2aa42da58cb460787a968f1e8a09f5a7e862728d -v "<scratch>/nim-cache:/opt/nim/.cache" -p 8000:8000 nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1:1.8.5
```

- 17:43:22Z container exited with code 1 (OOMKilled false). `docker rm agent-joules-nim`.

Run, attempt 3, 17:43:41Z: the attempt 2 command with `-e NIM_MAX_MODEL_LEN=32768` added.

- 17:47:40Z container exited with code 1 (OOMKilled false). `docker rm agent-joules-nim`.

Run, attempt 4, 17:48:02Z: the attempt 2 command with `-e NIM_MAX_MODEL_LEN=16384` added.

```
docker run -d --name agent-joules-nim --gpus all --shm-size=16GB -e NGC_API_KEY -e NIM_CACHE_PATH=/opt/nim/.cache -e NIM_MODEL_PROFILE=4f904d571fe60ff24695b5ee2aa42da58cb460787a968f1e8a09f5a7e862728d -e NIM_MAX_MODEL_LEN=16384 -v "<scratch>/nim-cache:/opt/nim/.cache" -p 8000:8000 nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1:1.8.5
```

- 17:48:02Z to 17:51:29Z `docker logs -f agent-joules-nim > nim4.log 2>&1 &` (detached) and polling `curl -sS --max-time 5 http://127.0.0.1:8000/v1/models`.
- 17:51:29Z `curl -sS --max-time 5 http://127.0.0.1:8000/v1/models`: HTTP 200 with a model id (see Outcome).
- 17:51:40Z `curl -sS http://127.0.0.1:8000/v1/models` again: HTTP 200; `nvidia-smi --query-gpu=memory.used,memory.total --format=csv`; `docker stats --no-stream`; one POST to `http://127.0.0.1:8000/v1/chat/completions` with max_tokens 16.
- The container `agent-joules-nim` was left running.

## Image

Search sources (URL, time):

1. NGC public catalog search, https://api.ngc.nvidia.com/v2/search/catalog/resources/CONTAINER?q={"query":"nemotron","pageSize":50} (URL-encoded as in Commands), 17:19:23Z. Returned 50 distinct container entries; 48 are in the `nim/nvidia` namespace.
2. NIM supported-models page, https://docs.nvidia.com/nim/large-language-models/latest/supported-models.html, 17:19:52Z; it redirects to https://docs.nvidia.com/nim/large-language-models/latest/reference/support-matrix.html, read at 17:20:26Z.
3. build.nvidia.com model pages, https://build.nvidia.com/nvidia/nemotron-3-5-bolt-e4b and https://build.nvidia.com/nvidia/llama3_1-nemotron-nano-4b-v1_1, 17:20:50Z. No deploy command in the static HTML.

Does a 4B-class Nemotron NIM exist: yes, one that is not Nemotron 3 Nano.

- Nemotron 3 Nano 4B: no NIM found. The catalog entries `nemotron-3-nano` and `nemotron-3-nano-pb6` are described as Nemotron-3-Nano-30B-A3B. In the support matrix the 17 `nemotron-3-nano` rows list data-center GPUs only (A100, A10G, H100, H200, L40S, GH200, GB10, GB200, B200 and similar) with tensor parallelism 1, 2, 4 or 8; the one row that lists the A10G (compute capability 8.6) is BF16 with tensor parallelism 4. No row lists a laptop GPU, and the model has 30B parameters, which in BF16 does not fit one 16 GB GPU (inferred).
- Chosen: catalog entry `llama3.1-nemotron-nano-4b-v1.1` (display name Llama-3.1-Nemotron-Nano-4B-v1.1, model served as `nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1`), a NIM LLM 1.x container. Catalog attributes: latestTag 1.8.5, size 9641725404, last pushed 2025-06-25T23:25:35Z. It is listed under "1.x NIM LLM Models" in the support matrix.
- Not chosen: `nemotron-3.5-bolt-e4b-4gb` (catalog created 2026-10-23T02:46:14Z, tag `latest`, catalog size 13819790036, BF16, vision-language model). The support matrix lists verified GPUs GB10, H100-80GB-HBM3, RTX-PRO-4500-Blackwell and RTX-PRO-6000-Blackwell only; this 8.6 GPU is not listed. Not pulled.
- Not chosen by size or precision: `nvidia-nemotron-nano-9b-v2` and `llama-3.1-nemotron-nano-8b-v1`. Inferred: 8B-class BF16 weights alone are about 16 GB and do not fit in 16 GB with a KV cache; 9B is larger. FP8 profiles need compute capability 8.9 and do not run on 8.6 (inferred from NVIDIA hardware documentation, not tested).

Image used:

- Image path: `nvcr.io/nim/nvidia/llama3.1-nemotron-nano-4b-v1.1`
- Tag: `1.8.5`
- Digest (manifest list, from `docker manifest inspect`/`imagetools inspect` and printed by `docker pull`): `sha256:5aad99c50b34b076ab6769fb9ef641c6b4e1216fe6abac83f09ac14f8d378230`
- linux/amd64 manifest digest: `sha256:280f5e92bc4a32d0f24175c3b9d0cce7479b2d3edcadc6102c27935b50280bca`
- Size: 9641801263 bytes (`docker image inspect`, measured). Catalog attribute size: 9641725404.
- Pull duration: 6 minutes 29 seconds (17:22:05Z to 17:28:34Z, measured). Free space on C: was 190G before the pull (17:19:00Z) and 165G after it (17:28:43Z); 122G at 17:51:40Z with the model cache (8.5G) and the running container.

## Outcome

Result: the first successful `GET http://127.0.0.1:8000/v1/models` came on the fourth container start, at 17:51:29Z, 32 minutes 6 seconds after Started. The log line `Application startup complete.` is stamped 17:51:27Z. The response:

```
HTTP 200
{"object":"list","data":[{"id":"nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1","object":"model","created":1792864290,"owned_by":"system","root":"nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1","parent":null,"max_model_len":16384, ...}]}
```

(the `permission` array is omitted here). Model id: `nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1`; `max_model_len` 16384.

Start durations (measured): attempt 1 from 17:28:52Z until killed at 17:39:37Z (10 minutes 45 seconds); attempt 2 from 17:39:54Z to exit at about 17:42:44Z; attempt 3 from 17:43:41Z to exit at about 17:46:23Z; attempt 4 from 17:48:02Z to the models response at 17:51:29Z (3 minutes 27 seconds, with the model files already in the cache). The model files were downloaded into the cache during attempt 1 (cache 8.5G after 17:33:35Z).

Errors, verbatim:

- Attempt 1 (profile selected by the NIM: `ac34857f8dcbd174ad524974248f2faf271bd2a0355643b2cf1490d0fe7787c2`, `tensorrt_llm-trtllm_buildable-bf16-tp1-pp1`; the log named a second valid profile `4f904d571fe60ff24695b5ee2aa42da58cb460787a968f1e8a09f5a7e862728d`, `vllm-bf16-tp1-pp1`). The last progress line was `Total time of reading and converting rank0 checkpoint: 4.68 s` (17:37:14Z), then the process was killed during the engine build (`docker stats` showed 4.843GiB / 7.756GiB at 17:37:36Z; the cause is inferred to be the container memory limit, supported by OOMKilled true):

```
/opt/nim/start_server.sh: line 90:    60 Killed                  python3 -m nim_llm_sdk.entrypoints.launch
```

`docker inspect`: exit 137, OOMKilled true. The 7.756 GiB figure is the memory limit `docker stats` reports for the container, host RAM and not GPU memory (that it comes from the WSL2 VM is inferred). GPU memory used stayed at 84 MiB during this attempt (no GPU allocation was reached).

- Attempt 2 (vllm-bf16 profile, default maximum length 131072; GPU memory used 8973 MiB while loading):

```
ERROR 2026-10-24 17:42:36.656 async_trtllm_engine_factory.py:62] The model's max seq len (131072) is larger than the maximum number of tokens that can be stored in KV cache (38336). Try increasing `gpu_memory_utilization` or decreasing `max_model_len` when initializing the engine.
RuntimeError: Engine process failed to start
```

- Attempt 3 (`NIM_MAX_MODEL_LEN=32768`):

```
ERROR 2026-10-24 17:46:15.217 async_trtllm_engine_factory.py:62] The model's max seq len (32768) is larger than the maximum number of tokens that can be stored in KV cache (24656). Try increasing `gpu_memory_utilization` or decreasing `max_model_len` when initializing the engine.
RuntimeError: Engine process failed to start
```

The KV cache capacity differed between attempts 2 and 3 (38336 and 24656 tokens) with the same profile, memory settings and GPU; the cause was not investigated.

- Attempt 4 (`NIM_MAX_MODEL_LEN=16384`): started. Log lines: `# GPU blocks: 2089, # CPU blocks: 2048`; `Maximum concurrency for 16384 tokens per request: 2.04x`; `Uvicorn running on http://0.0.0.0:8000`.

GPU memory, `nvidia-smi --query-gpu=memory.used,memory.total --format=csv` (measured):

- Before the first start (17:28:43Z): 84 MiB, 16384 MiB
- During the load, attempts 2 to 4 (17:42:09Z, 17:45:24Z, 17:49:39Z): 8973 MiB, 16384 MiB
- At the end, model serving, container still running: 13449 MiB, 16384 MiB at 17:51:40Z; 13451 MiB, 16384 MiB at about 17:52Z (unstamped follow-up reading)

The container's host memory at 17:51:40Z was 6.225GiB / 7.756GiB (`docker stats`, not GPU).

One chat completion at 17:51:40Z (not part of the box criterion): the request asked for the single word "ready" with max_tokens 16, temperature 0; the reply content began `acks` followed by a note, 16 completion tokens (26 prompt tokens). The model is a reasoning model and no system prompt was set; the output is recorded as observed, not judged.

Secrets: the key reached docker only through `--password-stdin` for the login and `-e NGC_API_KEY` (name only) for the container. The temporary DOCKER_CONFIG was deleted and `docker logout nvcr.io` ran before any container started. `grep -c -F "$NGC_API_KEY"` over the pull log and the four container logs printed 0 for each.

## Decision

Reasoning: the container served `GET /v1/models` with HTTP 200 inside the box (32 minutes 6 seconds of 90). The working configuration is the BF16 vllm profile with `NIM_MAX_MODEL_LEN=16384`. The default profile did not start (host memory kill during engine build, exit 137), and the default maximum length of 131072 does not fit the KV cache on this 16 GB GPU. Limits that a later story must accept: the served model is Llama-3.1-Nemotron-Nano-4B-v1.1 (no Nemotron 3 Nano 4B NIM exists), the context is 16384 tokens, the NIM version is 1.8.5 (a 1.x container, last pushed 2025-06-25), and the support matrix does not list this GPU, so the working state is a measured result of one successful start (attempt 4) and has not been repeated. The container `agent-joules-nim` is left running for a later measurement story. The image was not removed (free space on C: 122G, above 30 GB).

Local runtime: nim
