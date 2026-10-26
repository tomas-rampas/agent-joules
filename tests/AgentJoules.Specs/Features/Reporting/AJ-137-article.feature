@unit @AJ-S137 @REQ-027
Feature: AJ-137 Long-form article
  @positive
  Scenario: AJ-137 The article cites the required prior art and has a Limitations section
    Given the committed content/article.md
    When the citations and headings are checked
    Then it contains 2407.16893, TokenPowerBench, 2604.09048, greentoken and one "Limitations" heading
    And the Limitations section contains GPU-only, display, power limit, sampling, offload, hosted and cloud
  @positive
  Scenario: AJ-137 Every quantity names an existing results file and a label
    Given the committed article
    When each line with a measured quantity is checked
    Then each names an existing results file and one label word
  @positive
  Scenario: AJ-137 The article is within the length bounds
    Given the committed article
    When the words are counted
    Then the count is between 800 and 2500
  @negative
  Scenario: AJ-137 A quantity without a results file is rejected
    Given a copy of the article with the line "The Writer used 55 J."
    When the article check runs
    Then it fails with "number without source: 55 J"
  @negative
  Scenario: AJ-137 A label word inside another word does not label a quantity
    Given a copy of the article with the line "The Writer used 55 J unmeasured (results/20261025T133440Z-nebius.json)."
    When the article check runs
    Then it fails with "number without label: 55 J"
  @negative
  Scenario: AJ-137 A missing Limitations heading is rejected
    Given a copy of the article without the heading "Limitations"
    When the article check runs
    Then it fails with "missing heading: Limitations"
  @negative
  Scenario: AJ-137 A ranking claim is rejected
    Given a copy of the article saying "the local tier is greener than the hosted tier"
    When the article check runs
    Then it fails with "ranking claim: greener"
  @negative
  Scenario: AJ-137 A missing citation is rejected
    Given a copy of the article without "greentoken"
    When the article check runs
    Then it fails with "missing citation: greentoken"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-137 The committed article passes every article check
    Given the committed article
    When the article check runs
    Then it reports no failure
  @positive
  Scenario: AJ-137 The What is new section names the contribution and scopes the claim
    Given the committed article
    When the sections are checked
    Then What is new names per-agent-step attribution, .NET, Microsoft Agent Framework, Nemotron and the search of 2026-10-17
  @positive
  Scenario: AJ-137 Each results table repeats the environment of the run file it names
    Given the committed article
    When the environment of each results table is compared with its run file
    Then GPU name, driver, run date, local runtime and model tags equal the strings of the run file and at least one table names a run file
  @negative
  Scenario: AJ-137 A table with another driver than its run file is rejected
    Given a copy of the article whose driver version differs from the local run file
    When the article check runs
    Then it fails with a message starting "table environment differs from"
  @negative
  Scenario: AJ-137 An article that is too short is rejected
    Given a copy of the article cut to 100 words
    When the article check runs
    Then it fails with "word count 100 is outside 800 to 2500"
  @negative
  Scenario: AJ-137 A non-ASCII character is rejected
    Given a copy of the article with a non-ASCII character
    When the article check runs
    Then it fails with a message starting "non-ASCII character at line"
  @positive
  Scenario: AJ-137 The committed article is ASCII with LF endings
    Given the committed article
    When the character set is checked
    Then no character failure is found
  @positive
  Scenario: AJ-137 Without a cloudgpu run file the article says the tier did not run
    Given an article that names the cloudgpu tier only in a sentence that it did not run
    When the cloudgpu-absent rule is applied with no cloudgpu run file
    Then it passes
  @negative
  Scenario: AJ-137 A cloudgpu result without a cloudgpu run file is rejected
    Given an article with a cloudgpu result line
    When the cloudgpu-absent rule is applied with no cloudgpu run file
    Then it fails with a message starting "cloudgpu result without a run file"
  @negative
  Scenario: AJ-137 An article that never mentions the cloudgpu tier is rejected when it did not run
    Given an article that does not mention the cloudgpu tier
    When the cloudgpu-absent rule is applied with no cloudgpu run file
    Then it fails with "missing sentence: the cloudgpu tier did not run"
  @negative
  Scenario: AJ-137 A runtime version that the run file does not record is rejected
    Given a copy of the article whose local runtime version differs from the local run file
    When the article check runs
    Then it fails with a message starting "table environment differs from"
  @negative
  Scenario: AJ-137 A header replaced by prose is rejected
    Given a copy of the article whose local environment header is prose
    When the article check runs
    Then it fails with a message ending "header missing"
  @negative
  Scenario: AJ-137 A citation in another case than the story greps is rejected
    Given a copy of the article whose greentoken citation is written "GreenToken"
    When the article check runs
    Then it fails with "missing citation: greentoken"
