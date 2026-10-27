@unit @AJ-S159 @DoD
Feature: AJ-159 Drop order and buffer
  @positive
  Scenario: AJ-159 The drop order is monotonic in damage
    Given the committed docs/build-log.md
    When the Drop order list is parsed
    Then steps C1 to C9 and D1 to D3 have "boxes unticked" none and Level 3 names boxes 21, 22 and 23
  @positive
  Scenario: AJ-159 Every trigger time is before the deadline
    Given the Drop order and Buffer sections
    When each trigger time is read
    Then every time is before 2026-10-30 17:00 UTC
  @positive
  Scenario: AJ-159 The never-dropped list names the minimum submission
    Given the committed build log
    When the Never dropped list is read
    Then it names the nebius run file, the local run file, the secret scan, the README block, the checklist with the push command, the story, the script and the user's video and submission
  @positive
  Scenario: AJ-159 The buffer reserves three hours before the deadline
    Given the Buffer table
    When the last target and the deadline are compared
    Then the gap is 3 hours or more
  @negative
  Scenario: AJ-159 A damaging item placed before a harmless one is rejected
    Given a drop list where Level 3 precedes step C3
    When the order check runs
    Then it fails with "Level 3 unticks boxes before harmless steps"
  @negative
  Scenario: AJ-159 An item without a trigger time is rejected
    Given a drop item with an empty trigger
    When the order check runs
    Then it fails with "no trigger: C4"
  @negative
  Scenario: AJ-159 A trigger after the deadline is rejected
    Given a drop item with trigger "2026-10-30 18:00 UTC"
    When the order check runs
    Then it fails with "trigger after deadline: C5"
  @negative
  Scenario: AJ-159 A never-dropped item that appears in the drop list is rejected
    Given a drop list that contains the secret scan
    When the order check runs
    Then it fails with "never-dropped item in drop list: secret scan"
  @negative
  Scenario: AJ-159 A buffer under three hours is rejected
    Given a Buffer table whose last target is 15:00 UTC
    When the order check runs
    Then it fails with "buffer 2 h is below 3 h"
