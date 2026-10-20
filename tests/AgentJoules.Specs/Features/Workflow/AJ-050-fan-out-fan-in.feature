@unit @AJ-S050 @REQ-011
Feature: AJ-050 Run Planner, fan-out Researcher and Writer, fan-in Reviewer as a workflow
  @positive
  Scenario: AJ-050 workflow produces one reading per named agent
    Given the workflow factory over four fake chat clients
    When the workflow runs the task "energy-explainer"
    Then the sink holds readings for "Planner", "Researcher", "Writer" and "Reviewer"
    And each reading agent name matches the agent owning that client
  @positive
  Scenario: AJ-050 reviewer waits for both branches
    Given serialized mode and fake clients whose Researcher takes 1 second and Writer takes 2 seconds
    When the workflow runs
    Then the Reviewer window starts after the Writer window ends
  @positive
  Scenario: AJ-050 every step streams and records time to first token
    Given fake streaming clients
    When the workflow runs
    Then every reading has a measured ttftMs
  @negative
  Scenario: AJ-050 a failing researcher stops the workflow before the reviewer
    Given a Researcher client whose endpoint answers 500
    When the workflow runs
    Then StepFailedException is raised
    And the sink holds an "error" reading for "Researcher" and no reading for "Reviewer"
  @negative
  Scenario: AJ-050 a failing planner stops both branches
    Given a Planner client whose endpoint answers 500
    When the workflow runs
    Then the sink holds one reading for "Planner" with outcome "error"
    And no reading for "Researcher" or "Writer"
