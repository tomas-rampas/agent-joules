@unit @AJ-S155 @DoD
Feature: AJ-155 Final hardening
  @positive
  Scenario: AJ-155 A clean build output is accepted
    Given a captured build output containing "0 Warning(s)" and "0 Error(s)" and exit code 0
    When the hardening check runs
    Then box 4 passes
  @positive
  Scenario: AJ-155 The test summary line is recorded with the three counts
    Given a captured test run with 412 passed, 0 failed and 9 skipped
    When the hardening check formats the summary
    Then the line reads "dotnet test: passed=412 failed=0 skipped=9 exit=0"
  @positive
  Scenario: AJ-155 The CI workflow has both runners and no secrets
    Given the committed .github/workflows/ci.yml
    When it is searched
    Then it contains windows-latest, ubuntu-latest, the build, format and test commands and a probe step
    And it contains no "secrets."
  @positive
  Scenario: AJ-155 The secret scan fallback records its name and exit codes
    Given gitleaks is not installed
    When the scan step runs
    Then the build log records "git grep fallback" with the exit codes of the four commands
  @negative
  Scenario: AJ-155 A build with a warning is rejected
    Given a captured build output containing "1 Warning(s)"
    When the hardening check runs
    Then it fails with "build has warnings"
  @negative
  Scenario: AJ-155 A test run with a failure is rejected
    Given a captured test run with 1 failed
    When the hardening check runs
    Then it fails with "tests failed: 1"
  @negative
  Scenario: AJ-155 A format difference is rejected
    Given a captured "dotnet format --verify-no-changes" exit code 2
    When the hardening check runs
    Then it fails with "format check failed"
  @negative
  Scenario: AJ-155 A key-shaped string in a tracked file is rejected
    Given a tracked fixture file containing a runtime-built "nvapi-" plus 16 characters value
    When the secret scan runs
    Then it fails with "key-shaped string in tracked file"
  @negative
  Scenario: AJ-155 A workflow that references a secret is rejected
    Given a workflow fixture containing "secrets.NVIDIA_API_KEY"
    When the CI check runs
    Then it fails with "secrets reference in ci.yml"
