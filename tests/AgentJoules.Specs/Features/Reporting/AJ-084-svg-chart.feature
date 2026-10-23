@unit @AJ-S084 @REQ-016
Feature: AJ-084 Draw the SVG chart with N/A as a text marker
  @positive
  Scenario: AJ-084 measured value is drawn as a bar with a label
    Given a fixture local step with median net joules 12.3
    When the chart is rendered
    Then the SVG has one bar for that step and the text "12.3"
  @positive
  Scenario: AJ-084 chart is well-formed XML with the fixed tier order
    Given a fixture with all four tiers
    When the chart is rendered
    Then the output parses as XML
    And the tier slots appear in the order local, cloudgpu, nvidia, nebius
  @positive
  Scenario: AJ-084 negative net value is drawn below the axis
    Given a fixture local step with net joules -2.0 flagged "below-baseline"
    When the chart is rendered
    Then the bar extends below the axis line
  @negative
  Scenario: AJ-084 N/A step has a text marker and no bar
    Given a fixture hosted step whose net joules are N/A
    When the chart is rendered
    Then the SVG has a text element "N/A" for that step
    And the SVG has no rect element for that step
  @negative
  Scenario: AJ-084 tier without a run file is labelled not run
    Given a fixture with no cloudgpu run
    When the chart is rendered
    Then the cloudgpu slot has the text "not run" and no bar
  @negative
  Scenario: AJ-084 hostile model ID is escaped
    Given a model ID containing "&" and "<"
    When the chart is rendered
    Then the output still parses as XML
