@unit @AJ-S105 @REQ-020
Feature: Disclose the local runtime, tag, offload and precision
  @positive
  Scenario: AJ-105 full offload on Ollama
    Given the fake /api/ps reports size_vram equal to size for the configured tag
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then environment.localRuntime is "ollama" and environment.gpuOffloadPercent is 100
    And run.flags does not contain "partial-offload"
  @positive
  Scenario: AJ-105 the NIM runtime reports 100 by construction
    Given Local:Runtime is "nim" and Tiers:local:Precision is "BF16"
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then environment.localRuntime is "nim" and environment.gpuOffloadPercent is 100
    And environment.precision is "BF16"
  @positive
  Scenario: AJ-105 precision comes from the Ollama quantization level
    Given the fake /api/show reports quantization level "Q4_K_M"
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then environment.precision is "Q4_K_M"
  @negative
  Scenario: AJ-105 partial offload sets the flag and the header
    Given the fake /api/ps reports size_vram at 62 percent of size
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then environment.gpuOffloadPercent is 62
    And run.flags contains "partial-offload"
    And the rendered report header contains "partial-offload"
  @negative
  Scenario: AJ-105 the model is missing from the process list
    Given the fake /api/ps lists no model after warm-up
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then the exit code is 6
    And the output says the offload could not be determined
  @negative
  Scenario: AJ-105 a zero size is refused
    Given the fake /api/ps reports size 0 for the configured tag
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then the exit code is 6
    And the run file contains no gpuOffloadPercent value of 0
