@integration @AJ-S108 @REQ-018
Feature: Verify streaming usage and the reasoning toggle per tier
  @positive
  Scenario: AJ-108 the nvidia tier reports streaming usage and honours the toggle
    Given NVIDIA_API_KEY is set
    When one streaming call is made with include_usage and the reasoning toggle JSON
    Then "Streaming usage: reported" is recorded with the token counts
    And "Reasoning result: off" is recorded with the evidence
  @positive
  Scenario: AJ-108 the nebius tier is verified the same way
    Given NEBIUS_API_KEY is set
    When the same two calls are made against the nebius tier
    Then its section records the usage behaviour, the toggle JSON and the result
  @negative
  Scenario: AJ-108 Ollama omits streaming usage
    Given the local Ollama endpoint returns no usage in the final stream chunk
    When the builder records the local section
    Then "Streaming usage: not-reported" is recorded
    And docs/build-log.md records the ADR-09 decision
    And a metered local call shows tokens labelled "not-reported" and never 0
  @negative
  Scenario: AJ-108 the toggle is ignored by the endpoint
    Given the response with the toggle still contains reasoning content
    When the builder records the tier section
    Then "Reasoning result: unsupported" is recorded and Tiers:<tier>:ReasoningMode is "unsupported"
  @negative
  Scenario: AJ-108 the endpoint rejects the unknown body field
    Given the endpoint answers HTTP 400 to the request containing the toggle JSON
    When the builder records the tier section
    Then the section records "Reasoning toggle: unsupported" with the error excerpt and no key value
  @negative
  Scenario: AJ-108 a missing key stops the check
    Given NVIDIA_API_KEY is not set
    When I run "run --tier all --dry-run"
    Then the exit code is 3
    And the output names "NVIDIA_API_KEY"
