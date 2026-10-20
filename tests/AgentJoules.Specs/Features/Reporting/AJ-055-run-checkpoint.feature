@unit @AJ-S055 @REQ-017
Feature: AJ-055 Write run files atomically after every task and repeat
  @positive
  Scenario: AJ-055 run file name follows the committed pattern
    Given a run for tier "nvidia" starting at 2026-10-22 09:15:30 UTC
    When the run file name is created
    Then the name is "20261022T091530Z-nvidia.json"
  @positive
  Scenario: AJ-055 non-Nemotron model switches the baseline suffix on
    Given a run where one agent model ID does not contain "nemotron"
    When the run file name is created
    Then the name ends with "-baseline.json"
  @positive
  Scenario: AJ-055 first checkpoint has status running
    Given a started run
    When the first checkpoint is written
    Then the file parses and run.status is "running"
  @negative
  Scenario: AJ-055 failed write keeps the previous complete file
    Given a complete file from checkpoint 1
    When writing checkpoint 2 fails after the partial file was created
    Then the file still holds the content of checkpoint 1
  @negative
  Scenario: AJ-055 N/A metric is serialised with a null value
    Given a step whose cost is N/A with reason "free-tier-no-price"
    When the document is serialised
    Then the cost value is null and the label is "N/A"
    And the text "0" does not appear as the cost value
  @negative
  Scenario: AJ-055 unwritable output directory exits 1 before any call
    Given an output directory that cannot be written
    When "run --tier nvidia --out <dir> --repeat 1" is executed
    Then the exit code is 1
    And the fake handler recorded 0 requests
