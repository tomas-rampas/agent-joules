@unit @AJ-S040 @REQ-008
Feature: AJ-040 Define the per-call reading and publish it to a metrics sink
  @positive
  Scenario: AJ-040 sink returns readings in publish order
    Given an in-memory metrics sink
    When readings for agents "Planner", "Researcher" and "Writer" are published in that order
    Then the snapshot lists "Planner", "Researcher" and "Writer" in that order
  @positive
  Scenario: AJ-040 hosted tier reading labels energy as N/A
    Given a reading for tier "nvidia" built with the unmetered energy meter
    When the reading is published
    Then grossJoules, netJoules, avgW and peakW each have label "N/A", reason "hosted-tier" and a null value
  @negative
  Scenario: AJ-040 numeric value under an N/A label is rejected
    Given a metric factory
    When a metric with label "N/A" and value 0 is requested
    Then construction fails and no metric is created
  @negative
  Scenario: AJ-040 judge reading never reaches the workflow sink
    Given a workflow sink and a separate judge sink
    When a reading for agent "Judge" is published to the judge sink
    Then the workflow sink snapshot is empty
