@integration @AJ-S135 @REQ-027 @REQ-028 @REQ-032
Feature: AJ-135 Demo rehearsal with the laptop and keys
  @positive
  Scenario: AJ-135 Dry run of all enabled tiers prints OK
    Given NVIDIA_API_KEY and NEBIUS_API_KEY are set and the local runtime is up
    When "run --tier all --dry-run" runs
    Then the exit code is 0 and each enabled tier prints its endpoint, matched model IDs and "OK"
  @negative
  Scenario: AJ-135 Dry run without a key exits 3 and names the variable
    Given NEBIUS_API_KEY is unset
    When "run --tier all --dry-run" runs
    Then the exit code is 3 and the output names NEBIUS_API_KEY and prints no key value
