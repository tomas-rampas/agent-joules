@unit @AJ-S112 @REQ-022
Feature: Write the methodology document with a contract test
  @positive
  Scenario: AJ-112 a complete document passes
    Given a methodology document with the five headings and the seven Limitations strings
    And it names a calibration file and quotes its stored relativeDifference
    When the contract test runs
    Then the result is pass
  @positive
  Scenario: AJ-112 the Limitations section may use the strings in any order
    Given the seven strings appear in a different order
    When the contract test runs
    Then the result is pass
  @negative
  Scenario: AJ-112 a missing heading fails
    Given the document has no "Baseline" heading
    When the contract test runs
    Then the result is fail and the message names "Baseline"
  @negative
  Scenario: AJ-112 a missing Limitations string fails
    Given the Limitations section lacks the string "cloud"
    When the contract test runs
    Then the result is fail and the message names "cloud"
  @negative
  Scenario: AJ-112 a quoted difference that differs from the file fails
    Given the document quotes 0.5 while the named calibration file stores a different value
    When the contract test runs
    Then the result is fail and the message names both values
  @negative
  Scenario: AJ-112 no calibration file is named
    Given the document names no results/calibration file
    When the contract test runs
    Then the result is fail and the message says the calibration file must be named
  @negative
  Scenario: AJ-112 a missing dashboard sentence fails
    Given the Limitations section lacks the sentence "The dashboard server adds no GPU work." or the sentence "Estimates shown on the live dashboard never enter results files."
    When the contract test runs
    Then the result is fail and the message names the sentence
  @negative
  Scenario: AJ-112 an existing sentence from an earlier milestone survives the merge
    Given docs/methodology.md already holds a sentence written by an M3 story under no required heading
    When the headings are created by merging into the existing document
    Then the sentence is still present verbatim in the document
    And the five required headings exist
