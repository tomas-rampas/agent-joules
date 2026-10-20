@unit @AJ-S038 @REQ-024
Feature: Add the GitHub Actions matrix for build, tests and the no-GPU probe
  @positive
  Scenario: AJ-038 Both runners and the strict build command are present
    Given .github/workflows/ci.yml
    When the workflow text is read
    Then it contains "windows-latest", "ubuntu-latest" and "dotnet build -warnaserror -tl:off"
  @positive
  Scenario: AJ-038 The probe step expects the no-GPU answer
    Given .github/workflows/ci.yml
    When the probe step is read
    Then it runs "probe --json" under bash and checks for "gpuPresent": false
  @positive
  Scenario: AJ-038 The local commands match the workflow
    Given the workflow's restore, build and test commands
    When they run locally
    Then each exits 0
  @negative
  Scenario: AJ-038 A secret reference is rejected
    Given the workflow contains "secrets.NVIDIA_API_KEY"
    When "grep -n 'secrets\.' ci.yml" runs
    Then it exits 0 with the offending line and the story fails
  @negative
  Scenario: AJ-038 A build step without warnings as errors is rejected
    Given the build step reads "dotnet build" without -warnaserror
    When CiWorkflowTests runs
    Then the test fails
  @negative
  Scenario: AJ-038 A pull_request_target trigger is rejected
    Given the workflow triggers on pull_request_target
    When CiWorkflowTests runs
    Then the test fails because forks would run with repository privileges
