@unit @AJ-S129 @REQ-031 @EDGE-017
Feature: Apply the partial-tier consequences to the report, README and Devpost texts
  @positive
  Scenario: AJ-129 a completed cloudgpu file keeps candidate A
    Given a committed cloudgpu run file with status "completed" and gpuName containing "L40S"
    When the tagline rule is evaluated
    Then the tagline is candidate A and no change is required
  @positive
  Scenario: AJ-129 a partial file keeps candidate A and the row says partial
    Given a committed cloudgpu run file with status "partial"
    When the README block is rendered
    Then the cloudgpu row contains "partial"
    And the tagline rule keeps candidate A
  @positive
  Scenario: AJ-129 a different GPU name replaces the name in candidate A
    Given the committed cloudgpu file names a GPU other than the L40S
    When the tagline rule is evaluated
    Then candidate A uses the file's gpuName
    And docs/build-log.md contains an entry for the change
  @negative
  Scenario: AJ-129 no usable cloudgpu file selects candidate A-local
    Given no cloudgpu file with status "completed" or "partial" is committed
    When the tagline rule is evaluated
    Then the tagline is candidate A-local
    And the README block renders "cloudgpu | not run"
  @negative
  Scenario: AJ-129 an incomplete file counts as absent
    Given the only cloudgpu file has status "incomplete"
    When the tagline rule is evaluated
    Then the tier state is absent and candidate A-local is selected
  @negative
  Scenario: AJ-129 a story sentence changed without a log entry fails
    Given docs/devpost-story.md differs from its pre-drafted sentence and docs/build-log.md has no matching entry
    When the consequence check runs
    Then the check fails and names the sentence
  @negative
  Scenario: AJ-129 laptop and cloud rows must stay separate
    Given a report fixture with a laptop local file and a cloudgpu file
    When the report is rendered
    Then the summary has separate rows and contains no merged or averaged figure
  @positive
  Scenario: AJ-129 a sampling-method cloudgpu row names the method
    Given a completed cloudgpu file with method "sampling"
    When the README block is rendered
    Then the cloudgpu row contains "sampling"
