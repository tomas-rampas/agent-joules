@unit @AJ-S133 @REQ-025
Feature: AJ-133 README results block
  @positive
  Scenario: AJ-133 Update then verify succeeds and is idempotent
    Given a workspace with latest results files for local, nvidia and nebius and a README with markers
    When "report --update-readme" runs twice and then "report --verify-readme" runs
    Then the second update changes nothing and verify exits 0
  @positive
  Scenario: AJ-133 Block shows one row per tier with cloudgpu not run
    Given a workspace without a cloudgpu run file
    When "report --update-readme" runs
    Then the block has rows for local, nvidia and nebius and the row "cloudgpu | not run"
    And hosted rows show "N/A" for energy and never "0"
  @negative
  Scenario: AJ-133 Manual edit inside the markers is detected
    Given an updated README whose results block has one number edited by hand
    When "report --verify-readme" runs
    Then the exit code is 7
  @negative
  Scenario: AJ-133 Missing markers are reported
    Given a README without the line "<!-- results:begin -->"
    When "report --update-readme" runs
    Then the exit code is 2 and the message names the missing marker
  @positive
  Scenario: AJ-133 Block with a partial cloudgpu run says partial
    Given a latest cloudgpu file with run.status "partial" and stopReason "cost-cap"
    When "report --update-readme" runs
    Then the cloudgpu row contains "partial"
  @negative
  Scenario: AJ-133 A hosted row with a zero energy value is rejected
    Given a fixture where a hosted step has energy value 0 and label "measured"
    When the results are validated
    Then validation fails and no README block is written
  # The scenarios below are additions of the test side for criteria 1, 2, 3, 5 and 6 that the story text does not spell out.
  @positive
  Scenario: AJ-133 The block is ordered local, cloudgpu, nvidia, nebius and names the header facts
    Given a workspace with all four tiers and a README with markers
    When "report --update-readme" runs
    Then the rows are in the order local, cloudgpu, nvidia, nebius
    And the block names the GPU, the driver version, the run date in UTC, the local runtime and the model IDs with precision
    And the block contains "Energy is GPU-board energy only."
  @positive
  Scenario: AJ-133 A sampling tier row says sampling
    Given a workspace whose local run used the sampling method
    When "report --update-readme" runs
    Then the local row contains "sampling"
  @positive
  Scenario: AJ-133 No sentence of the README averages across tiers
    Given a workspace with all four tiers and a README with markers
    When "report --update-readme" runs
    Then the README does not contain "average of"
  @positive
  Scenario: AJ-133 The report page has no script and docs/.nojekyll exists
    Given a workspace with all four tiers and a README with markers
    When "report --in results --out docs/report" runs
    Then docs/report/index.html contains no "<script" and docs/.nojekyll exists
