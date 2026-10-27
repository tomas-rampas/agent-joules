@unit @AJ-S144 @REQ-025 @REQ-026 @REQ-016
Feature: AJ-144 report flags and the content template renderer
  @positive
  Scenario: AJ-144 Update then verify exits 0 and a second update changes nothing
    Given a workspace with latest results files for local, nvidia and nebius and a README with markers
    When "report --update-readme" runs twice and then "report --verify-readme" runs
    Then the second update changes no byte and verify exits 0
  @positive
  Scenario: AJ-144 Verify accepts a CRLF checkout of the same block
    Given an updated README converted to CRLF line endings
    When "report --verify-readme" runs
    Then the exit code is 0
  @positive
  Scenario: AJ-144 Render writes the variants with a Sources line first
    Given a workspace with the template and latest results files for local and nvidia
    When "report --render-content" runs
    Then the first line of content/linkedin-posts.md starts with "Sources:" and names both run files
    And "{{results." occurs 0 times and "{{repo.url}}" occurs once per variant
  @positive
  Scenario: AJ-144 An indexed path with the value suffix and a format resolves
    Given a placeholder "{{results.local.steps[0].netJoules.value|F1}}" and a run file whose first step has net joules 12.34 labelled measured
    When the placeholder is rendered
    Then the output reads "12.3"
  @positive
  Scenario: AJ-144 A null label value renders as its label text
    Given a placeholder for the nvidia gross joules whose value is null with label "N/A"
    When the placeholder is rendered
    Then the output reads "N/A" and never "0", "null" or an empty string
  @negative
  Scenario: AJ-144 A manual edit inside the markers exits 7
    Given an updated README whose results block has one number edited by hand
    When "report --verify-readme" runs
    Then the exit code is 7
  @negative
  Scenario: AJ-144 A README without the begin marker exits 2
    Given a README without the line "<!-- results:begin -->"
    When "report --update-readme" runs
    Then the exit code is 2 and the message names the marker
  @negative
  Scenario: AJ-144 A placeholder path missing from the results file exits 2
    Given a template placeholder "{{results.local.totals.joulesPerToken.value}}" that the file lacks
    When "report --render-content" runs
    Then the exit code is 2 and the message names the placeholder
  @negative
  Scenario: AJ-144 A tier with no completed or partial run file exits 2
    Given a template placeholder for cloudgpu and no completed or partial cloudgpu run file
    When "report --render-content" runs
    Then the exit code is 2 and the message names the tier "cloudgpu"
  @negative
  Scenario: AJ-144 A variant over 1300 characters exits 2
    Given a template variant whose rendered text is 1301 characters
    When "report --render-content" runs
    Then the exit code is 2 and the message names the variant and its length
  @positive
  Scenario: AJ-144 The block links the dashboard screenshot when the file exists
    Given a workspace where docs/report/dashboard.png exists
    When "report --update-readme" runs
    Then the results block contains the link to docs/report/dashboard.png and "report --verify-readme" exits 0
  @negative
  Scenario: AJ-144 Verify exits 7 when the screenshot link disagrees with the file
    Given an updated README that links docs/report/dashboard.png and a workspace where the file is then deleted
    When "report --verify-readme" runs
    Then the exit code is 7
