@unit @AJ-S142 @REQ-028
Feature: AJ-142 Devpost feedback answers
  @positive
  Scenario: AJ-142 Builder placeholders are gone and user placeholders are untouched
    Given the committed docs/devpost-feedback.md and its pre-fill version
    When the owner markers are counted and the user lines compared
    Then "owner: builder" occurs 0 times and "owner: user" occurs 7 times
    And the seven user lines are byte-identical to the pre-fill file
  @positive
  Scenario: AJ-142 The checklist lists the seven user placeholders
    Given the committed docs/devpost-checklist.md
    When the heading "Feedback form, user fills" is read
    Then it has seven bullets naming Q2, Q4, Q5, Q6, Q7, Q8 and Q9 and docs/devpost-feedback.md
  @positive
  Scenario: AJ-142 Filled passages name sources and labels
    Given the eight filled passages
    When each is checked
    Then each names an existing file and a label word or an explicit not-run statement
  @positive
  Scenario: AJ-142 Without a baseline file the baseline passage says so
    Given a workspace with no baseline run file
    When the Q4 baseline passage is checked
    Then it reads "No Qwen3 8B baseline run was made."
  @negative
  Scenario: AJ-142 A leftover builder placeholder is rejected
    Given a copy of the feedback file with one "owner: builder" placeholder left
    When the feedback check runs
    Then it fails with "builder placeholder left: 1"
  @negative
  Scenario: AJ-142 A user placeholder filled by the builder is rejected
    Given a copy of the feedback file where the Q6 user placeholder is replaced by "9"
    When the feedback check runs
    Then it fails with "user placeholder modified: Q6"
  @negative
  Scenario: AJ-142 A missing checklist heading is rejected
    Given a copy of the checklist without "Feedback form, user fills"
    When the feedback check runs
    Then it fails with "missing heading: Feedback form, user fills"
  @negative
  Scenario: AJ-142 An invented credits answer is rejected
    Given a feedback file claiming the credits covered all runs and no user statement in docs/build-log.md
    When the feedback check runs
    Then it fails with "credits answer without a user statement"
  @negative
  Scenario: AJ-142 A passage with a number and no source is rejected
    Given a copy of the Q7 passage with "latency 842 ms" and no results file
    When the feedback check runs
    Then it fails with "number without source: 842 ms"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-142 The Fill checklist table, the credits answer, the design changes and the comparisons follow their rules
    Given the committed docs/devpost-feedback.md and its pre-fill version
    When every feedback check runs on the committed files
    Then every one passes
  @negative
  Scenario: AJ-142 An invented credits answer is rejected against the committed build log
    Given a feedback file claiming the credits covered all runs and the committed docs/build-log.md
    When the feedback check runs
    Then it fails with "credits answer without a user statement"
  @negative
  Scenario: AJ-142 A credits marker quoted mid-sentence in prose is not a user statement
    Given a feedback file claiming the credits covered all runs and the committed docs/build-log.md
    And a build log with a prose line quoting the credits marker mid-sentence
    When the feedback check runs
    Then it fails with "credits answer without a user statement"
  @positive
  Scenario: AJ-142 A credits list item is recognised as a user statement
    Given a feedback file claiming the credits covered all runs and the committed docs/build-log.md
    And the Q5 credits claim names its source and label
    And a build log holding the user statement on credits list item
    When the feedback check runs
    Then no failure names "credits answer without a user statement"
