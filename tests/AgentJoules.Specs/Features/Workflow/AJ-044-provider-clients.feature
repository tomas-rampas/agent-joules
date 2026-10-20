@unit @AJ-S044 @REQ-012
Feature: AJ-044 Create provider chat clients per tier from configuration only
  @positive
  Scenario: AJ-044 request carries temperature 0 and the task token limit
    Given tier "nvidia" configured with a fake base URL and task maxOutputTokens 512
    When the chat client sends one request
    Then the recorded body has temperature 0 and the limit 512
  @positive
  Scenario: AJ-044 per-agent model override wins over the tier default
    Given tier "nebius" with ModelId "model-a" and AgentModelIds Writer "model-b"
    When clients for "Planner" and "Writer" are created
    Then "Planner" uses "model-a" and "Writer" uses "model-b"
  @positive
  Scenario: AJ-044 extra body JSON is merged into the completion request only
    Given tier "nvidia" with ExtraBodyJson that sets one extra field
    When a completion request and a models request are sent
    Then the completion body contains the extra field
    And the models request has no body change
  @negative
  Scenario: AJ-044 SDK retry is disabled so a 500 is attempted once
    Given a fake endpoint that always answers 500
    When one chat request is sent
    Then the handler recorded exactly 1 request
  @negative
  Scenario: AJ-044 seed is omitted when the tier does not support it
    Given tier "nebius" with SupportsSeed false
    When the chat client sends one request
    Then the recorded body has no seed field
  @negative
  Scenario: AJ-044 unparseable extra body JSON exits 2
    Given tier "nvidia" with ExtraBodyJson "{not json"
    When any command starts
    Then the exit code is 2
  @positive
  Scenario: AJ-044 configured max tokens field name is used in the body
    Given tier "nebius" with MaxTokensField "max_completion_tokens" and task maxOutputTokens 384
    When the chat client sends one request
    Then the recorded body has the property "max_completion_tokens" with value 384
