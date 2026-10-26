@integration @AJ-S143 @REQ-025 @REQ-026 @REQ-027 @REQ-028
Feature: AJ-143 Key scan with the real keys
  @positive
  Scenario: AJ-143 No tracked line contains a key value
    Given NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY are set
    When "git grep -nF" runs for each value
    Then each returns exit code 1 and prints no line
  @negative
  Scenario: AJ-143 A tracked line containing a key value fails the scan
    Given a temporary tracked file containing the value of NGC_API_KEY
    When "git grep -nF" runs for that value
    Then it prints the file name without the value and the scan fails
