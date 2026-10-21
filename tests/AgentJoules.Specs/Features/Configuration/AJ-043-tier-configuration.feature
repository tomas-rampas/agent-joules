@unit @AJ-S043 @REQ-012
Feature: AJ-043 Bind and validate tier configuration
  @positive
  Scenario: AJ-043 four tier sections bind from the committed file
    Given the committed config/appsettings.json
    When the options are bound and validated
    Then tiers "local", "cloudgpu", "nvidia" and "nebius" each have a BaseUrl and a ModelId
    And tier "cloudgpu" is disabled
  @positive
  Scenario: AJ-043 environment variable overrides the file
    Given the committed config with nvidia ModelId "model-a"
    When AGENTJOULES__Tiers__nvidia__ModelId is set to "model-b" in the configuration override
    Then the bound nvidia ModelId is "model-b"
  @negative
  Scenario: AJ-043 invalid reasoning mode exits 2
    Given the config with Tiers:nvidia:ReasoningMode set to "maybe"
    When any command starts
    Then the exit code is 2
    And the output names "Tiers:nvidia:ReasoningMode"
  @negative
  Scenario: AJ-043 enabled tier without base URL exits 2
    Given the config with Tiers:nebius:BaseUrl removed
    When any command starts
    Then the exit code is 2
    And the output names "Tiers:nebius:BaseUrl"
  @negative
  Scenario: AJ-043 judge tier naming an unknown tier exits 2
    Given the config with Judge:Tier set to "bogus"
    When any command starts
    Then the exit code is 2
