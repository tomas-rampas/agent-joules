@unit @AJ-S086 @REQ-016 @REQ-030
Feature: AJ-086 Wire the report command
  @positive
  Scenario: AJ-086 report writes the three files from fixtures
    Given the fixture results folder
    When "report --in <fixtures> --out <tmp>" is executed
    Then the exit code is 0
    And summary.md, joules-per-step.svg and index.html exist in the output folder
  @positive
  Scenario: AJ-086 empty input still renders four not-run rows
    Given an empty results folder
    When "report --in <empty> --out <tmp>" is executed
    Then the exit code is 0
    And the summary lists "local | not run", "cloudgpu | not run", "nvidia | not run" and "nebius | not run"
  @positive
  Scenario: AJ-086 two runs produce identical files
    Given the fixture results folder
    When the report is generated twice
    Then the SHA-256 sums of the three files are equal between the two runs
  @negative
  Scenario: AJ-086 missing input path exits 2
    Given a path that does not exist
    When "report --in <path>" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-086 schema-invalid run file exits 2 naming the file
    Given a results folder with one run file where cost has label "N/A" and value 0.5
    When "report --in <folder> --out <tmp>" is executed
    Then the exit code is 2 and the output names the file
