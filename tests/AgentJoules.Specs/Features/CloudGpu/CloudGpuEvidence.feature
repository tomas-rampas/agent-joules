@unit @AJ-S151 @DoD
Feature: AJ-151 cloudgpu evidence
  @positive
  Scenario: AJ-151 A completed cloudgpu file with FP8 on a named preset is accepted
    Given a results folder with a completed cloudgpu file whose provider is "nebius-ai-cloud" and precision "FP8"
    When the cloudgpu evidence check runs
    Then it passes and records the preset
  @positive
  Scenario: AJ-151 The run record has six headings and a cost within the cap
    Given the committed docs/cloudgpu-run.md
    When its sections are parsed
    Then it has Instance, Commands, Probe, Runs, Cost and Teardown
    And the Cost section holds a USD value of 30 or less and the Teardown section holds "deleted"
  @positive
  Scenario: AJ-151 The README results block names cloudgpu
    Given the committed README.md
    When the results block is read
    Then it contains "cloudgpu"
  @negative
  Scenario: AJ-151 A cost above the cap is rejected
    Given a cloudgpu-run.md whose Cost section states "USD 31.20"
    When the cloudgpu evidence check runs
    Then it fails with "cost 31.20 exceeds 30"
  @negative
  Scenario: AJ-151 A missing deletion confirmation is rejected
    Given a cloudgpu-run.md whose Teardown section lacks the word "deleted"
    When the cloudgpu evidence check runs
    Then it fails with "teardown not confirmed"
  @negative
  Scenario: AJ-151 An incomplete cloudgpu file does not count as evidence
    Given a results folder whose only cloudgpu file has run.status "incomplete"
    When the cloudgpu evidence check runs
    Then it fails with "no completed or partial cloudgpu run file"
  @negative
  Scenario: AJ-151 A probe with MIG enabled is rejected
    Given a cloudgpu probe fixture with migMode "enabled"
    When the cloudgpu evidence check runs
    Then it fails with "MIG enabled: measurement invalid"
  @negative
  Scenario: AJ-151 A missing run-record heading is rejected
    Given a cloudgpu-run.md without the heading "Teardown"
    When the cloudgpu evidence check runs
    Then it fails with "missing heading: Teardown"
