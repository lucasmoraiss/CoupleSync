# CoupleSync AI Spec-Driven Development Flow

## Objective
Use repository artifacts as the single coordination source for AI agents, replacing ad-hoc implementation with deterministic state transitions and auditable outputs.

## Workflow Modes
- Full mode: INTAKE -> DESIGN -> APPROVE_DESIGN -> PLAN -> REVIEW_STRATEGY -> IMPLEMENT_LOOP -> INTEGRATE -> RELEASE -> DONE
- Lean mode (current bootstrap): INTAKE_LEAN -> IMPLEMENT_LOOP -> INTEGRATE -> DONE

## Current Session
- Session folder: .agents-work/2026-04-13_couplesync-ai-driven-bootstrap/
- Current state: INTAKE_LEAN
- Core artifacts initialized: spec.md, acceptance.json, status.json, tasks.yaml

## AI Artifact Contract
- spec.md: product scope, requirements, constraints, assumptions, risks, done definition.
- acceptance.json: machine-readable acceptance criteria with verification methods.
- tasks.yaml: executable backlog for implementation state transitions.
- status.json: session state machine and checkpoint ledger.
- report.md: final delivery summary with run/test instructions and known issues.

## Agent Handoff Rules
- SpecAgent creates initial scope and acceptance baseline.
- Architect defines architecture and major technical decisions (full mode).
- Planner decomposes tasks into mergeable units (full mode or post-lean expansion).
- Coder implements task goals and updates task status to implemented.
- Reviewer, QA, Security gate each task (or final batch strategy).
- Integrator validates build and release readiness.
- Docs finalizes operator/developer documentation.

## State Integrity Rules
- status.json must always be valid JSON.
- tasks.yaml is the only source of per-task status.
- All user decisions must be persisted before state transition.
- Every acceptance criterion must be traceable to one or more checks.

## Practical Next Transition
- Option A (continue lean): keep T-001 and iterate with Coder/Reviewer/QA/Security.
- Option B (upgrade to full): run Architect then Planner to replace single-task file with multi-task backlog.

## Required Output Discipline
- Every agent output should include status, summary, artifacts, gates, and next recommendations.
- Every implementation change must update affected artifacts in the current session folder.
