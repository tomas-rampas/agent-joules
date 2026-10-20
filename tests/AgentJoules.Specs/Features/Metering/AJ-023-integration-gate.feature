@integration @AJ-S023 @REQ-003
Feature: Gate the NVML integration tests on AGENTJOULES_INTEGRATION
  @positive
  Scenario: AJ-023 The laptop GPU is identified by the real binding
    Given AGENTJOULES_INTEGRATION is "1" and the NVIDIA driver is installed
    When the NVML integration test opens device 0
    Then the name contains "3080" and the power limit is between 50 and 200 W
  @positive @unit
  Scenario: AJ-023 The gate is open only for the value 1
    Given the variable value is "1"
    When IntegrationGate.SkipReason is evaluated
    Then it returns null
  @negative @unit
  Scenario: AJ-023 An unset variable skips the test with a reason
    Given the variable is unset
    When IntegrationGate.SkipReason is evaluated
    Then it returns "Set AGENTJOULES_INTEGRATION=1"
  @negative @unit
  Scenario: AJ-023 The value true does not open the gate
    Given the variable value is "true"
    When IntegrationGate.SkipReason is evaluated
    Then the test is skipped
  @negative
  Scenario: AJ-023 A missing driver with the gate open fails loudly
    Given AGENTJOULES_INTEGRATION is "1" and NVML cannot be loaded
    When the NVML integration test runs
    Then it fails with the Unavailable reason instead of skipping
  @negative @unit
  Scenario: AJ-023 A power limit outside 50 to 200 W fails the assertion
    Given a reported power limit of 300 W
    When PowerLimitInRange is evaluated
    Then it fails
