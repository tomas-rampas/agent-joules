@unit @AJ-S131 @REQ-025 @REQ-032
Feature: AJ-131 README quick start, CLI reference and reproduce steps
  @positive
  Scenario: AJ-131 Judges without a GPU needs no key and no GPU
    Given the committed README.md
    When the section "Judges without a GPU" is extracted
    Then it contains "dotnet test", "validate results/" and "docs/report/index.html"
    And it contains no "_API_KEY" text
  @positive
  Scenario: AJ-131 Every documented option exists in the CLI help
    Given the CLI reference table of the README
    When "run --help" is executed in process
    Then the exit code is 0
    And the help text contains each option named for run: --tier --task --mode --out --baseline-seconds --repeat --dry-run
  @positive
  Scenario: AJ-131 Exit-code table is complete
    Given the committed README.md
    When the exit-code table is parsed
    Then it has the codes 0 1 2 3 4 5 6 7 each once
  @negative
  Scenario: AJ-131 README that documents a nonexistent option is rejected
    Given a copy of the README whose run options include "--fast"
    When the option check runs against "run --help"
    Then it fails with "option not in help: --fast"
  @negative
  Scenario: AJ-131 README with a key value is rejected
    Given a copy of the README containing a runtime-built "nvapi-" plus 16 characters value
    When the secret check runs on the file
    Then it fails with "key material in README.md"
  @negative
  Scenario: AJ-131 README missing the Judges without a GPU heading is rejected
    Given a copy of the README without that heading
    When the README structure check runs
    Then it fails with "missing heading: Judges without a GPU"
  @negative
  Scenario: AJ-131 Judges section that asks for a key is rejected
    Given a copy of the README whose Judges without a GPU section mentions NVIDIA_API_KEY
    When the README structure check runs
    Then it fails with "judges section needs a key"
  @positive
  Scenario: AJ-131 The dashboard screenshot is linked when the file exists
    Given a workspace where docs/report/dashboard.png exists
    When the README link check runs
    Then the README links docs/report/dashboard.png under the dashboard usage text
  @negative
  Scenario: AJ-131 A link to a missing dashboard screenshot is rejected
    Given a README that links docs/report/dashboard.png and a workspace where the file is absent
    When the README link check runs
    Then it fails with "dangling link: docs/report/dashboard.png"
  # The scenarios below are additions of the test side for criteria 1, 3, 4, 5, 6 and 7 that the story text does not spell out.
  @positive
  Scenario: AJ-131 The README has the five guide headings once each
    Given the committed README.md
    When the README structure check runs
    Then the check passes
  @positive
  Scenario: AJ-131 Every command documents only options its help prints
    Given the CLI reference table of the README
    When "--help" is executed in process for each of the six commands
    Then each exits 0 and prints every long option the README names for it
  @positive
  Scenario: AJ-131 The exit-code table rows map to the exit codes of the program
    Given the committed README.md
    When the exit-code table is parsed
    Then each row names the meaning of the ExitCode of the same number
  @positive
  Scenario: AJ-131 The README shows no key value
    Given the committed README.md
    When the secret check runs on the file
    Then the check passes
  @positive
  Scenario: AJ-131 Reproduce is an ordered list of the ten demo steps
    Given the committed README.md
    When the section "Reproduce" is extracted
    Then it is an ordered list numbered 1 to 10
  @positive
  Scenario: AJ-131 Prior art cites the four sources and scopes the claim
    Given the committed README.md
    When the section "Prior art" is extracted
    Then it cites arXiv 2407.16893, TokenPowerBench, Watt Counts 2604.09048 and greentoken with links and names the search of 2026-10-17
  @negative
  Scenario: AJ-131 A README without the screenshot link is rejected when the file exists
    Given a README without a link to docs/report/dashboard.png and a workspace where the file exists
    When the README link check runs
    Then it fails with "missing link: docs/report/dashboard.png"
  @negative
  Scenario: AJ-131 A Reproduce step that differs from the demo definition is rejected
    Given a copy of the README whose Reproduce step 3 runs the local tier only
    When the Reproduce content check runs
    Then it fails with "reproduce step 3 differs from the demo definition"
  @negative
  Scenario: AJ-131 A Reproduce command with an extra option is rejected
    Given a copy of the README whose Reproduce step 3 adds --dry-run to the command
    When the Reproduce content check runs
    Then it fails with "reproduce step 3 differs from the demo definition"
  @negative
  Scenario: AJ-131 A Judges section with a fifth action that needs a GPU is rejected
    Given a copy of the README whose Judges without a GPU section has a fifth action that runs a tier
    When the README structure check runs
    Then it fails with "judges section has 5 actions, not 4"
    And it fails with "judges section needs a GPU"
