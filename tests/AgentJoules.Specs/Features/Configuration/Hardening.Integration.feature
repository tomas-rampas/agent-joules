@integration @AJ-S155 @DoD
Feature: AJ-155 Key scan with the real keys
  @positive
  Scenario: AJ-155 No tracked line contains a key value
    Given NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY are set
    When "git grep -nF" runs for each value
    Then each exits 1 and prints no line
  @negative
  Scenario: AJ-155 A key value found in the tree fails the scan
    Given a temporary tracked file containing the NGC_API_KEY value
    When "git grep -nF" runs for it
    Then the scan fails and prints the file name only
