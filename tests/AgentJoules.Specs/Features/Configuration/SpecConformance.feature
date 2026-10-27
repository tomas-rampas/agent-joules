@unit @AJ-S154 @DoD
Feature: AJ-154 Spec conformance evidence
  @positive
  Scenario: AJ-154 The acceptance matrix has a row for every requirement and edge case
    Given the committed docs/build-log.md
    When the Acceptance matrix is parsed
    Then it has 51 rows with no empty status
  @positive
  Scenario: AJ-154 The Spec filter table covers the 36 unit-test IDs
    Given the committed docs/build-log.md
    When the Spec filter results table is parsed
    Then it has 36 rows each with passed 1 or more and failed 0
  @positive
  Scenario: AJ-154 No out-of-scope package is referenced
    Given the committed Directory.Packages.props
    When it is searched for alpha, preview, charting and Semantic Kernel packages
    Then nothing is found
  @negative
  Scenario: AJ-154 A matrix with a missing row is rejected
    Given a build log whose Acceptance matrix lacks EDGE-014
    When the matrix check runs
    Then it fails with "missing row: EDGE-014"
  @negative
  Scenario: AJ-154 A Spec filter row with a failed test is rejected
    Given a filter table row "REQ-009 | passed 3 | failed 1"
    When the matrix check runs
    Then it fails with "REQ-009 has failed tests"
  @negative
  Scenario: AJ-154 An alpha package is rejected
    Given a Directory.Packages.props that references Microsoft.Agents.AI.AgentHooks
    When the scope check runs
    Then it fails with "out-of-scope package: Microsoft.Agents.AI.AgentHooks"
  @negative
  Scenario: AJ-154 A stretch commit before all boxes are ticked is rejected
    Given a git history with a commit "STRETCH: energy budget" and one unchecked box
    When the scope check runs
    Then it fails with "stretch started before the Definition of Done was complete"
  @negative
  Scenario: AJ-154 An UNMET row without a reason is rejected
    Given a matrix row "REQ-031 | ... | UNMET" with an empty reason
    When the matrix check runs
    Then it fails with "UNMET without reason: REQ-031"
