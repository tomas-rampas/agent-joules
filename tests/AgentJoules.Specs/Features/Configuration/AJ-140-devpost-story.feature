@unit @AJ-S140 @REQ-028
Feature: AJ-140 Devpost story fill
  @positive
  Scenario: AJ-140 All nine placeholders are replaced and the checklist is gone
    Given the committed docs/devpost-story.md
    When placeholders and headings are counted
    Then "[[FILL:" occurs 0 times and no heading contains "Fill-after-build"
  @positive
  Scenario: AJ-140 Each filled passage names its source file and a label
    Given the nine filled passages
    When each is checked
    Then each names an existing results file or document, a unit and one label word
  @positive
  Scenario: AJ-140 Unchanged sentences are byte-identical to the pre-fill story
    Given the pre-fill story and the filled story
    When the lines outside the nine passages are diffed
    Then every difference has a "Story change:" entry in docs/build-log.md
  @positive
  Scenario: AJ-140 Without a cloudgpu run the story drops its cloudgpu claims
    Given a workspace with no completed or partial cloudgpu run file
    When the story is checked
    Then the cloud passage says "The cloudgpu tier did not run in this submission"
    And no sentence claims a measured Nebius AI Cloud or L40S figure
  @negative
  Scenario: AJ-140 A leftover placeholder is rejected
    Given a copy of the story with one "[[FILL: ... ]]" left
    When the story check runs
    Then it fails with "leftover placeholder: 1"
  @negative
  Scenario: AJ-140 A number without a source is rejected
    Given a copy of the story where the local median is written as "212 J" with no results file
    When the story check runs
    Then it fails with "number without source: 212 J"
  @negative
  Scenario: AJ-140 An unlogged change to an existing sentence is rejected
    Given a copy of the story where the sentence "Energy here means GPU-board energy, not CPU, system memory or wall power." is edited
    When the diff check runs without a build-log entry
    Then it fails with "unlogged story change"
  @negative
  Scenario: AJ-140 A leftover Fill-after-build section is rejected
    Given a copy of the story that still has the heading "Fill-after-build checklist"
    When the story check runs
    Then it fails with "checklist section not deleted"
  @negative
  Scenario: AJ-140 A merged laptop and cloud figure is rejected
    Given a copy of the story with "the average of the laptop and the cloud instance"
    When the story check runs
    Then it fails with "merged tiers"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-140 The ending, the comparison and calibration passages and the checklist row follow their rules
    Given the committed docs/devpost-story.md
    When the ending, the comparison passage, the calibration passage and the checklist row are checked
    Then every one passes
  @positive
  Scenario: AJ-140 The agent names equal the distinct agents of the latest local run file
    Given the committed docs/devpost-story.md
    When the agent names are compared with the latest local run file
    Then every one passes
  @positive
  Scenario: AJ-140 The first passages have their sentence counts and passage 5 names its file
    Given the committed docs/devpost-story.md
    When the sentence counts and the passage 5 file are checked
    Then every one passes
  @negative
  Scenario: AJ-140 An agent name that the run file does not hold is rejected
    Given a copy of the story that names an agent "Summarizer" that the run file does not hold
    When the agent names are compared with the latest local run file
    Then it fails with "agent names differ from the run file: Summarizer"
  @negative
  Scenario: AJ-140 A second sentence in passage 2 is rejected
    Given a copy of the story where passage 2 holds one more sentence
    When the sentence counts and the passage 5 file are checked
    Then it fails with "passage 2 has 2 sentences"
  @negative
  Scenario: AJ-140 A passage 5 without a file in parentheses is rejected
    Given a copy of the story where passage 5 names no file in parentheses
    When the sentence counts and the passage 5 file are checked
    Then it fails with "passage 5 names no file in parentheses"
  @negative
  Scenario: AJ-140 An edit after the first 40 characters of a logged sentence is rejected
    Given a copy of the story where the sentence "Energy here means GPU-board energy, not CPU, system memory or wall power." is edited
    And a build log whose only entry records only the unedited sentence
    When the diff check runs against that build log
    Then it fails with "unlogged story change"
  @negative
  Scenario: AJ-140 One joule figure for the laptop and the cloud together is rejected
    Given a copy of the story with "3802.15 J together on the laptop and the cloud instance"
    When the story check runs
    Then it fails with "merged tiers"
