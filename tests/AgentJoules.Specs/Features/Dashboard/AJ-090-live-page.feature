@unit @AJ-S090 @REQ-032
Feature: AJ-090 Serialize live events and embed the dashboard page
  @positive
  Scenario: AJ-090 step payload carries the agent name and the labelled net joules
    Given a fixture local step for agent "Writer" with net joules measured
    When the event is serialized
    Then the payload contains "Writer" and a netJoules object with label "measured"
  @positive
  Scenario: AJ-090 page contains the stream client, the formatter and the sentence
    Given the embedded dashboard page
    When it is loaded
    Then it contains EventSource "/events", the null-to-label formatter and "GPU-board energy only"
  @negative
  Scenario: AJ-090 hosted step power renders as N/A not zero
    Given a hosted step whose power metric has no value
    When the event is serialized
    Then the power metric has label "N/A", reason "hosted-tier" and a null value
    And the page formatter prints the label text for a null value
  @negative
  Scenario: AJ-090 page has no external resource
    Given the embedded dashboard page after placeholder replacement
    When the page text is scanned
    Then there is no script src, no link href and no http or https URL other than the repository link
  @negative
  Scenario: AJ-090 payload contains no key material
    Given a runtime-built key starting with "nvapi-" present in the configuration
    When every event type is serialized
    Then no payload contains any character of the key after the nvapi- prefix
  @positive
  Scenario: AJ-090 payload carries cumulative values as label objects
    Given two completed steps of agent "Writer" with net joules 10 and 12 (fixture values)
    When the second step event is serialized
    Then cumulativeNetJoules is 22 with label "measured"
  @negative
  Scenario: AJ-090 cumulative stays correct after earlier events are dropped
    Given 1500 events emitted with no reader so the earliest are dropped
    When the last step event is serialized
    Then its cumulative values equal the sum over all emitted steps
  @negative
  Scenario: AJ-090 page builds no markup from event strings
    Given the embedded dashboard page
    When the page text is scanned
    Then it contains no innerHTML and no createElementNS
  @positive
  Scenario: AJ-090 page source obeys the render model
    Given the embedded dashboard page source
    When the render rules are checked
    Then there is exactly one EventSource and one requestAnimationFrame loop
    And createElement appears only inside the agent card builder
  @positive
  Scenario: AJ-090 running energy is a baseline-net trapezoid estimate labelled estimated
    Given power events of 60 W at 0.0 s, 0.1 s and 0.2 s for the active step and a 20 W baseline mean
    When the snapshot is serialized
    Then the running energy is 8 with label "estimated"
  @positive
  Scenario: AJ-090 page states the one-second driver average
    Given the embedded dashboard page
    When the page text is scanned
    Then it contains "one-second driver average"
  @negative
  Scenario: AJ-090 a page source with innerHTML fails the render rules
    Given a page source that assigns innerHTML
    When the render rules are checked
    Then the check fails and names innerHTML
  @negative
  Scenario: AJ-090 a page source with a timer or fetch fails the render rules
    Given a page source that calls setInterval and fetch
    When the render rules are checked
    Then the check fails and names setInterval and fetch
  @negative
  Scenario: AJ-090 a hidden tab stops repainting
    Given the repaint loop of the embedded page
    When the document is hidden
    Then the loop returns without painting
  @negative
  Scenario: AJ-090 estimate is replaced by the measured value on step completion
    Given a running step with an estimated energy of 118 and an estimated chunk count
    When the step-completed event with measured net joules arrives
    Then the displayed value carries label "measured" and no estimated value remains for that step
  @negative
  Scenario: AJ-090 estimates are absent from the results file
    Given a run executed with the dashboard enabled
    When the results file is read
    Then it contains no running-estimate field and no estimated energy label
  @negative
  Scenario: AJ-090 reconnect keeps the last state until the snapshot
    Given the page source error handler
    When the stream reconnects
    Then no state is cleared before the snapshot item arrives
  @positive
  Scenario: AJ-090 page defines design tokens, regions and the light theme
    Given the embedded dashboard page source
    When the design rules are checked
    Then it has at least 6 custom properties, a prefers-color-scheme media query and the regions header, agents, chart and footer
    And it has no link element, no import and no external url
  @positive
  Scenario: AJ-090 palette meets the WCAG AA contrast ratio in both themes
    Given the palette values of the dark and the light theme
    When the contrast ratios of every text-colour token on the panel are computed
    Then every ratio is at least 4.5
  @negative
  Scenario: AJ-090 a page without the light theme media query fails the design check
    Given a page source without a prefers-color-scheme media query
    When the design rules are checked
    Then the check fails and names the media query
  @negative
  Scenario: AJ-090 a page with an import fails the design check
    Given a page source that contains an @import rule
    When the design rules are checked
    Then the check fails and names the import
  @negative
  Scenario: AJ-090 a low-contrast palette fails the contrast check
    Given a palette whose muted text on the panel has a ratio of 3.0
    When the contrast ratios are computed
    Then the check fails and names the muted-on-panel pair
  @positive
  Scenario: AJ-090 two overlapping active steps both carry shared-window
    Given two steps active at the same time in concurrent mode
    When the snapshot is serialized
    Then both running estimates carry the flag "shared-window"
  @positive
  Scenario: AJ-090 the first power sample after step start is held to the step start
    Given a step started at 1.00 s and a first power sample at 1.05 s of 60 W with a 20 W baseline mean
    When the next sample at 1.15 s of 60 W arrives
    Then the running energy covers 0.15 s of net 40 W, which is 6 with label "estimated"
  @negative
  Scenario: AJ-090 hosted step running energy is N/A not zero
    Given a step of tier "nvidia" with no power samples
    When the snapshot is serialized
    Then the running energy has label "N/A", reason "hosted-tier" and a null value
