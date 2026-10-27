@integration @AJ-S151 @DoD
Feature: AJ-151 cloudgpu record carries no key
  @positive
  Scenario: AJ-151 The NGC key value is absent from the run record
    Given NGC_API_KEY is set
    When "grep -F" for its value runs over docs/cloudgpu-run.md
    Then the exit code is 1 and no line is printed
  @negative
  Scenario: AJ-151 A key value in the run record fails the scan
    Given a copy of the run record containing the value of NGC_API_KEY
    When the scan runs
    Then it fails with "key material in docs/cloudgpu-run.md" without printing the value
