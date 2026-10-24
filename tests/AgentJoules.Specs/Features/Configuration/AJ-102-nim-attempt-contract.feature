@unit @AJ-S102 @REQ-019 @EDGE-010
Feature: Enforce the NIM attempt document contract
  @positive
  Scenario: AJ-102 a complete document passes
    Given a NIM attempt document with the four headings, Started and Ended 62 minutes apart and "Local runtime: ollama"
    When the contract test runs
    Then the result is pass
  @positive
  Scenario: AJ-102 exactly 90 minutes passes
    Given Started and Ended are exactly 90 minutes apart
    When the contract test runs
    Then the result is pass
  @positive
  Scenario: AJ-102 an exceeded box with a stated reason passes
    Given Started and Ended are 104 minutes apart and a line "Box exceeded: image pull stalled"
    When the contract test runs
    Then the result is pass
  @negative
  Scenario: AJ-102 a missing heading fails
    Given the document has no "Outcome" heading
    When the contract test runs
    Then the result is fail and the message names "Outcome"
  @negative
  Scenario: AJ-102 91 minutes without a statement fails
    Given Started and Ended are 91 minutes apart and no "Box exceeded:" line
    When the contract test runs
    Then the result is fail and the message names the 90-minute box
  @negative
  Scenario: AJ-102 a docker login with a password argument fails
    Given the Commands section contains a docker login line with "--password" followed by a value
    When the contract test runs
    Then the result is fail and the message says the login must use --password-stdin
  @negative
  Scenario: AJ-102 a runtime-built key in the text fails
    Given the document contains a key built at runtime as "nvapi-" plus a random suffix
    When the contract test runs
    Then the result is fail and the message does not echo the suffix
