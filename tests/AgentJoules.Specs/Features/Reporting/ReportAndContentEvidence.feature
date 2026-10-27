@unit @AJ-S152 @DoD
Feature: AJ-152 Generated outputs match the committed results
  @positive
  Scenario: AJ-152 Report generation is deterministic
    Given a workspace with the committed results and a README with markers
    When "report --in results/ --out docs/report/ --update-readme --render-content" runs twice
    Then the second run changes no file and "report --verify-readme" exits 0
  @positive
  Scenario: AJ-152 The report page has no script and links the chart
    Given the generated docs/report/index.html and docs/.nojekyll
    When the page is read
    Then it contains no "<script", contains "joules-per-step.svg" and "GPU-board energy only"
  @positive
  Scenario: AJ-152 The rendered posts have three variants and no placeholder
    Given the committed content/linkedin-posts.md
    When it is parsed
    Then it has three variants, no "{{results." text, and a Sources line naming existing files
  @positive
  Scenario: AJ-152 The shot list totals 170 seconds or less
    Given the committed content/demo-script.md
    When the duration column is summed
    Then the sum is 170 or less
  @negative
  Scenario: AJ-152 A hand-edited README block is rejected
    Given a README whose results block has one number edited
    When "report --verify-readme" runs
    Then the exit code is 7
  @negative
  Scenario: AJ-152 A script element in the page is rejected
    Given a page fixture containing "<script>"
    When the evidence check runs
    Then it fails with "script element in docs/report/index.html"
  @negative
  Scenario: AJ-152 A missing nojekyll file is rejected
    Given a workspace without docs/.nojekyll
    When the evidence check runs
    Then it fails with "missing docs/.nojekyll"
  @negative
  Scenario: AJ-152 A shot list above 170 seconds is rejected
    Given a script whose durations sum to 171
    When the evidence check runs
    Then it fails with "shot list totals 171 s, limit 170 s"
  @negative
  Scenario: AJ-152 A post with an unresolved placeholder is rejected
    Given rendered posts containing "{{results.local.totals.netJoules.value}}"
    When the evidence check runs
    Then it fails with "unresolved placeholder in content/linkedin-posts.md"
