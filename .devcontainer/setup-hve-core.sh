#!/usr/bin/env bash
# Best-effort installer for the GitHub Copilot CLI and the hve-core-all plugin
# (sourced from github.com/microsoft/hve-core).
#
# Design goals:
#   * NON-FATAL  - the dev container / Codespace / Copilot cloud sandbox MUST still
#                  start even if every step below fails. Each step is guarded with
#                  `|| echo "::warning::..."` (or `|| true`) so a failure only warns.
#   * IDEMPOTENT - re-running is safe: the CLI is only installed when missing and
#                  re-adding an existing marketplace / plugin is a harmless no-op.
#
# It is invoked from both .devcontainer/devcontainer.json (postCreateCommand) and
# .github/workflows/copilot-setup-steps.yml (the "Install hve-core-all plugin" step).
set -euo pipefail

echo "==> Setting up GitHub Copilot CLI + hve-core-all plugin"

# Make npm's global bin directory visible in this shell so a freshly installed
# `copilot` resolves immediately (non-fatal when npm is unavailable).
if command -v npm >/dev/null 2>&1; then
  if npm_prefix="$(npm prefix -g 2>/dev/null)"; then
    npm_global_bin="${npm_prefix}/bin"
    case ":${PATH}:" in
      *":${npm_global_bin}:"*) : ;;
      *) export PATH="${npm_global_bin}:${PATH}" ;;
    esac
  fi
fi

# 1) Install the Copilot CLI only if it is not already on PATH (idempotent).
command -v copilot >/dev/null 2>&1 || npm install -g @github/copilot || echo "::warning::copilot CLI install failed"

# 2) Register the hve-core marketplace, then install the hve-core-all plugin.
copilot plugin marketplace add microsoft/hve-core || echo "::warning::marketplace add failed"
copilot plugin install hve-core-all@hve-core       || echo "::warning::hve-core-all install failed"

# 3) List installed plugins for verification (never fatal).
copilot plugin list || true

echo "==> hve-core-all setup script finished"
