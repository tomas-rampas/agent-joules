@unit @AJ-S157 @DoD
Feature: AJ-157 Release package
  @positive
  Scenario: AJ-157 The release notes have the required headings and the scope sentence
    Given the committed docs/release-notes-v0.1.0.md
    When it is parsed
    Then it has the eight headings and the sentence "Energy is GPU-board energy only."
  @positive
  Scenario: AJ-157 The three zip assets have the expected contents
    Given the built artifacts folder
    When the zip listings are read
    Then the fdd zip lists AgentJoules.Cli.dll, the results zip counts every results json and the report zip lists index.html
  @positive
  Scenario: AJ-157 The checklist release command matches the files on disk
    Given the committed checklist and the artifacts folder
    When the paths in the "gh release create" block are checked
    Then every path exists and the notes path equals docs/release-notes-v0.1.0.md
  @negative
  Scenario: AJ-157 A key-shaped string inside a zip is rejected
    Given a results zip fixture containing a runtime-built "nvapi-" plus 16 characters value
    When the package check runs
    Then it fails with "key material in agent-joules-v0.1.0-results.zip"
  @negative
  Scenario: AJ-157 A tracked artifacts folder is rejected
    Given a repository where "artifacts/agent-joules-v0.1.0-fdd.zip" is tracked
    When the package check runs
    Then it fails with "artifacts must not be tracked"
  @negative
  Scenario: AJ-157 A number in the notes without a source is rejected
    Given release notes containing "the local run used 310 J"
    When the package check runs
    Then it fails with "number without source: 310 J"
  @negative
  Scenario: AJ-157 A missing asset named in the command is rejected
    Given a checklist command naming "artifacts/agent-joules-v0.1.0-report.zip" that does not exist
    When the package check runs
    Then it fails with "asset not found: artifacts/agent-joules-v0.1.0-report.zip"
  @negative
  Scenario: AJ-157 Release notes that claim a missing tier ran are rejected
    Given release notes mentioning cloudgpu figures and no cloudgpu run file
    When the package check runs
    Then it fails with "cloudgpu claim without run file"
