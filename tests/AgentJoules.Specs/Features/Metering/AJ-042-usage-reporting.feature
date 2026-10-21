@unit @AJ-S042 @REQ-008 @EDGE-007
Feature: AJ-042 Report absent usage as not-reported, never 0
  @positive
  Scenario: AJ-042 usage with 120 input and 45 output tokens is measured
    Given a fake client that returns usage with 120 input and 45 output tokens
    When a buffered call is metered
    Then inputTokens is 120 with label "measured"
    And outputTokens is 45 with label "measured"
  @positive
  Scenario: AJ-042 provider-reported zero tokens stays measured
    Given a fake client that returns usage with 0 input and 0 output tokens
    When a buffered call is metered
    Then inputTokens is 0 with label "measured"
  @negative
  Scenario: AJ-042 buffered call without usage reports not-reported
    Given a fake client that returns no usage
    When a buffered call is metered
    Then inputTokens and outputTokens have label "not-reported" and a null value
    And cost has label "N/A" and a null value
  @negative
  Scenario: AJ-042 streaming call without usage reports not-reported
    Given a fake stream whose updates carry no usage content
    When the stream is consumed
    Then inputTokens and outputTokens have label "not-reported" and a null value
  @positive
  Scenario: AJ-042 output above the task limit gets the max-tokens-exceeded flag
    Given a task with maxOutputTokens 512 and a response reporting 600 output tokens
    When a buffered call is metered
    Then the reading has flag "max-tokens-exceeded"
  @negative
  Scenario: AJ-042 unreported output tokens carry no max-tokens flag
    Given a task with maxOutputTokens 512 and a response without usage
    When a buffered call is metered
    Then the reading has no flag "max-tokens-exceeded"
