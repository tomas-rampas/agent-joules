@unit @AJ-S128 @REQ-031 @EDGE-016 @EDGE-017
Feature: Document the cloudgpu run, the cost and the user's teardown
  @positive
  Scenario: AJ-128 a complete document passes
    Given a run document with the six headings, a Cost section of "USD 6.12" and a Teardown section containing "deleted"
    When the contract test runs
    Then the result is pass
  @positive
  Scenario: AJ-128 a cost of exactly 30 USD passes
    Given the Cost section states "USD 30.00"
    When the contract test runs
    Then the result is pass
  @negative
  Scenario: AJ-128 a missing Teardown heading fails
    Given the document has no "Teardown" heading
    When the contract test runs
    Then the result is fail and the message names "Teardown"
  @negative
  Scenario: AJ-128 a cost above the cap fails
    Given the Cost section states "USD 31.20"
    When the contract test runs
    Then the result is fail and the message names the 30 USD cap
  @negative
  Scenario: AJ-128 a pending teardown fails
    Given the Teardown section says "pending user confirmation" and does not contain "deleted"
    When the contract test runs
    Then the result is fail and the message says the teardown is not confirmed
  @negative
  Scenario: AJ-128 an IP address literal fails
    Given the Commands section contains a dotted IPv4 address
    When the contract test runs
    Then the result is fail without echoing the address
  @negative
  Scenario: AJ-128 a runtime-built key fails
    Given the document contains a key built at runtime as "nvapi-" plus a random suffix
    When the contract test runs
    Then the result is fail and the message does not echo the suffix
