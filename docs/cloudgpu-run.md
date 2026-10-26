# Cloudgpu run record (REQ-031, EDGE-016, EDGE-017)

This document records the cloudgpu tier campaign of 2026-10-26: the virtual machine (VM) on Nebius AI Cloud, the commands as run, the GPU probe, the measured runs, the cost and the teardown. The CLI is the self-contained linux-x64 publish of commit 33597a7 (`--version` printed 1.0.0+33597a73e844d1ac08a389374db794c8cf99c43c, measured on the VM). Every number carries a label: measured (read from a file or a command output), derived (computed here from measured values), console (read by the user on the cloud console or the billing page), estimated (a cost computed from the console price and a duration) or user-confirmed. No key value, host, address or SSH user name appears here; the SSH target is written `$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST` and the loopback interface is written `localhost`. The Teardown section records the user's deletion of the VM (deleted at 13:50Z, confirmed by the user in the chat at 13:51Z), so the contract test for this file passes.

## Instance

VM1 (not used, deleted by the user):
- Platform: gpu-l40s-d; preset: 2gpu-64vcpu-384gb; region: eu-north1; created 2026-10-26T11:16:52Z (console).
- The L40S presets offered in eu-north1 were two-GPU only; the story platform gpu-l40s-a is offered in no region; the fallback presets of the story do not exist.
- Two Start operations ended "Resource exhausted"; no run was made on it. Cost: disk hours only, under USD 0.01 on the billing page (console).
- Decision: the builder stopped and asked the user (AJ-125 step 3); the candidates were the H100 in eu-north1 at USD 4.63 per hour and the RTX PRO 6000 in uk-south2 at USD 1.93 per hour. The H100 was chosen (user-confirmed) because the NIM support matrix lists L40S and H100, the region stays eu-north1 and the energy counter is present.

