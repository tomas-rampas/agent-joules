@unit @AJ-S123 @EDGE-016
Feature: Keep incomplete cloudgpu run files out of reports
  @positive
  Scenario: AJ-123 a mid-stream connection drop yields one incomplete step
    Given a fake transport that drops the connection during the second agent call
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then the second step has outcome "incomplete" and run.status is "incomplete"
    And the exit code is 5
    And the output asks the user to check the VM state in the console
  @positive
  Scenario: AJ-123 a completed file is unaffected
    Given a completed cloudgpu run file and an older incomplete one
    When the latest file for tier "cloudgpu" is selected
    Then the completed file is returned
  @negative
  Scenario: AJ-123 no retry follows the drop
    Given a fake transport that drops the connection and ample time and budget remain
    When the run stops
    Then the fake recorded no attempt after the drop
  @negative
  Scenario: AJ-123 the report renders no row for an incomplete file
    Given a report input whose only cloudgpu file has status "incomplete"
    When the report is rendered
    Then the summary contains "cloudgpu | not run" and no cloudgpu step row
  @negative
  Scenario: AJ-123 a file left as running is skipped
    Given a copied cloudgpu file with run.status "running"
    When the latest file for tier "cloudgpu" is selected
    Then no file is returned
  @negative
  Scenario: AJ-123 rate-limit exhaustion is an error and not incomplete
    Given the fake endpoint answers HTTP 429 five times
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then the failed step has outcome "error"
    And run.status is "failed"
