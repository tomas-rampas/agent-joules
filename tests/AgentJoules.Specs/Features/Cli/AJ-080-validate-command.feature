@unit @AJ-S080 @REQ-015
Feature: AJ-080 Validate run files with AJ validate
  @positive
  Scenario: AJ-080 three valid files are counted
    Given a folder with 3 valid run files and 1 probe file
    When "validate <folder>" is executed
    Then the output contains "Validated 3 files" and the exit code is 0
  @positive
  Scenario: AJ-080 validation works without the docs folder
    Given a working directory that has no docs folder
    When "validate <folder>" is executed on valid files
    Then the exit code is 0
  @negative
  Scenario: AJ-080 corrupted file exits 2 and names the location
    Given a folder with one run file where cost has label "N/A" and value 0.5
    When "validate <folder>" is executed
    Then the exit code is 2
    And one output line names the file and the cost location
  @negative
  Scenario: AJ-080 missing path exits 2
    Given a path that does not exist
    When "validate <path>" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-080 non-JSON run file exits 2 naming the file
    Given a run file name that holds the text "not json"
    When "validate <folder>" is executed
    Then the exit code is 2 and the output names the file
