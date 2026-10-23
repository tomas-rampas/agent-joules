@unit @AJ-S083 @REQ-016
Feature: AJ-083 Render the Markdown summary with honest labels
  @positive
  Scenario: AJ-083 header carries the context and the GPU-board sentence
    Given a fixture with a local run and a nvidia run
    When the summary is rendered
    Then the header lists GPU name, driver, run date, runtime, model IDs and precision
    And the text contains "Energy is GPU-board energy only."
  @positive
  Scenario: AJ-083 tier without a run file is shown as not run
    Given a fixture with only a nvidia run
    When the summary is rendered
    Then the table has the rows "local | not run", "cloudgpu | not run" and "nebius | not run"
  @positive
  Scenario: AJ-083 concurrent run adds the overlap sentence
    Given a fixture whose run.mode is "concurrent"
    When the summary is rendered
    Then the text contains "Per-step energy overlaps in concurrent mode and does not sum to the run total."
  @negative
  Scenario: AJ-083 usage-free row shows not-reported and N/A
    Given a fixture row whose response had no usage
    When the summary is rendered
    Then both token cells contain "not-reported" and the cost cell contains "N/A"
  @negative
  Scenario: AJ-083 hosted energy is N/A and never 0
    Given a fixture hosted row
    When the summary is rendered
    Then the gross J, net J, avg W and peak W cells contain "N/A"
    And no energy cell contains "0.0"
  @negative
  Scenario: AJ-083 differing models produce a disclosure and no efficiency claim
    Given runs whose tiers use different model IDs
    When the summary is rendered
    Then the header contains "Models differ across tiers"
    And the text has no cross-tier ratio
  @negative
  Scenario: AJ-083 low-resolution flag stays visible
    Given a fixture row with flag "low-resolution"
    When the summary is rendered
    Then the row contains "low-resolution" next to the value
