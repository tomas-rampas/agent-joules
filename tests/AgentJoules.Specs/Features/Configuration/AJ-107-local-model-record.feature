@integration @AJ-S107 @REQ-018 @REQ-020 @EDGE-009
Feature: Select and record the local model
  @positive
  Scenario: AJ-107 the Nemotron tag is verified and recorded
    Given Ollama is running and the Local:Runtime decision is "ollama"
    When the builder pulls and shows "nemotron-3-nano:30b" and lists the models endpoint
    Then the "## local" section holds the tag, the quantization, the size and a Verified date
  @positive
  Scenario: AJ-107 the Qwen3 8B tag is recorded as baseline
    Given "ollama list" shows a Qwen3 8B tag
    When the builder writes the "## local" section
    Then the Qwen tag is recorded exactly and marked "baseline (non-Nemotron)"
  @positive
  Scenario: AJ-107 partial offload is recorded with the processor split
    Given "ollama ps" after warm-up shows less than 100 percent GPU for the 30b tag
    When the builder records the model choice
    Then the processor split and the reason for keeping or replacing the tag are recorded
  @negative
  Scenario: AJ-107 Ollama stopped gives exit 5 with the start command
    Given the Ollama service is stopped
    When I run "run --tier local --dry-run"
    Then the exit code is 5
    And the output contains "ollama serve"
  @negative
  Scenario: AJ-107 an unpulled tag gives exit 4 with the pull command
    Given the local model ID is overridden to a tag that is not pulled
    When I run "run --tier local --dry-run"
    Then the exit code is 4
    And the output contains "ollama pull" followed by that tag
  @negative
  Scenario: AJ-107 the library lists no smaller Nemotron tag
    Given the Ollama library page lists no smaller verified Nemotron tag
    When the builder records the model choice
    Then the 30b tag is kept and the result of the library check is recorded
