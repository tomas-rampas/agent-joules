@unit @AJ-S134 @REQ-026
Feature: AJ-134 LinkedIn post drafts
  @positive
  Scenario: AJ-134 Three variants render within the length limit
    Given a workspace with latest results files for local, nvidia and nebius and the committed template
    When "report --render-content" runs
    Then the exit code is 0
    And the output has three variants of 1300 characters or fewer
    And the first line starts with "Sources:" and names run files that exist
  @positive
  Scenario: AJ-134 Every number is a labelled placeholder
    Given the committed template
    When hand-typed quantities are searched outside placeholders
    Then none are found
    And each metric placeholder is followed by a unit and one of measured, estimated, model-judged
  @positive
  Scenario: AJ-134 A null hosted energy value renders as N/A
    Given a results fixture where the nvidia energy metric has value null and label "N/A"
    When a placeholder for that metric is rendered
    Then the output reads "N/A" and never "0", "null" or an empty string
  @negative
  Scenario: AJ-134 A placeholder path missing from the results file fails the render
    Given a template placeholder "{{results.local.totals.joulesPerToken.value}}" that the file lacks
    When "report --render-content" runs
    Then the exit code is 2 and the message names the placeholder
  @negative
  Scenario: AJ-134 A variant over 1300 characters fails the render
    Given a template variant whose rendered text is 1301 characters
    When "report --render-content" runs
    Then the exit code is 2 and the message names the variant and its length
  @negative
  Scenario: AJ-134 A hand-typed number in the template is rejected
    Given a copy of the template containing "uses 412 J per run"
    When the template check runs
    Then it fails with "number without source: 412 J"
  @negative
  Scenario: AJ-134 A hand-typed number with a spelled-out unit in lower case is rejected
    Given a copy of the template containing "uses 412 joules per run"
    When the template check runs
    Then it fails with "number without source: 412 joules"
  @negative
  Scenario: AJ-134 A metric placeholder followed by unmeasured is rejected
    Given a copy of the template with a metric placeholder followed by the word unmeasured
    When the template check runs
    Then it fails with "placeholder without label and unit: {{results.local.totals.netJoules.value}}"
  @negative
  Scenario: AJ-134 A cloudgpu placeholder without a cloudgpu run file fails
    Given a workspace without a completed cloudgpu run file and a variant citing a cloudgpu placeholder
    When "report --render-content" runs
    Then the exit code is 2 and the message names the cloudgpu placeholder
  @negative
  Scenario: AJ-134 A variant without the repo placeholder is rejected
    Given a copy of the template whose launch variant lacks "{{repo.url}}"
    When the template check runs
    Then it fails with "missing {{repo.url}} in variant: launch"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-134 The committed template passes the whole template check
    Given the committed template
    When the template check runs
    Then it reports no failure
  @positive
  Scenario: AJ-134 The committed template has the three variants and the hooks of the prompt
    Given the committed template
    When the variant headings and the hooks are checked
    Then there are exactly three headings launch, lesson-learned and hackathon
    And the first sentence of each variant is a hook of the prompt or a recorded refinement without a digit
  @negative
  Scenario: AJ-134 A fourth variant is rejected
    Given a copy of the template with a fourth variant
    When the template check runs
    Then it fails with "expected 3 variants, found 4"
  @negative
  Scenario: AJ-134 A cloudgpu variant with the laptop-only scope sentence is rejected
    Given a copy of the template whose launch variant cites a cloudgpu placeholder with the laptop-only scope sentence
    When the template check runs
    Then it fails with "missing scope sentence in variant: launch"
  @negative
  Scenario: AJ-134 A metric placeholder without a label word is rejected
    Given a copy of the template with a metric placeholder that no label word follows
    When the template check runs
    Then it fails with "placeholder without label and unit: {{results.local.totals.netJoules.value}}"
  @negative
  Scenario: AJ-134 An opening sentence that is none of the hooks is rejected
    Given a copy of the template whose hackathon variant opens with another sentence
    When the template check runs
    Then it fails with "hook not from the prompt or the build log in variant: hackathon"
  @negative
  Scenario: AJ-134 A hook with a digit is rejected
    Given a copy of the template whose lesson-learned hook line carries a digit
    When the template check runs
    Then it fails with "number in the hook line in variant: lesson-learned"
  @positive
  Scenario: AJ-134 The committed template is ASCII with LF endings
    Given the committed template
    When the character set is checked
    Then no failure is found
  @positive
  Scenario: AJ-134 The committed posts are the render of the committed template
    Given a workspace with latest results files for local, nvidia and nebius and the committed template
    When "report --render-content" runs
    Then the exit code is 0
    And the rendered posts equal the committed content/linkedin-posts.md byte for byte
  @negative
  Scenario: AJ-134 A metric placeholder followed by another label word than the run file carries is rejected
    Given a copy of the template with a metric placeholder followed by the wrong label word
    When the template check runs
    Then it fails with "placeholder without label and unit: {{results.local.totals.netJoules.value}}"
  @negative
  Scenario: AJ-134 A label word that follows only the next placeholder is rejected
    Given a copy of the template with a metric placeholder whose label word follows only the next placeholder
    When the template check runs
    Then it fails with "placeholder without label and unit: {{results.local.totals.netJoules.value}}"
