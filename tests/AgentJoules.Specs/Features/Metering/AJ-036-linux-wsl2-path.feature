@unit @AJ-S036 @EDGE-014 @EDGE-001
Feature: Load NVML on Linux and WSL2 and prove the no-GPU container path
  @positive
  Scenario: AJ-036 A Linux machine without the library reports no GPU
    Given the Linux loader is faked as missing
    When I run "probe --json"
    Then gpuPresent is false and the exit code is 0
  @positive @integration
  Scenario: AJ-036 The container prints gpuPresent false and exits 0
    Given Docker Desktop is running and the sdk:10.0 image tag exists
    When the EDGE-001 docker command runs without --gpus
    Then it exits 0 and stdout parses as JSON with gpuPresent false
  @negative
  Scenario: AJ-036 A missing library never crashes the process
    Given the native library cannot be found
    When the factory opens device 0
    Then the result is Unavailable naming "libnvidia-ml.so.1" and no exception escapes
  @negative @integration
  Scenario: AJ-036 A stopped Docker daemon is recorded as blocked
    Given "docker info" exits non-zero
    When the container check is attempted
    Then docs/build-log.md records "blocked: Docker daemon not running" with the date
  @negative @integration
  Scenario: AJ-036 An absent image tag is recorded, not assumed
    Given "docker manifest inspect mcr.microsoft.com/dotnet/sdk:10.0" fails
    When the container check is attempted
    Then the verified replacement tag is recorded before the run
  @negative @integration
  Scenario: AJ-036 WSL2 without a .NET SDK is recorded as not run
    Given the WSL2 distro has no dotnet command
    When the WSL2 probe is attempted
    Then docs/build-log.md records "not run" with the reason
