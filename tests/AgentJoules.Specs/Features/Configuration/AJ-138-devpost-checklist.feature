@unit @AJ-S138 @REQ-028
Feature: AJ-138 Devpost checklist rules and fields
  @positive
  Scenario: AJ-138 The checklist records the 13 rule items with an evidence class
    Given the committed docs/devpost-checklist.md
    When the Rules section is parsed
    Then it has 13 items and each ends with an evidence class
  @positive
  Scenario: AJ-138 Every field maps to an existing artifact
    Given the Fields to artifacts checkbox list
    When each artifact path is checked
    Then every path exists or is marked "(user)"
    And the About the project row names "docs/devpost-story.md"
  @positive
  Scenario: AJ-138 The Demo URL line names a satisfier and a reason
    Given the committed checklist
    When the line starting "Demo URL:" is read
    Then it names the GitHub Pages report page and gives a reason
  @positive
  Scenario: AJ-138 The deadline is written in both zones
    Given the committed checklist
    When the deadline line is read
    Then it contains "2026-10-30 10:00 PDT (17:00 UTC)"
  @negative
  Scenario: AJ-138 A checklist without a Demo URL line is rejected
    Given a copy of the checklist without the line "Demo URL:"
    When the checklist check runs
    Then it fails with "missing line: Demo URL:"
  @negative
  Scenario: AJ-138 A field that maps to a missing file is rejected
    Given a copy of the checklist mapping "About the project" to "docs/missing-story.md"
    When the checklist check runs
    Then it fails with "artifact not found: docs/missing-story.md"
  @negative
  Scenario: AJ-138 More than 25 Built-with tags are rejected
    Given a copy of the checklist whose Built-with list has 26 tags
    When the checklist check runs
    Then it fails with "26 tags exceed 25"
  @negative
  Scenario: AJ-138 A rule item without an evidence class is rejected
    Given a copy of the checklist with the line "- license requirement: MIT"
    When the checklist check runs
    Then it fails with "no evidence class: license requirement"
  @negative
  Scenario: AJ-138 A Nebius AI Cloud tag without a cloudgpu run is rejected
    Given a workspace with no completed or partial cloudgpu run file and a Built-with list containing "Nebius AI Cloud"
    When the checklist check runs
    Then it fails with "tag without evidence: Nebius AI Cloud"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-138 The source, conflicts and Built-with sections are complete
    Given the committed checklist
    When the source, conflicts and Built-with sections are checked
    Then every one passes and the Tags line equals the count
  @negative
  Scenario: AJ-138 A rules section without the video length item is rejected
    Given a copy of the checklist without the line "- Video length limit"
    When the checklist check runs
    Then it fails with "rule item missing: video length limit"
  @negative
  Scenario: AJ-138 A conflict without a resolution is rejected
    Given a copy of the checklist whose first conflict has no resolution
    When the checklist check runs
    Then it fails with "conflict without resolution"
