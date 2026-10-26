@unit @AJ-S141 @REQ-028
Feature: AJ-141 Devpost pitch rule
  @positive
  Scenario: AJ-141 A cloudgpu file naming an L40S keeps candidate A
    Given a workspace with a completed cloudgpu run file whose gpuName is "NVIDIA L40S"
    When the pitch rule is applied
    Then the chosen pitch is candidate A and is 197 characters
    And the checklist block equals candidate A byte for byte
  @positive
  Scenario: AJ-141 No cloudgpu file selects A-local
    Given a workspace with no completed or partial cloudgpu run file
    When the pitch rule is applied
    Then the chosen pitch is A-local and is 192 characters
    And it contains neither "Nebius AI Cloud" nor "L40S"
  @positive
  Scenario: AJ-141 A cloudgpu file naming another GPU swaps the GPU token
    Given a workspace with a completed cloudgpu run file whose gpuName is "NVIDIA H100 80GB HBM3"
    When the pitch rule is applied
    Then candidate A has "L40S" replaced by "H100" and is 200 characters or fewer
    And docs/build-log.md has a "Pitch change:" entry
  @negative
  Scenario: AJ-141 A pitch over 200 characters is rejected
    Given a pitch candidate of 201 characters
    When the pitch check runs
    Then it fails with "201 characters exceed 200"
  @negative
  Scenario: AJ-141 A-local used although a cloudgpu file exists is rejected
    Given a workspace with a completed cloudgpu run file and a checklist block equal to A-local
    When the pitch check runs
    Then it fails with "A-local chosen but cloudgpu run exists"
  @negative
  Scenario: AJ-141 Candidate A used without a cloudgpu file is rejected
    Given a workspace with no cloudgpu run file and a checklist block equal to candidate A
    When the pitch check runs
    Then it fails with "candidate A chosen without cloudgpu run"
  @negative
  Scenario: AJ-141 A checklist block that differs from the pitch file is rejected
    Given a checklist pitch block that differs from candidate A by one character
    When the pitch check runs
    Then it fails with "pitch block differs from docs/devpost-pitch.md"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-141 The committed checklist block equals candidate A of the committed pitch file
    Given the committed checklist, the committed pitch file and the committed run files
    When the pitch check runs
    Then the block equals the chosen candidate, the Characters line equals the count and the tagline row follows the rule
