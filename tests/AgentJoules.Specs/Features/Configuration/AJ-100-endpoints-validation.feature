@unit @AJ-S100 @REQ-018
Feature: Validate configured model IDs against docs/endpoints.md
  @positive
  Scenario: AJ-100 every configured ID is recorded
    Given a docs/endpoints.md with sections local, nvidia and nebius, each with a Verified date and its model lines
    And a configuration whose tier and agent model IDs all appear in those sections
    When I run "validate --config"
    Then the exit code is 0
    And the output contains "Validated" and "model IDs"
  @positive
  Scenario: AJ-100 the judge ID is looked up in the judge tier section
    Given Judge:Tier is "nvidia" and Judge:ModelId is listed only under "## nvidia"
    When I run "validate --config"
    Then the exit code is 0
  @positive
  Scenario: AJ-100 a disabled tier is skipped
    Given Tiers:cloudgpu:Enabled is false and "## cloudgpu" has no Verified line
    When I run "validate --config"
    Then the exit code is 0
  @negative
  Scenario: AJ-100 an absent ID exits 4 without network access
    Given the configuration names a model ID that is not listed in its section
    When I run "validate --config"
    Then the exit code is 4
    And the output names the missing ID and its section
    And the fake HTTP handler recorded 0 requests
  @negative
  Scenario: AJ-100 a tag differing only by case exits 4
    Given the section lists "nemotron-3-nano:30b" and the configuration names "nemotron-3-nano:30B"
    When I run "validate --config"
    Then the exit code is 4
  @negative
  Scenario: AJ-100 an enabled tier without a Verified date exits 4
    Given the "## nebius" section has model lines but no Verified line
    When I run "validate --config"
    Then the exit code is 4
    And the output contains "Verified date missing in section ## nebius"
  @negative
  Scenario: AJ-100 a judge ID listed only under another tier exits 4
    Given Judge:Tier is "nvidia" and the judge ID is listed only under "## nebius"
    When I run "validate --config"
    Then the exit code is 4
