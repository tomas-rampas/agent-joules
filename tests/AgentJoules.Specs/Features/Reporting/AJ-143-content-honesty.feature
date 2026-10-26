@unit @AJ-S143 @REQ-025 @REQ-026 @REQ-027 @REQ-028
Feature: AJ-143 Content honesty audit
  @positive
  Scenario: AJ-143 The committed content set passes every rule
    Given the audited files and the committed results files
    When the honesty audit runs
    Then it reports 0 findings for the rules source, label, zero, scope, ranking, format and cloudgpu
  @positive
  Scenario: AJ-143 A cloudgpu-free submission passes the consistency rule
    Given a workspace with no cloudgpu run file and content updated by the fallback paths
    When the honesty audit runs
    Then it reports 0 findings
  @negative
  Scenario: AJ-143 A number without a source is a finding
    Given a copy of the article with "The Planner used 40 J."
    When the honesty audit runs
    Then a finding reads "number without source: 40 J"
  @negative
  Scenario: AJ-143 A hosted energy value written as zero is a finding
    Given a copy of the README with "nvidia energy 0 J"
    When the honesty audit runs
    Then a finding reads "hidden zero: nvidia energy 0 J"
  @negative
  Scenario: AJ-143 A ranking claim is a finding
    Given a copy of a post saying "local beats the cloud on energy"
    When the honesty audit runs
    Then a finding reads "ranking claim"
  @negative
  Scenario: AJ-143 A key-shaped string is a finding
    Given a copy of a file containing a runtime-built "nvapi-" plus 16 characters value
    When the honesty audit runs
    Then a finding reads "key material"
  @negative
  Scenario: AJ-143 An em-dash is a finding
    Given a copy of a file containing the character U+2014
    When the honesty audit runs
    Then a finding reads "non-ASCII character"
  @negative
  Scenario: AJ-143 A cloudgpu claim without a run file is a finding
    Given a workspace with no cloudgpu run file and a story sentence "the Nebius AI Cloud L40S used 90 J"
    When the honesty audit runs
    Then a finding reads "cloudgpu claim without run file"
  @negative
  Scenario: AJ-143 A post variant without the GPU-board sentence is a finding
    Given a copy of the posts where the hackathon variant lacks "GPU-board energy"
    When the honesty audit runs
    Then a finding reads "scope sentence missing: hackathon"
  @negative
  Scenario: AJ-143 A quantity in the rendered posts without a Sources line is a finding
    Given a copy of the posts without the Sources line and with "The Planner used 40 J."
    When the honesty audit runs
    Then a finding reads "number without source: 40 J"
  @negative
  Scenario: AJ-143 A results file in a neighbouring paragraph does not source a quantity
    Given a copy of the article with a results file in one paragraph and "The Planner used 40 J (measured)." in the next
    When the honesty audit runs
    Then a finding reads "number without source: 40 J"
  @negative
  Scenario: AJ-143 A labelled energy value under an option mention is a finding
    Given a copy of the README with "With `--repeat 3` the local tier used 33716.77 J (measured)."
    When the honesty audit runs
    Then a finding reads "number without source: 33716.77 J"
  @negative
  Scenario: AJ-143 A sourced quantity without a label is a finding
    Given a copy of the article with a sourced quantity "40 J" and no label
    When the honesty audit runs
    Then a finding reads "finding: no label: 40 J"
  @negative
  Scenario: AJ-143 L40S outside a planned-or-replaced sentence is a finding
    Given a workspace where the cloudgpu run file names an H100 and a story sentence "The L40S board drew the power."
    When the honesty audit runs
    Then a finding reads "L40S outside a planned-or-replaced sentence"
  @negative
  Scenario: AJ-143 A cloudgpu sentence naming another GPU is a finding
    Given a workspace where the cloudgpu run file names an H100 and a story sentence "The cloudgpu tier ran on an A100 board."
    When the honesty audit runs
    Then a finding reads "cloudgpu measurement on A100"
  @negative
  Scenario: AJ-143 A README without the browser statement is a finding
    Given a copy of "README.md" without the browser statement
    When the honesty audit runs
    Then a finding reads "browser statement missing: README.md"
  @negative
  Scenario: AJ-143 A methodology document without the browser statement is a finding
    Given a copy of "docs/methodology.md" without the browser statement
    When the honesty audit runs
    Then a finding reads "browser statement missing: docs/methodology.md"
  @negative
  Scenario Outline: AJ-143 A unit-form quantity without a source is a finding
    Given a copy of the article with "The Planner used <quantity>."
    When the honesty audit runs
    Then a finding reads "number without source: <quantity>"

    Examples:
      | quantity  |
      | 9.4 Wh    |
      | 9.4 kJ    |
      | 1 joule   |
      | 40 Joules |
      | 2 watts   |
  @negative
  Scenario: AJ-143 A not-measured value is not a label
    Given a copy of the article with "40 J, not measured (results/20261025T131930Z-local.json)."
    When the honesty audit runs
    Then a finding reads "finding: no label: 40 J"
  @negative
  Scenario: AJ-143 A sentence-initial Not measured value is not a label
    Given a copy of the article with "Not measured, 40 J (results/20261025T131930Z-local.json)."
    When the honesty audit runs
    Then a finding reads "finding: no label: 40 J"
  @negative
  Scenario: AJ-143 A Nebius virtual machine sentence naming an A100 is a finding
    Given a workspace where the cloudgpu run file names an H100 and a story sentence "The Nebius virtual machine ran on an A100."
    When the honesty audit runs
    Then a finding reads "cloudgpu measurement on A100"
  @negative
  Scenario: AJ-143 A lone carriage return in the middle of a line is a finding
    Given a copy of the article with a lone carriage return in the middle of a line
    When the honesty audit runs
    Then a finding reads "rule 7: CRLF"
  @negative
  Scenario: AJ-143 A README whose browser statement reads only recorded without one is a finding
    Given a copy of the README whose browser statement reads "recorded without one"
    When the honesty audit runs
    Then a finding reads "browser statement missing: README.md"
