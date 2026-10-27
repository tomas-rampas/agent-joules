@unit @AJ-S156 @DoD
Feature: AJ-156 Git history and push boundary
  @positive
  Scenario: AJ-156 Every build commit starts with a requirement ID
    Given a temporary repository with a recorded build-start and commits "AJ-006 REQ-001: scaffold", "AJ-045 EDGE-006: retry on 429" and "AJ-155: format"
    When the history check runs
    Then it passes
  @positive
  Scenario: AJ-156 Nothing is pushed when origin main equals the recorded base
    Given a temporary repository with origin/main at the recorded base and three local commits
    When the push-boundary check runs
    Then it reports 3 commits ahead and passes
  @positive
  Scenario: AJ-156 The checklist holds the git push command
    Given the committed docs/devpost-checklist.md
    When it is searched for "git push"
    Then at least one fenced block contains it
  @negative
  Scenario: AJ-156 A commit without a requirement ID is rejected
    Given a temporary repository with a commit "tidy readme"
    When the history check runs
    Then it fails with "commit without story id: tidy readme"
  @negative
  Scenario: AJ-156 A pushed history is rejected
    Given a temporary repository where origin/main equals HEAD
    When the push-boundary check runs
    Then it fails with "origin/main moved: the builder pushed"
  @negative
  Scenario: AJ-156 An uncommitted change blocks the review gates
    Given a temporary repository with a modified tracked file
    When the gate precondition check runs
    Then it fails with "uncommitted changes"
  @negative
  Scenario: AJ-156 A checklist without the push command is rejected
    Given a copy of the checklist without "git push"
    When the push-boundary check runs
    Then it fails with "missing git push block"
