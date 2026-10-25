@integration @AJ-S110 @REQ-021
Feature: Run the preflight for the laptop measurement campaign
  @positive
  Scenario: AJ-110 the probe is saved and matches nvidia-smi
    Given the laptop GPU and driver are present
    When I run "probe --json --save"
    Then the exit code is 0
    And the saved probe has gpuPresent true and the driver version printed by nvidia-smi
  @positive
  Scenario: AJ-110 the dry-run passes for every enabled tier
    Given NVIDIA_API_KEY and NEBIUS_API_KEY are set and Ollama is running
    When I run "run --tier all --dry-run"
    Then the exit code is 0
    And each of "local", "nvidia" and "nebius" is followed by "OK"
  @negative
  Scenario: AJ-110 battery power blocks the campaign
    Given the probe reports power source "battery"
    When the builder evaluates the preflight
    Then the campaign is not started
    And the user is asked to connect mains power
  @negative
  Scenario: AJ-110 an unsupported counter proceeds with sampling
    Given the saved probe has energyCounterSupported false
    When the builder evaluates the preflight
    Then the campaign proceeds with method "sampling"
    And docs/build-log.md notes that the README results block must contain "sampling"
  @negative
  Scenario: AJ-110 a missing key fails the dry-run before any call
    Given NEBIUS_API_KEY is not set
    When I run "run --tier all --dry-run"
    Then the exit code is 3
    And the output names "NEBIUS_API_KEY"
  @negative
  Scenario: AJ-110 another GPU application is running
    Given nvidia-smi lists a compute process other than Ollama
    When the builder evaluates the preflight
    Then the process is recorded and the user is asked to close it before the campaign
