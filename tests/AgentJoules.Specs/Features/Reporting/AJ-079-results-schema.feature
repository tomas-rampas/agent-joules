@unit @AJ-S079 @REQ-015
Feature: AJ-079 Generate the JSON Schema and guard it against drift
  @positive
  Scenario: AJ-079 generated document validates against the schema
    Given a generated fixture document
    When it is validated against the schema
    Then the result is valid
  @positive
  Scenario: AJ-079 committed schema equals the generated schema
    Given the committed docs/results-schema.json
    When the schema is regenerated from the model
    Then the two JSON trees are equal
  @negative
  Scenario: AJ-079 N/A metric with a number is invalid
    Given a fixture where cost has label "N/A" and value 0.5
    When it is validated
    Then the result is invalid at the cost location
  @negative
  Scenario: AJ-079 quality labelled measured is invalid
    Given a fixture where quality has label "measured" and value 4
    When it is validated
    Then the result is invalid at the quality location
  @negative
  Scenario: AJ-079 stale committed schema fails the drift test
    Given a committed schema that lacks a field the model now has
    When the drift test runs
    Then the test fails and names the update variable
  @positive
  Scenario: AJ-079 a run flagged non-publishable validates
    Given a generated fixture whose run.flags is ["non-publishable"]
    When it is validated against the schema
    Then the result is valid
  @negative
  Scenario: AJ-079 an unknown run flag is invalid
    Given a generated fixture whose run.flags is ["made-up"]
    When it is validated against the schema
    Then the result is invalid at the run flags location
