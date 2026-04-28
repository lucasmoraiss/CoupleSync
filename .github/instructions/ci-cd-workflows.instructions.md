---
description: "Use when writing, modifying, or debugging GitHub Actions workflows, CI/CD pipelines, or deployment configurations."
applyTo: ".github/workflows/**"
---
# CI/CD Conventions (CoupleSync)

## Current Pipeline
- Single workflow: `.github/workflows/ci.yml`
- Triggers: push to `main`, pull requests to `main`
- Services: PostgreSQL 16 for integration/E2E tests

## Pipeline Steps
1. Checkout → .NET 8 setup → Restore → Build
2. Apply EF Core migrations to test DB
3. Unit tests → Integration tests → E2E tests
4. Node.js setup → Mobile type-check (`npx tsc --noEmit`)
5. TruffleHog secret scan
6. Docker build validation

## MCP Tools for CI/CD
When checking CI status or debugging failures, use the `github-actions` MCP server:
- `list_workflow_runs` with `workflow: "ci.yml"` to check recent runs
- `get_job_logs` to read failure logs
- `validate_workflow_file` to lint workflow YAML before committing
- `rerun_workflow` with `only_failed: true` to retry without full rebuild

## Rules
- Never bypass CI checks — all PRs must pass before merge
- Keep pipeline under 10 minutes
- Use `--no-build` flag for test steps after build step
- Secret scan is mandatory — no verified secrets in commits
