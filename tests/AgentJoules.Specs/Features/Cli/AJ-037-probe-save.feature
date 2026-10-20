@unit @AJ-S037 @REQ-006
Feature: Save the probe result and commit the laptop probe file
  @positive
  Scenario: AJ-037 Save writes a timestamped file with the JSON content
    Given the fake clock is 2026-10-19T09:30:15Z and a scripted GPU
    When I run "probe --json --save" in a temp workspace
    Then results/probe-20261019T093015Z.json exists and equals the stdout JSON
  @positive
  Scenario: AJ-037 The suffix is appended
    Given the same clock and GPU
    When I run "probe --json --save --suffix cloudgpu"
    Then results/probe-20261019T093015Z-cloudgpu.json exists
  @positive @integration
  Scenario: AJ-037 The laptop probe matches nvidia-smi
    Given the laptop GPU and AGENTJOULES_INTEGRATION is "1"
    When I run "AJ probe --json --save"
    Then gpuPresent is true, driverVersion equals the nvidia-smi driver version and energyCounterSupported is a boolean
  @positive
  Scenario: AJ-037 A no-GPU save is an honest record
    Given the NVML loader is faked as missing
    When I run "probe --json --save"
    Then the saved file has gpuPresent false and the exit code is 0
  @negative
  Scenario: AJ-037 A suffix without save is an argument error
    Given a scripted GPU
    When I run "probe --suffix cloudgpu"
    Then the exit code is 2
  @negative
  Scenario: AJ-037 A suffix that escapes the folder is rejected
    Given a scripted GPU
    When I run "probe --save --suffix ../x"
    Then the exit code is 2 and no file is written
  @negative
  Scenario: AJ-037 An existing file is never overwritten
    Given results/probe-20261019T093015Z.json already exists and the clock reads the same second
    When I run "probe --save"
    Then the exit code is 1, stderr says "file exists" and the file is unchanged
  @negative @integration
  Scenario: AJ-037 A driver mismatch against nvidia-smi is a defect
    Given the committed probe driverVersion differs from the nvidia-smi value
    When the comparison runs
    Then the story fails and the discrepancy is recorded in docs/build-log.md
