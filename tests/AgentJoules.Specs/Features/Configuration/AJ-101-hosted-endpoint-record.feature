@integration @AJ-S101 @REQ-018
Feature: Record the nvidia and nebius endpoint verification
  @positive
  Scenario: AJ-101 the nvidia listing is recorded with date and redacted command
    Given NVIDIA_API_KEY is set in the environment
    When the builder calls GET {BaseUrl}/models for the nvidia tier and writes the "## nvidia" section
    Then the section has "Verified:" with a UTC date of 2026-10-17 or later
    And the section lists every returned Nemotron and Qwen model as "- model: <id>"
    And the recorded command shows "Bearer ****" and no key value
  @positive
  Scenario: AJ-101 the nebius tier uses the same model when it is listed
    Given NEBIUS_API_KEY is set and the nebius listing contains the model chosen for nvidia
    When the builder writes the "## nebius" section
    Then the chosen nebius model equals the chosen nvidia model
    And "AJ validate --config" exits 0
  @positive
  Scenario: AJ-101 the smallest listed Nemotron 3 model is chosen when the nvidia model is absent
    Given the nebius listing lacks the nvidia model but lists two other Nemotron 3 models
    When the builder chooses the nebius model
    Then the smaller of the two is chosen and the reason is recorded in the section
  @negative
  Scenario: AJ-101 no Nemotron model on nebius is reported as blocked
    Given the nebius listing contains no model whose ID contains "nemotron"
    When the builder writes the "## nebius" section
    Then the section says "BLOCKED" and names the missing item
    And the final report lists the nebius tier as blocked and not skipped
  @negative
  Scenario: AJ-101 a missing key stops the check before any call
    Given NEBIUS_API_KEY is not set
    When I run "run --tier nebius --dry-run"
    Then the exit code is 3
    And the output names "NEBIUS_API_KEY" and the user-secrets alternative
  @negative
  Scenario: AJ-101 the 45-minute box expires
    Given the nebius onboarding started more than 45 minutes ago without a listed model
    When the builder closes the box
    Then the section records what was verified and what failed
    And the build is reported blocked on the nebius item
