@unit @AJ-S085 @REQ-030
Feature: AJ-085 Generate the static HTML report page for the demo URL
  @positive
  Scenario: AJ-085 page has the table, the chart reference and the sentence
    Given a fixture with a local run and a nvidia run
    When the HTML page is rendered
    Then it contains exactly one table, "joules-per-step.svg" and "GPU-board energy only"
  @positive
  Scenario: AJ-085 one table row per task and agent per tier
    Given a fixture with 3 tasks, 4 agents and 1 tier
    When the HTML page is rendered
    Then the aggregate table body has 12 rows
  @positive
  Scenario: AJ-085 nojekyll file is created when absent
    Given a docs folder without .nojekyll
    When the report is written
    Then docs/.nojekyll exists
  @negative
  Scenario: AJ-085 page contains no script and no external resource
    Given any fixture
    When the HTML page is rendered
    Then the text "<script" does not appear
    And no link element and no src other than "joules-per-step.svg" appears
  @negative
  Scenario: AJ-085 hostile model ID is HTML-encoded
    Given a model ID containing "<script>alert(1)</script>"
    When the HTML page is rendered
    Then the text "<script" does not appear in the output
  @negative
  Scenario: AJ-085 existing nojekyll file is not rewritten
    Given a docs folder with a .nojekyll file of known content
    When the report is written
    Then the file content is unchanged