VM2 (the run):
- Platform: gpu-h100-sxm (NVIDIA H100 NVLink); preset: 1gpu-16vcpu-200gb (the single 1-GPU H100 preset of the console; the L40S presets of the story were unavailable, hence this fallback); region: eu-north1.
- Image: ubuntu24.04-cuda13.0 (console; no cuda12 image was offered, the NIM ships its own CUDA).
- Boot disk: 160 GiB SSD (console); block device 171798691840 bytes, df 154G (measured).
- Public IP: dynamic (console; the address is not recorded here).
- preemptible: off (user-confirmed)
- Hourly price: USD 4.63 per hour (console: compute USD 4.50 plus storage USD 0.13).
- Boot: 2026-10-26T12:19:14Z (uptime, measured). Handover stamp: 2026-10-26T12:19:28Z (the user's "running" reply, the start of the box).
- Box: 4 hours, ending 2026-10-26T16:19:28Z; stop line USD 25 (config: TimeBoxHours 4, CostStopUsd 25).
- GPU: NVIDIA H100 80GB HBM3; UUID GPU-3e12a8e8-d957-1269-b802-c39f6a145b5f; driver 580.173.02; power limit 700.00 W (measured).
- Host: 16 vCPU, 196 GiB RAM, Ubuntu 24.04.5 LTS, kernel 6.11.0-1016-nvidia, Docker 29.9.2, NVIDIA Container Toolkit 1.20.1 (all measured; Docker and the toolkit were in the image).
- Delivery choice: the self-contained publish of 33597a7 (ADR-14, 392 files), copied over SSH (scp) with config/, tasks/ and docs/endpoints.md from the working tree, not from HEAD, to keep the billing clock short; the bootstrap script runs in tmux in three passes. Keys reach the VM only as the environment of the tmux session, piped over the SSH session's standard input.
- Model: nvidia/nemotron-3-nano served by NIM 2.1.4 in FP8, profile vllm-fp8-tp1-pp1-34.0, on localhost of the VM only.

## Commands

Laptop, before the passes (abridged; run from the staging folder; SSH options abridged as `$SSHO`; the key file is `$CLOUDGPU_SSH_KEY`):

```
dotnet publish src/AgentJoules.Cli -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
ssh -i "$CLOUDGPU_SSH_KEY" $SSHO "$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST" 'mkdir -p ~/agentjoules/docs ~/agentjoules/log'
scp -i "$CLOUDGPU_SSH_KEY" -o IdentitiesOnly=yes -r linux-x64 config tasks cloudgpu-bootstrap.sh "$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST:agentjoules/"
scp -i "$CLOUDGPU_SSH_KEY" -o IdentitiesOnly=yes docs/endpoints.md "$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST:agentjoules/docs/"
```

Pass launch (abridged: the guards are omitted). Both keys travel on standard input and are never an argument, a file or a sudo argument; they exist only in the environment of the tmux session:

```
printf '%s\n%s\n' "$NGC_API_KEY" "$NVIDIA_API_KEY" | ssh -T -i "$CLOUDGPU_SSH_KEY" $SSHO "$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST" \
  'IFS= read -r a && IFS= read -r b; export NGC_API_KEY="$a" NVIDIA_API_KEY="$b"; unset a b; cd ~/agentjoules && exec tmux new-session -d -s aj "bash cloudgpu-bootstrap.sh --to 6"'
```

The three passes (measured): `--to 6`, resumed `--from 2 --to 6` after exit code 25 at phase 2 (the SSH user was added to the docker group, by design); `--from 7` with only `$NVIDIA_API_KEY`; `--only 9` with `$NVIDIA_API_KEY` and `AGENTJOULES__CloudGpu__ObservedCostUsd=0.00`. Phase 1 checked the CLI version and the script hash against expected files, then the GPU name against `NVIDIA H100 80GB HBM3`.

Phase 2 and 3, container runtime and pull (no install command ran: Docker and the toolkit were present). The login uses standard input; the pull job runs with a `DOCKER_CONFIG` directory on tmpfs under /dev/shm and logs out when it ends:

```
docker --version                                  # Docker version 29.9.2
nvidia-ctk --version                              # 1.20.1
docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi -L   # listed the H100
printf '%s' "$NGC_API_KEY" | docker login nvcr.io --username '$oauthtoken' --password-stdin   # Login Succeeded
docker pull nvcr.io/nim/nvidia/nemotron-3-nano:2.1.4   # rc 0, 12:23:30Z to 12:33:46Z
docker logout nvcr.io                             # in the exit trap of the pull job
```

ContainerDigest: sha256:b2cf7068cc4d69adb32e9932ea903633591573b2c5af1947517c28f1c5ea0be8 (the RepoDigests index digest, measured on the VM). The laptop-measured linux/amd64 manifest digest is sha256:2c29e51362b5188e4d12481ecd14e5668d08c036ed9115822212f8867810166c; the two differ as expected for a multi-arch index.

Phase 5, profile and container. The listing offered 4 compatible profiles (vllm-fp8-tp1-pp1-34.0 and a bf16 one, each with a LoRA variant); the fp8 single-GPU profile was selected (measured, profiles list in the bootstrap log). No bf16 profile was used. No context cap was set, the container started at its default length. The pull error and substitution scenarios did not occur. The container binds the loopback interface only (the script passes the numeric loopback address; the command below shows it as `<loopback IP>`):

```
FP8_PROFILE=8c91cce84b9b032ff4af489cb1a20395e223af35623010df9155390ab2284b7a   # vllm-fp8-tp1-pp1-34.0
docker run --rm --gpus all -e NGC_API_KEY nvcr.io/nim/nvidia/nemotron-3-nano:2.1.4 list-model-profiles
docker run -d --name agent-joules-nim-vm --gpus all --shm-size=16GB -e NGC_API_KEY \
  -e NIM_MODEL_PROFILE="$FP8_PROFILE" -v $HOME/nim-cache:/opt/nim/.cache -u 1001 -p <loopback IP>:8000:8000 \
  nvcr.io/nim/nvidia/nemotron-3-nano:2.1.4
```

The key reaches the container by name only (`-e NGC_API_KEY`). Ready about 241 s after the start (measured); the log held the warning "Using KV cache scaling factor 1.0 for fp8_e4m3" (12:37:32), no other KV cache message.

Phase 6, endpoint checks (exit code 0 at 12:39:44Z; figures are in docs/endpoints.md under `## cloudgpu`): GET /v1/models, a streamed completion with usage, enable_thinking false and true, and a request with max_completion_tokens 16. Exclusivity check, taken twice:

```
nvidia-smi --query-compute-apps=pid,process_name --format=csv   # phase 1, 12:22Z: header only
nvidia-smi --query-compute-apps=pid,process_name --format=csv   # phase 6, 12:39:44Z: one line, the NIM engine process
```

The check was not repeated right before the calibration or the run. The supporting facts are the baseline coefficient of variation 0.00029 (measured, run file) and the teardown finding "no GPU process left".

Phase 7 to 9, validation, calibration and the measured run. `$AJ_ENV` stands for the AGENTJOULES__ variables of the run, from three sources. The script's fixed CG_ENV sets AGENTJOULES__Tiers__cloudgpu__Enabled=true and AGENTJOULES__CloudGpu__RequireSshVariables=false (not read from any file). The file log/cloudgpu.env sets CloudGpu__TierStartUtc=2026-10-26T12:19:28+00:00, InstancePreset=1gpu-16vcpu-200gb, Region=eu-north1, HourlyPriceUsd=4.63, HourlyPriceSource=console and Provider=nebius-ai-cloud; ContainerImage comes from the script's image setting and ContainerDigest from log/digest.txt, both as above, unless the file sets them. The third source is the phase-specific overrides: phase 8 (calibrate) also passed AGENTJOULES__Tiers__local__ModelId=nvidia/nemotron-3-nano and AGENTJOULES__Tiers__local__Precision=FP8, and phase 9 (the run) passed AGENTJOULES__Tiers__cloudgpu__ModelId:

```
env $AJ_ENV ./AgentJoules.Cli validate --config
# Validated 5 model IDs, exit code 0
env $AJ_ENV ./AgentJoules.Cli run --tier cloudgpu --dry-run
# cloudgpu: the model nvidia/nemotron-3-nano on localhost reported OK; exit code 0
env $AJ_ENV ./AgentJoules.Cli calibrate --seconds 60 --suffix cloudgpu
# exit code 0, 12:44:14Z
env $AJ_ENV AGENTJOULES__CloudGpu__ObservedCostUsd=0.00 ./AgentJoules.Cli run --tier cloudgpu --repeat 3
# exit code 0, 12:49:48Z, elapsed 147 s
```

The calibration runs through the local tier's configuration: the calibrate command selects only the local tier, so its workload ran with the Tiers:local settings (the system prompt prefix and max_tokens of that tier, no ExtraBodyJson), and the NIM thinking mode was on by default during the calibration (inferred: no probe sent a request without chat_template_kwargs, and the effect of the local prefix "detailed thinking off" on this model was not measured). The comparison of the counter against the sampling (0.0056, see Probe) is unaffected, because both methods metered the same workload.

Laptop, after the run (the key variables stay in the environment, never printed):

```
scp -i "$CLOUDGPU_SSH_KEY" -o IdentitiesOnly=yes "$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST:agentjoules/results/*-cloudgpu.json" results/
scp -i "$CLOUDGPU_SSH_KEY" -o IdentitiesOnly=yes -r "$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST:agentjoules/log" ./vm-log
./AgentJoules.Cli validate results/
# Validated 4 files, exit code 0
```

The log directory went to the session scratchpad, never into the repository. Key scans over the log and results (both keys on standard input) printed only "scanned" on the VM and on the laptop copies: no hit.

## Probe

- File: results/probe-20261026T122330Z-cloudgpu.json, written by `probe --json --save --suffix cloudgpu` at phase 4 (exit code 0).
- gpuPresent true; name NVIDIA H100 80GB HBM3; computeCapability 9.0; powerLimitW 700; sampleRateHz 10 (all measured).
- migMode disabled; energyCounterSupported true; method counter; the UUID and the driver are the ones in Instance.
- Calibration: results/calibration-20261026T124311Z-cloudgpu.json, 60 s, window 62.18 s, 624 samples at 10 Hz. The counter gave 20796.42 J and the sampling 20679.15 J; the relative difference is 0.0056 (all measured), within 0.6 percent.
- The calibration file records FP8 on the NIM, GPU offload 100 percent and local runtime nim; the run file records precision FP8 and leaves gpuOffloadPercent and localRuntime null. powerSource is unknown in both (a GPU-board measurement, not a wall meter).

## Runs

- Start 2026-10-26T12:47:21Z; end 2026-10-26T12:49:48Z; elapsed 147 s (measured). Command: `run --tier cloudgpu --repeat 3` inside the tmux session; exit code: 0.
- File: results/20261026T124721Z-cloudgpu.json; status completed; stopReason null (no stop); tasks incident-brief, api-migration, energy-explainer; repeat 3; 36 steps, all outcome ok (measured).
- Energy: baseline 30.0005 s at 118.52 W with coefficient of variation 0.00029; netJoules measured on all 36 steps (140.58 J to 500.27 J, method counter); no sampling fallback, because the counter was present (EDGE-015).
- Run totals: grossJoules 16364.054, netJoules 10389.62, 25379 input and 15889 output tokens, wall clock 50.73 s (all measured).
- Environment in the file: gpuName NVIDIA H100 80GB HBM3, instancePreset 1gpu-16vcpu-200gb, region eu-north1, provider nebius-ai-cloud, precision FP8, reasoningMode off, containerDigest as above, cloudCostUsd not-reported with reason billed-after-run (the whole-run bill is in Cost).
- Passes: pass 1 launched 12:22:34Z, resumed 12:23:23Z after exit code 25, final exit code 0 at 12:39:44Z; pass 2 launched 12:43:10Z, exit code 0 at 12:44:14Z; pass 3 launched 12:47:21Z, exit code 0 at 12:49:48Z.
- Judge failures: 3 of 36 steps failed on the free NVIDIA judge with HTTP 429 (the status seen in the VM log; the committed file records RetriesExhaustedException) after 5 attempts (energy-explainer Reviewer repeat 1, Planner repeat 2, Writer repeat 2). Their quality is N/A with reason judge-failed; the other 33 steps are model-judged. The run is kept and was not re-run.
- Incidents: none for the VM, the SSH session or the budget guard. No run was retried or repeated past the box or the cap; the workload steps needed no retry (0 in the file); the judge retried within its policy (35 retries over 16 of the 36 judge calls, 3 exhausted after 5 attempts) and the run was not re-run. No stop of the budget guard and no partial file occurred.
- The EDGE-016 (lost VM, incomplete file) and EDGE-017 (cost cap, partial file) paths were not exercised by this run; the tests of AJ-123 and AJ-122 cover them.
- Deviation, no warm-up call (found by the M5 exit reviews): REQ-031 asks for the same warm-up as the local tier, but this run made no warm-up call, because the orchestrator warms up the local tier only (RunOrchestrator). environment.modelLoadSeconds is null in the run file (measured); the first step starts 0.44 s after the 30.0005 s baseline ended (derived from run.startUtc 12:47:21Z and the first step start 12:47:51Z), and phase 9 of the VM log shows no warm-up. What had served the engine before the run (measured): the same NIM answered the phase 6 endpoint checks (exit code 0 at 12:39:44Z) and the 60 s calibration of 12:43:12Z to 12:44:14Z, which had its own warm-up; the run started 3 min 7 s after the calibration ended. The first repeat differs from the others: the time to first token of the incident-brief Planner step is 174.7 ms in repeat 1 against 28.8 ms and 29.2 ms in repeats 2 and 3 (measured, run file). The likely effect on the energy figures is small, but it is not measured. The decision put to the user at the M5 exit: the deviation is accepted by the orchestrator and recorded here, the run is not re-run (the choice is surfaced to the user; the orchestrator assumed it for this record). Carried to M6 as a code change (owner: M6 kickoff item, orchestrator): a warm-up through the cloudgpu tier's own client before the baseline, so that modelLoadSeconds is measured on a future cloud run.
- Local validation of the copied files: exit code 0, "Validated 4 files".

## Cost

- Hourly price: USD 4.63 per hour (console). Cost cap of the story: USD 30. Stop line of the guard: USD 25. Time box: 4 hours from 2026-10-26T12:19:28Z.
- Observed cost at the pass 3 start (billing page, read 2026-10-26T12:47Z): Total USD 0.00, subtotal under USD 0.01 (console). The page showed "Last updated 11:20 UTC", before VM2 existed, so it lags about an hour; the figure was passed to the run as `ObservedCostUsd=0.00` with that lag. Balance USD 23.54 (console).
- Derived: from the handover stamp to the container teardown at 12:51:29Z is 32 min 1 s, which at USD 4.63 per hour is about USD 2.47. This is an estimate, not the bill; as of that reading (12:51Z) the VM kept billing until the user deleted it at 13:50Z.
- Stop reason: no stop triggered (neither cost-cap nor time-box).
- Whole-run cost: about USD 7 (estimated: 1.509 GPU-hours at USD 4.63 per hour is USD 6.99; the balance difference of USD 5.92 is a lower bound read between two lagging snapshots, not the bill). The figure is recorded under Teardown as an estimate from the console hourly price; the usage page's own line for the VM is carried forward to the M5 exit or its addendum in docs/build-log.md.
- If the final bill is above the stop line, the user is told to delete the VM; this did not occur.

## Teardown

- Container: `bash cloudgpu-bootstrap.sh --teardown` over SSH at 2026-10-26T12:51:29Z, exit code 0 (measured): the container agent-joules-nim-vm was removed, docker config directories left 0, no container and no GPU process left.
- Key hygiene: the NGC key was unset in the tmux environment after each pass; the pull job ended with docker logout.
- The VM and its boot disk are deleted by the user in the console (AJ-128-T4), never by the builder.

Deleted: yes (user-confirmed in the chat at 2026-10-26T13:51Z: "I deleted it in 2:50 PM CET", the VM with its boot disk, deleted from the console by the user; no cloud resource was touched by the builder)
Final cost: USD 6.99 (estimated: 1.509 GPU-hours from the handover stamp 2026-10-26T12:19:28Z to the deletion at 13:50Z at the console price of USD 4.63 per hour, billed per second; the estimate starts at the handover stamp, and the VM's creation a few minutes earlier (not recorded for VM2) adds a few cents; the account balance moved from USD 23.54 at 12:47Z to USD 17.62 after the deletion, a difference of USD 5.92 read between two lagging snapshots and therefore a lower bound, not the bill; the usage page's own line for the VM is carried forward to the M5 exit or its addendum; the USD 25 stop line and the USD 30 cap were not reached)
Deletion time: 2026-10-26T13:50Z (user-confirmed, minute precision, 14:50 CET converted to UTC; the VM ran about 90 minutes from the handover stamp)
