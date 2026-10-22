@unit @AJ-S075 @REQ-010
Feature: AJ-075 Commit the judge rubric and load it
  @positive
  Scenario: AJ-075 committed rubric has all five levels
    Given the committed config/judge-rubric.md
    When the rubric is loaded
    Then levels 1, 2, 3, 4 and 5 are present
  @positive
  Scenario: AJ-075 rubric text is returned verbatim
    Given a rubric file with known text
    When the rubric is loaded
    Then the returned text equals the file content
  @negative
  Scenario: AJ-075 missing rubric file exits 2
    Given Paths:Rubric points to a file that does not exist
    When the rubric is loaded
    Then the exit code is 2
    And the output names the configured path
  @negative
  Scenario: AJ-075 rubric without level 3 exits 2
    Given a rubric file that defines levels 1, 2, 4 and 5 only
    When the rubric is loaded
    Then the exit code is 2
    And the output names level 3
