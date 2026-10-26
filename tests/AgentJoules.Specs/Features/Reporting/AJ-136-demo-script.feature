@unit @AJ-S136 @REQ-027
Feature: AJ-136 Demo video script
  @positive
  Scenario: AJ-136 Shot durations fit the budget and are contiguous
    Given the committed content/demo-script.md
    When the shot list is parsed
    Then the durations sum to 165 and are at most 170
    And each start second equals the previous start plus the previous duration
  @positive
  Scenario: AJ-136 Narration is speakable in each shot
    Given the parsed shot list
    When words per second are computed for each narration cell
    Then no shot exceeds 2.5 words per second
  @positive
  Scenario: AJ-136 Commands shown all answer help
    Given the commands listed under "Commands shown"
    When each CLI command is run with "--help" in process
    Then every exit code is 0
  @negative
  Scenario: AJ-136 A script over three minutes is rejected
    Given a copy of the script whose durations sum to 181
    When the script check runs
    Then it fails with "total 181 s exceeds 170 s"
  @negative
  Scenario: AJ-136 A script over the 170 second budget but under three minutes is rejected
    Given a copy of the script whose durations sum to 175
    When the script check runs
    Then it fails with "total 175 s exceeds 170 s"
  @negative
  Scenario: AJ-136 Narration that is too long for its shot is rejected
    Given a copy of the script whose 12 second shot 1 has 40 narration words
    When the script check runs
    Then it fails with "shot 1: 3.3 words per second exceeds 2.5"
  @negative
  Scenario: AJ-136 A number without a results file is rejected
    Given a copy of the script whose shot 5 narration says "123 J" with no results file
    When the script check runs
    Then it fails with "number without source: 123 J"
  @negative
  Scenario: AJ-136 A command that does not exist is rejected
    Given a copy of the script that shows "AJ bench"
    When the script check runs
    Then it fails with "unknown command: bench"
  @negative
  Scenario: AJ-136 A script without the browser statement is rejected
    Given a copy of the script without the sentence that published results were recorded without a browser open
    When the script check runs
    Then it fails with "missing statement: recorded without a browser"
  @negative
  Scenario: AJ-136 A script without the dashboard commands is rejected
    Given a copy of the script that never mentions "--dashboard" or "dashboard --replay"
    When the script check runs
    Then it fails with "missing command: dashboard"
  @negative
  Scenario: AJ-136 A script without the upload target is rejected
    Given a copy of the script without the line "Upload target: public YouTube video"
    When the script check runs
    Then it fails with "missing header: Upload target"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-136 The committed script passes every script check
    Given the committed content/demo-script.md
    When the script check runs
    Then it reports no failure
  @positive
  Scenario: AJ-136 The fallback shot table fits the budget and is contiguous
    Given the committed content/demo-script.md
    When the shot list is parsed
    Then a fallback shot table exists and its durations sum to 170 or less
  @positive
  Scenario: AJ-136 The committed script is ASCII with LF endings
    Given the committed content/demo-script.md
    When the character set is checked
    Then no character failure is found
  @negative
  Scenario: AJ-136 A shot that starts at the wrong second is rejected
    Given a copy of the script whose shot 3 starts at second 30
    When the script check runs
    Then it fails with "shot 3: start 30 s is not 27 s (contiguous)"
  @negative
  Scenario: AJ-136 A narration that ranks a tier is rejected
    Given a copy of the script whose shot 6 narration says "The local tier beats the hosted tier."
    When the script check runs
    Then it fails with "ranking claim: beats"
  @negative
  Scenario: AJ-136 Shot 4 narration without the word illustrative is rejected
    Given a copy of the script without the word "illustrative"
    When the script check runs
    Then it fails with "shot 4: narration lacks illustrative"
  @negative
  Scenario: AJ-136 Shot 8 without the energy sentence is rejected
    Given a copy of the script without the sentence "Energy is GPU-board energy only."
    When the script check runs
    Then it fails with "shot 8 lacks: Energy is GPU-board energy only."
  @negative
  Scenario: AJ-136 A spelled-out unit without a results file is rejected
    Given a copy of the script whose shot 5 narration says "412 Joules" with no results file
    When the script check runs
    Then it fails with "number without source: 412 Joules"
  @negative
  Scenario: AJ-136 A bare number without a results file is rejected
    Given a copy of the script whose shot 5 narration says "7" with no results file
    When the script check runs
    Then it fails with "number without source: 7"
  @negative
  Scenario: AJ-136 A run command shown with another option than step 9 of the demo definition is rejected
    Given a copy of the script whose Commands shown add "dotnet run --project src/AgentJoules.Cli -- run --tier local --repeat 3"
    When the script check runs
    Then it fails with "commands shown: the run line differs from step 9 of the demo definition"
  @negative
  Scenario: AJ-136 A probe line with an option and a fourth line under Commands shown is rejected
    Given a copy of the script whose Commands shown add "dotnet run --project src/AgentJoules.Cli -- probe --bogus"
    When the script check runs
    Then it fails with "commands shown: expected 3 lines, found 4"
    And it fails with "commands shown: the probe line differs"
