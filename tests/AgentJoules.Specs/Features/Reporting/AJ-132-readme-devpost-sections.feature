@unit @AJ-S132 @REQ-025
Feature: AJ-132 README sections required by the Devpost form
  @positive
  Scenario: AJ-132 Required headings and sentences are present
    Given the committed README.md and the latest local results file
    When the Devpost sections are extracted
    Then the four required headings occur once each
    And the sentence "Local numbers were produced with" names the runtime and tag of the results file
  @positive
  Scenario: AJ-132 Model IDs in the README exist in the endpoints file
    Given every model ID named in README.md
    When they are compared with the "- model:" lines of docs/endpoints.md
    Then each ID is listed and "validate --config" exits 0
  @positive
  Scenario: AJ-132 Cloud section without a cloudgpu run uses the fixed sentence
    Given a workspace with no completed cloudgpu run file
    When the README "Other Nebius tools or services used" section is checked
    Then it reads "None besides Token Factory." and the README labels the cloudgpu tier "not run"
  @negative
  Scenario: AJ-132 Missing required heading is rejected
    Given a copy of the README without "Where Nebius Token Factory accelerated the workflow"
    When the README structure check runs
    Then it fails with "missing heading: Where Nebius Token Factory accelerated the workflow"
  @negative
  Scenario: AJ-132 Model ID absent from the endpoints file is rejected
    Given a copy of the README naming the model ID "nemotron-invented"
    When the model-ID check runs
    Then it fails with "model id not in docs/endpoints.md: nemotron-invented"
  @negative
  Scenario: AJ-132 Cloud cost without a source file is rejected
    Given a copy of the README stating an observed cost with no "docs/cloudgpu-run.md" reference
    When the source-line check runs
    Then it fails with "number without source: observed cost"
  @negative
  Scenario: AJ-132 Leftover placeholder text is rejected
    Given a copy of the README that still contains "Filled during the build"
    When the README structure check runs
    Then it fails with "leftover placeholder text"
  # The scenarios below are additions of the test side for criteria 2, 4, 5 and 6 that the story text does not spell out.
  @positive
  Scenario: AJ-132 The committed README passes the structure check
    Given the committed README.md and the latest local results file
    When the README structure check runs
    Then the check passes
  @positive
  Scenario: AJ-132 Results files named in the README exist
    Given the committed README.md and the latest local results file
    When the results files named in the README are looked up
    Then each exists under results
  @positive
  Scenario: AJ-132 The cloud section matches the committed cloudgpu run
    Given the committed README.md and the latest local results file
    When the README "Other Nebius tools or services used" section is checked against the cloudgpu run file
    Then the section names docs/cloudgpu-run.md, the preset, the region and the engine of that file
  @positive
  Scenario: AJ-132 The methodology summary links the document and discloses the judge
    Given the committed README.md and the latest local results file
    When the methodology summary is checked
    Then it links docs/methodology.md, names the judge model, excludes judge calls from the totals and discloses self-judging when the latest file says so
  @positive
  Scenario: AJ-132 The NIM-local outcome links the attempt document
    Given the committed README.md and the latest local results file
    When the NIM-local outcome is checked
    Then the heading "NIM-local outcome" occurs once and docs/nim-local-attempt.md is linked
  @negative
  Scenario: AJ-132 A cloud figure absent from the run record is rejected
    Given a copy of the README whose cloud section states 9.999 GPU-hours
    When the cloud figures check runs
    Then it fails with "cloud figure not in docs/cloudgpu-run.md: 9.999"
  @negative
  Scenario: AJ-132 A self-judged results file that the README does not name is rejected
    Given a copy of the README whose self-judging disclosure does not name the self-judged results file
    When the methodology summary is checked
    Then it fails for each self-judged results file with the self-judging disclosure failure
  @negative
  Scenario: AJ-132 A plain-text model tag absent from the endpoints file is rejected
    Given a copy of the README naming the plain-text model tag "nemotron-invented:7b"
    When the model-ID check runs
    Then it fails with "model id not in docs/endpoints.md: nemotron-invented:7b"
