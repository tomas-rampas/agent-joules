@integration @AJ-S126 @REQ-031
Feature: Serve Nemotron 3 Nano in FP8 with NIM on the VM
  @positive
  Scenario: AJ-126 the pull succeeds and the digest is recorded
    Given the NGC login on the VM succeeded through standard input
    When the builder pulls "nvcr.io/nim/nvidia/nemotron-3-nano"
    Then the pull string is confirmed and the digest is recorded as ContainerDigest
  @positive
  Scenario: AJ-126 the FP8 profile serves and the models call returns the model ID
    Given the NIM profile listing contains a single-GPU FP8 profile
    When the container starts with NIM_MODEL_PROFILE set to it
    Then GET /v1/models from the VM returns a model ID
    And the "## cloudgpu" section of docs/endpoints.md records the ID, the date and the command
  @negative
  Scenario: AJ-126 the pull path is wrong and the fallback image is used
    Given the pull of "nvcr.io/nim/nvidia/nemotron-3-nano" returns a not-found error
    When the builder uses "nvcr.io/nim/nvidia/model-free-nim"
    Then the Commands section records the error and the substitution
  @negative
  Scenario: AJ-126 no FP8 profile is listed
    Given the NIM profile listing contains only BF16 profiles
    When the builder reads the listing
    Then the builder stops and reports instead of using BF16
  @negative
  Scenario: AJ-126 the container runs out of KV-cache memory
    Given the container exits with a memory error at the default context length
    When the builder sets NIM_MAX_MODEL_LEN to a lower value
    Then the value used is recorded and the models call succeeds
  @negative
  Scenario: AJ-126 the NGC login is rejected
    Given the NGC login returns an authorization error
    When the builder records the result
    Then the error is recorded without any key value
  @negative
  Scenario: AJ-126 another process holds the GPU during measurement
    Given nvidia-smi lists a compute process other than the NIM process
    When the builder checks exclusivity
    Then measurement does not start and the process is recorded
