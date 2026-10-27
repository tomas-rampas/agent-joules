@unit @AJ-S139 @REQ-028
Feature: AJ-139 Devpost checklist user actions
  @positive
  Scenario: AJ-139 Registration lines are present and left for the user
    Given the committed docs/devpost-checklist.md
    When the Registration section is read
    Then it has the four exact lines and three values read "<user fills>"
  @positive
  Scenario: AJ-139 The three user commands are in labelled fenced blocks
    Given the committed checklist
    When the fenced blocks are extracted
    Then blocks contain "git push", "gh release create" and the Pages settings
    And each is preceded by "User action (the builder never runs this):"
  @positive
  Scenario: AJ-139 Judging criteria map to existing artifacts
    Given the Judging criteria section
    When each mapped artifact is checked
    Then the four criteria map to paths or README headings that exist
  @positive
  Scenario: AJ-139 Tracks name a recommendation and leave the pick to the user
    Given the Tracks section
    When it is read
    Then it recommends one track with one reason and says "Final pick: the user decides"
  @negative
  Scenario: AJ-139 A filled Devpost handle is rejected
    Given a copy of the checklist with "Devpost handle: tomas-rampas"
    When the checklist check runs
    Then it fails with "builder filled a user field: Devpost handle"
  @negative
  Scenario: AJ-139 A missing release block is rejected
    Given a copy of the checklist without the string "gh release create"
    When the checklist check runs
    Then it fails with "missing command block: gh release create"
  @negative
  Scenario: AJ-139 A command block not labelled as a user action is rejected
    Given a copy of the checklist where the push block lacks the user-action label
    When the checklist check runs
    Then it fails with "block not marked as user action: git push"
  @negative
  Scenario: AJ-139 A missing Credits heading is rejected
    Given a copy of the checklist without the heading "Credits"
    When the checklist check runs
    Then it fails with "missing heading: Credits"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-139 Credits, the Pages label and the pre-push checks are written
    Given the committed checklist
    When the Credits section, the Pages label and the Before you push list are checked
    Then every one passes
  @negative
  Scenario: AJ-139 A Pages block without the UI path is rejected
    Given a copy of the checklist whose Pages block lacks the UI path
    When the checklist check runs
    Then it fails with "Pages block lacks the UI path"
