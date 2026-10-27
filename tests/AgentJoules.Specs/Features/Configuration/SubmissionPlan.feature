@unit @AJ-S158 @DoD
Feature: AJ-158 Submission order and cut-off plan
  @positive
  Scenario: AJ-158 The submission order has 12 user actions in the form order
    Given the committed docs/devpost-checklist.md
    When the Submission order section is parsed
    Then it has 12 numbered items each ending with "(user)" in the specified order
  @positive
  Scenario: AJ-158 The timeline offsets are correct
    Given the Timeline section
    When each UTC and PDT pair is compared
    Then PDT equals UTC minus 7 hours for every pair and the last target is at least 3 hours before 17:00 UTC
  @positive
  Scenario: AJ-158 The cut-off plan has a row for every missing item
    Given the Cut-off plan table
    When its rows are read
    Then rows exist for cloudgpu, local, nvidia, nebius, judge, Pages, video and feedback
    And each row names files, a regeneration command, a fallback story and unchecked boxes
  @positive
  Scenario: AJ-158 Regenerating without a cloudgpu file yields the not-run row
    Given a fixture workspace with run files for local, nvidia and nebius
    When the commands of the cloudgpu row are run
    Then the README block and summary contain "cloudgpu | not run" and "report --verify-readme" exits 0
  @negative
  Scenario: AJ-158 A timeline pair with a wrong offset is rejected
    Given a Timeline line "17:00 UTC (09:00 PDT)"
    When the checklist check runs
    Then it fails with "offset must be 7 hours: 17:00 UTC (09:00 PDT)"
  @negative
  Scenario: AJ-158 A submission step without a user marker is rejected
    Given a Submission order item without "(user)"
    When the checklist check runs
    Then it fails with "step not marked as user action: 11"
  @negative
  Scenario: AJ-158 A cut-off row without a regeneration command is rejected
    Given the cloudgpu row with the command column empty
    When the checklist check runs
    Then it fails with "no regeneration command: cloudgpu"
  @negative
  Scenario: AJ-158 A missing deadline string is rejected
    Given a checklist without "2026-10-30 10:00 PDT (17:00 UTC)"
    When the checklist check runs
    Then it fails with "missing deadline"
  @negative
  Scenario: AJ-158 A claim that the builder submitted is rejected
    Given a checklist sentence "the builder submitted the project"
    When the checklist check runs
    Then it fails with "builder action claimed: submitted"
