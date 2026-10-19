@unit @AJ-S024 @REQ-005
Feature: Define the Metric label object and the reading flags
  @positive
  Scenario: AJ-024 A measured metric keeps its number and unit
    Given a metric created with Measured(180, "J")
    When it is serialized
    Then the JSON has "value": 180, "label": "measured" and "unit": "J"
  @positive
  Scenario: AJ-024 A not-reported metric has a null value and a reason
    Given a metric created with NotReported("provider-omitted-usage")
    When it is serialized
    Then the JSON has "value": null, "label": "not-reported" and the reason
  @positive
  Scenario: AJ-024 Flags map to the vocabulary strings
    Given the flags BelowBaseline and Throttled
    When ToStrings is called
    Then the result is "below-baseline" and "throttled"
  @negative
  Scenario: AJ-024 An N/A metric with a number is rejected
    Given a metric constructed with label N/A and value 0
    When the constructor runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-024 A measured metric without a value is rejected
    Given a metric constructed with label measured and a null value
    When the constructor runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-024 An N/A metric without a reason is rejected
    Given a metric constructed with label N/A and an empty reason
    When the constructor runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-024 NaN is rejected
    Given a measured metric with value NaN
    When the constructor runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-024 An unknown flag string is rejected
    Given the string "overheated"
    When ReadingFlagNames.Parse runs
    Then it fails with a typed error
