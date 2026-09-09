# FluxMux Quick Commissioning Mode Spec (deferred)

> Deferred design notes only. This mode is not in the UI. Live-Switch Testing keeps Switch Benchmark for focused continuity checks. Revisit only if there is a clear product need for an auto-qualification pass that new users will actually run.

## Purpose
Enable a time-constrained user to establish a reliable 2-3 model team (local + cloud) with minimal oversight, low token spend, and auto-saved settings.

## Product Name
Quick Commissioning Mode

## User Promise
In one short run, FluxMux should:
- pick the best working model trio for the current machine and account
- auto-tune key runtime settings
- verify context continuity with lightweight stress checks
- save a ready-to-use profile set with plain-language guidance

## Non-Goals
- long-duration benchmark campaigns
- exhaustive model matrix testing
- deep manual tuning workflows

## Default Hard Caps
- wall-clock cap: 20 minutes
- token cap for cloud tests: 12000 tokens
- candidate pair/trio cap: 4 candidates
- per-phase timeout: 90 seconds
- max retries per step: 1

## Inputs (Minimal)
Required:
- preferred task style: drafting | coding | mixed
- local model directory
- selected cloud provider

Optional:
- budget profile: tight | standard | thorough
- preferred local model shortlist
- vision relevance flag: low | medium | high

## Budget Profiles
- tight: 8 minutes, 5000 cloud tokens, 2 candidates
- standard: 20 minutes, 12000 cloud tokens, 4 candidates
- thorough: 35 minutes, 25000 cloud tokens, 6 candidates

## Auto Role Assignment
For each candidate set, assign at most 3 roles:
- Local Fast: low latency draft/iteration role
- Local Deep: stronger local reasoning/execution role
- Cloud Expand: fallback/expansion and long-context synthesis role

If only 2 models are viable:
- Local General
- Cloud Expand

## Test Ladder (Escalating)
Run in order and stop early on clear failures.

1. Mode 1: Baseline Sanity
- launch, health, one short completion
- reject if launch or health is unstable

2. Mode 2: Route-Switch Continuity (Light)
- local -> cloud -> local
- short context capsule and intent check

3. Mode 3: Noisy Continuity (Light Stress)
- same switch path with injected irrelevant chatter
- score retention vs leakage

4. Mode 4: Round-Trip Fidelity
- A -> B -> C -> A reconstruction check
- compare final intent against original anchors

5. Mode 5: Recommendation Quality
- verify user-facing recommendation remains accurate and actionable

## Common Real-World Stress Scenario
Must include this scenario at least once per commissioning run:
- local model begins under context pressure (long turns or large script payload)
- local hands off compressed context to specialist model
- specialist returns enriched context
- local resumes for execution-focused continuation

Score this scenario separately as Rehydration Stability.

## Scoring
Primary metrics:
- Essential Retention Score (ERS)
- Chatter Leakage Score (CLS)
- Round-Trip Drift Score (RDS)
- Rehydration Stability Score (RSS)
- Latency Stability Score (LSS)

Recommended normalized aggregate:
- Commissioning Confidence Score (CCS)
- CCS = 0.30*ERS + 0.20*(100-CLS) + 0.20*(100-RDS) + 0.20*RSS + 0.10*LSS

## Pass/Fail Rules
- hard fail if any baseline health step fails twice
- hard fail if token cap or wall-clock cap is exceeded
- soft fail if CCS < 70
- high confidence if CCS >= 82 and no hard failures

## Output (User-Facing)
Keep this concise and plain language.

Required output block:
- Recommended setup: model trio (or pair)
- Confidence: High | Medium | Low
- Why this setup: 2-3 short bullets
- When to switch to cloud: one sentence trigger rule
- Expected limits: one sentence

## Output (Advanced Details)
Hidden behind an advanced expander.

Include:
- per-mode scores
- per-hop latency/token usage
- failure notes and fallback decisions
- selected parameter values
- run id and timestamp

## Auto-Saved Artifacts
- active commissioning profile
- fallback profile
- concise recommendation text for UI
- structured run log (jsonl)

## Suggested Saved Fields
- selected local fast model
- selected local deep model
- selected cloud expand model
- tuned context window
- tuned temperature
- handoff compression style
- recommended switch trigger
- commissioning confidence score
- run budget used (time/tokens)

## UX Behavior
- one-click start from local setup area
- visible budget meter during run (time/tokens)
- immediate early-stop if result is already decisive
- plain-language summary first
- advanced details optional

## Failure Handling
If commissioning cannot establish a reliable trio:
- return best safe pair
- lower aggressiveness for local settings
- default to conservative switch trigger
- present one-step next action, not a long diagnostics wall

## Acceptance Criteria
A commissioning run is acceptable if:
- finishes within selected budget profile caps
- produces a saved profile set and recommendation
- user can launch and continue with recommended setup without reading logs

## Implementation Note
This mode should reuse existing FluxMux runtime launch/health/switch infrastructure and benchmark helpers, adding only orchestration logic, budget control, and recommendation synthesis.
