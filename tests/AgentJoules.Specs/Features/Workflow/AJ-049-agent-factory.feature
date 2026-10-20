@unit @AJ-S049 @REQ-011
Feature: AJ-049 Create one metered agent per name from a tier and model
  @positive
  Scenario: AJ-049 four agents have four distinct metered clients
    Given the agent factory with fake provider clients
    When agents "Planner", "Researcher", "Writer" and "Reviewer" are created for one task and repeat
    Then there are 4 distinct metered client instances
  @positive
  Scenario: AJ-049 each reading carries the name of the owning agent
    Given the four agents created over fake chat clients
    When each agent answers one prompt
    Then the sink holds 4 readings and each reading agent name equals the agent that produced it
  @positive
  Scenario: AJ-049 chat options use temperature 0 and the task limit
    Given the task "incident-brief" with maxOutputTokens 512
    When an agent is created
    Then its chat options have temperature 0 and max output tokens 512
  @negative
  Scenario: AJ-049 an agent name outside the catalog is rejected
    Given the agent factory
    When an agent named "Judgey" is requested from the catalog
    Then creation fails with a configuration error
  @negative
  Scenario: AJ-049 a new repeat does not reuse the previous retry ledger
    Given agents created for repeat 1 whose Writer recorded 2 retries
    When agents are created for repeat 2
    Then the new Writer client starts with 0 retries
