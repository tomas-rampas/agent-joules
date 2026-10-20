@unit @AJ-S048 @REQ-012
Feature: AJ-048 Load the fixed task set from tasks/
  @positive
  Scenario: AJ-048 committed tasks load in file-name order
    Given the committed tasks folder
    When the task set is loaded
    Then the names are "incident-brief", "api-migration" and "energy-explainer" in that order
  @positive
  Scenario: AJ-048 a single task is selected by name
    Given the committed tasks folder
    When the task "energy-explainer" is requested
    Then exactly 1 task with maxOutputTokens 384 is returned
  @negative
  Scenario: AJ-048 missing maxOutputTokens exits 2 naming the file
    Given a tasks folder where "02-api-migration.json" has no maxOutputTokens
    When the task set is loaded
    Then the exit code is 2
    And the output names "02-api-migration.json"
  @negative
  Scenario: AJ-048 unknown task name exits 2 and lists the available names
    Given the committed tasks folder
    When the task "bogus" is requested
    Then the exit code is 2
    And the output lists "incident-brief", "api-migration" and "energy-explainer"
  @negative
  Scenario: AJ-048 duplicate task names exit 2
    Given a tasks folder with two files whose name is "same"
    When the task set is loaded
    Then the exit code is 2
