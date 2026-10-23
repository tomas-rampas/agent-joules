@unit @AJ-S087 @REQ-016
Feature: AJ-087 Render the README results block as a deterministic function
  @positive
  Scenario: AJ-087 block has one row per tier and a not-run cloudgpu row
    Given latest-run fixtures for local, nvidia and nebius only
    When the block is rendered
    Then the block has rows for "local", "nvidia" and "nebius"
    And it has the row "cloudgpu | not run"
  @positive
  Scenario: AJ-087 two renders are identical
    Given the same latest-run fixtures
    When the block is rendered twice
    Then the two outputs are byte-identical
  @positive
  Scenario: AJ-087 judge disclosure is present and names self-judging
    Given fixtures where run.selfJudged is true
    When the block is rendered
    Then the block names the judge model, says judge calls are excluded from workflow totals and discloses self-judging
  @negative
  Scenario: AJ-087 hosted energy is N/A in the README block
    Given a nvidia fixture
    When the block is rendered
    Then its energy cells contain "N/A" and not "0"
  @negative
  Scenario: AJ-087 partial and sampling runs are labelled
    Given a cloudgpu fixture with status "partial" and method "sampling"
    When the block is rendered
    Then the cloudgpu row contains "partial" and "sampling"
