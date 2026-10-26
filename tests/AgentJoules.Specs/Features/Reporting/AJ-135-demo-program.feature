@unit @AJ-S135 @REQ-027 @REQ-028 @REQ-032
Feature: AJ-135 Demo program end to end
  @positive
  Scenario: AJ-135 The demo section lists the ten commands in order
    Given the committed content/demo-script.md
    When the fenced block under "## Demo program" is parsed
    Then it holds ten commands whose verbs are probe, run, run, calibrate, cloudgpu, validate, report, report, run, dashboard in that order
  @positive
  Scenario: AJ-135 Replay of a committed hosted run emits one event per step
    Given a committed nebius run file fixture with 36 steps
    When "dashboard --replay <file> --speed 50 --exit-after 5" runs in process
    Then the exit code is 0 and 36 events were emitted, one per step
  @negative
  Scenario: AJ-135 A missing replay file is rejected
    Given no file at results/missing.json
    When "dashboard --replay results/missing.json" runs
    Then the exit code is not 0 and the message names the file
  @negative
  Scenario: AJ-135 A script without the browser-load statement is rejected
    Given a copy of the script without the sentence about recording without a browser open
    When the demo check runs
    Then it fails with "missing statement: published results recorded without a browser"
  @positive
  Scenario: AJ-135 Every demo command answers help
    Given the six CLI commands used by the demo
    When each is run with "--help" in process
    Then every exit code is 0
  @positive
  Scenario: AJ-135 The three laptop and hosted run files have the demo shape
    Given the latest committed run files for local, nvidia and nebius
    When run.repeat and the step counts are read
    Then each file has run.repeat 3 and 36 steps and no step energy is written as 0 where the label is N/A
  @positive
  Scenario: AJ-135 Without a cloudgpu run the report shows a not-run row
    Given a workspace with run files for local, nvidia and nebius only
    When "report --in results/ --out docs/report/" runs
    Then the exit code is 0 and summary.md contains the row "cloudgpu | not run"
  @negative
  Scenario: AJ-135 A demo command with an unknown verb is rejected
    Given a copy of the script whose block contains "dotnet run --project src/AgentJoules.Cli -- bench"
    When the demo check runs
    Then it fails with "unknown command: bench"
  @negative
  Scenario: AJ-135 A missing demo artifact is reported by path
    Given a workspace where docs/report/index.html is deleted
    When the artifact check runs
    Then it fails with "missing artifact: docs/report/index.html"
  @negative
  Scenario: AJ-135 A run file with fewer than three repeats is rejected
    Given a latest local run file with run.repeat 1
    When the demo shape check runs
    Then it fails with "run.repeat 1 is below 3: local"
  @negative
  Scenario: AJ-135 A script without the cloudgpu fallback section is rejected
    Given a copy of the script without the heading "If the cloudgpu tier is absent"
    When the demo check runs
    Then it fails with "missing heading: If the cloudgpu tier is absent"
  @positive
  Scenario: AJ-135 The rehearsal screenshot is a PNG at the fixed path
    Given a workspace where docs/report/dashboard.png exists and starts with the PNG signature
    When the demo artifact check runs
    Then it passes for the screenshot
  @negative
  Scenario: AJ-135 A screenshot saved under another name is rejected
    Given a workspace where the screenshot is saved as docs/report/dash.png and docs/report/dashboard.png is absent
    When the demo artifact check runs
    Then it reports "open user action: docs/report/dashboard.png" and the README has no dashboard link
  @positive
  Scenario: AJ-135 The build log records the width check at both widths
    Given a build log with the line "Dashboard width check: 1280 ok, 1920 ok"
    When the demo artifact check runs
    Then the width check passes
  @negative
  Scenario: AJ-135 A width check recorded for one width only is rejected
    Given a build log with the line "Dashboard width check: 1280 ok"
    When the demo artifact check runs
    Then it fails with "width check incomplete: 1920"
  # The scenarios below are additions beyond the story's Gherkin, for the criteria that the story text does not spell out.
  @positive
  Scenario: AJ-135 The committed script passes the structure check
    Given the committed content/demo-script.md
    When the demo check runs
    Then it reports no failure
  @positive
  Scenario: AJ-135 The shot table of the script equals the shot table of the demo definition
    Given the committed content/demo-script.md and the story file
    When the shot tables are compared
    Then all nine rows are equal in shot number, start and duration
  @negative
  Scenario: AJ-135 A shot that lasts another number of seconds is rejected
    Given a copy of the script whose shot 6 lasts 31 seconds
    When the shot tables are compared
    Then it fails with "shot 6 differs: story 90+30, script 90+31"
  @positive
  Scenario: AJ-135 The task set is the three task files and the video task exists
    Given the task files of the repository
    When the task names are read from the name field
    Then there are three task files and the task of the video command is one of them
  @positive
  Scenario: AJ-135 Validate counts the committed run files
    Given the committed results folder
    When "validate results/" runs on the repository
    Then the exit code is 0 and it prints "Validated n files" with n the number of run files
  @positive
  Scenario: AJ-135 Every artifact of the demo table exists
    Given the committed content/demo-script.md and the repository files
    When the artifact check runs
    Then it passes
  @positive
  Scenario: AJ-135 The report page exists and holds no script
    Given the committed report page
    When its script tags are counted
    Then there are none
  @positive
  Scenario: AJ-135 The live run folder is git-ignored
    Given the committed .gitignore
    When the ignore rule for artifacts is read
    Then artifacts/ is ignored
  @negative
  Scenario: AJ-135 A gitignore without artifacts is rejected
    Given a .gitignore without an artifacts line
    When the ignore rule for artifacts is read
    Then it fails with "artifacts/ is not git-ignored"
  @positive
  Scenario: AJ-135 The committed screenshot is a PNG or an open user action and the README link agrees
    Given the committed repository
    When the screenshot is checked
    Then it is a PNG or an open user action and the README link rule holds
  @positive
  Scenario: AJ-135 The script and the README state the browser-free recording
    Given the committed content/demo-script.md and the README
    When the browser statement is looked for
    Then both state that the published results were recorded without a browser
  @positive
  Scenario: AJ-135 The dashboard dry run prints the loopback URL
    Given the run command with fake keys, a scripted GPU and the dashboard enabled
    When "run --tier local --dashboard --dry-run" runs in process
    Then the output has a Dashboard line with "http://127.0.0.1:" and the exit code is 0
  @negative
  Scenario: AJ-135 A demo command with a changed flag is rejected
    Given a copy of the script whose demo step 3 reads "dotnet run --project src/AgentJoules.Cli -- run --tier local"
    When the demo check runs
    Then it fails with "demo step 3 differs from the demo definition"
  @negative
  Scenario: AJ-135 A changed command inside the cloud GPU step is rejected
    Given a copy of the script whose demo step 5 reads "./AgentJoules.Cli probe --json --save --suffix cloudgpu && ./AgentJoules.Cli run --tier cloudgpu --repeat 1 && ./AgentJoules.Cli calibrate --suffix cloudgpu"
    When the demo check runs
    Then it fails with "demo step 5 differs from the demo definition"
  @negative
  Scenario: AJ-135 A replay step with other speed and exit options is rejected
    Given a copy of the script whose demo step 10 reads "dotnet run --project src/AgentJoules.Cli -- dashboard --replay results/20261025T133440Z-nebius.json --speed 50 --exit-after 5"
    When the demo check runs
    Then it fails with "demo step 10 differs from the demo definition"
  @negative
  Scenario: AJ-135 A renamed cloudgpu artifact is reported while a cloudgpu run file exists
    Given a workspace where the cloudgpu probe file is renamed
    When the artifact check runs
    Then it fails with "missing artifact" and the path of the renamed file
  @positive
  Scenario: AJ-135 Without any cloudgpu file the fallback section covers the cloudgpu artifacts
    Given a workspace without any cloudgpu file
    When the artifact check runs
    Then it passes
  @positive
  Scenario: AJ-135 The committed build log records the width check at both widths
    Given the committed docs/build-log.md
    When the demo artifact check runs
    Then the width check passes
