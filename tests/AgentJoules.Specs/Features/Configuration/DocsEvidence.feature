@unit @AJ-S153 @DoD
Feature: AJ-153 Documentation and Devpost file evidence
  @positive
  Scenario: AJ-153 Each reference document carries a verification date
    Given the four committed documents
    When the "Verified:" lines are read
    Then each document has a date of 2026-10-17 or later
  @positive
  Scenario: AJ-153 The checklist has the Demo URL line and the four headings
    Given the committed docs/devpost-checklist.md
    When it is parsed
    Then it has one "Demo URL:" line and the headings Registration, Tracks, Judging criteria, Credits
  @positive
  Scenario: AJ-153 The story has no placeholder and no checklist section
    Given the committed docs/devpost-story.md
    When placeholders and headings are counted
    Then "[[FILL:" occurs 0 times and no heading contains "Fill-after-build"
  @positive
  Scenario: AJ-153 The feedback file has seven user placeholders listed in the checklist
    Given the committed feedback file and checklist
    When the owner markers and the checklist heading are read
    Then "owner: builder" occurs 0 times, "owner: user" occurs 7 times and the checklist lists seven bullets
  @negative
  Scenario: AJ-153 A document without a verification date is rejected
    Given a copy of docs/methodology.md without a "Verified:" line
    When the documentation check runs
    Then it fails with "no verification date: docs/methodology.md"
  @negative
  Scenario: AJ-153 A not-verified checklist leaves box 9 unchecked
    Given a checklist whose line reads "Verified: not verified (checked 2026-10-29, HTTP 403)"
    When the documentation check runs
    Then box 9 is reported unchecked with the reason "rules page not verified"
  @negative
  Scenario: AJ-153 A leftover story placeholder is rejected
    Given a copy of the story with one "[[FILL:" left
    When the documentation check runs
    Then it fails with "leftover placeholder in docs/devpost-story.md"
  @negative
  Scenario: AJ-153 A leftover builder placeholder in the feedback file is rejected
    Given a copy of the feedback file with one "owner: builder" placeholder
    When the documentation check runs
    Then it fails with "builder placeholder left in docs/devpost-feedback.md"
  @negative
  Scenario: AJ-153 A checklist without the Feedback heading is rejected
    Given a copy of the checklist without "Feedback form, user fills"
    When the documentation check runs
    Then it fails with "missing heading: Feedback form, user fills"
