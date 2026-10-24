@unit @AJ-S082 @REQ-016
Feature: AJ-082 Aggregate repeats into report rows without merging tiers
  @positive
  Scenario: AJ-082 three repeats give median, range and n
    Given 3 repeats of net joules 10, 12 and 14 for one agent
    When the rows are aggregated
    Then the cell shows median 12, range 10 to 14 and n 3
  @positive
  Scenario: AJ-082 a flag on one repeat is shown on the aggregate cell
    Given 3 repeats of which one has flag "low-resolution"
    When the rows are aggregated
    Then the aggregate cell carries "low-resolution"
  @negative
  Scenario: AJ-082 all-N/A repeats stay N/A and are not averaged as zero
    Given 3 hosted repeats whose net joules are N/A
    When the rows are aggregated
    Then the cell has label "N/A" and a null value
  @negative
  Scenario: AJ-082 local and cloudgpu rows are not merged
    Given runs for tiers "local" and "cloudgpu" with the same model ID
    When the rows are aggregated
    Then the result has separate rows for "local" and "cloudgpu" and no combined row
  @negative
  Scenario: AJ-082 no cross-tier ratio is produced
    Given runs for tiers "local" and "nvidia"
    When the rows are aggregated
    Then the report model contains no cross-tier ratio or ranking field
