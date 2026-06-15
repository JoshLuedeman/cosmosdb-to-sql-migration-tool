# Security Policy

The `cosmosdb-to-sql-migration-tool` repository takes security seriously. This document describes the security scanning we run, the thresholds that gate merges, and how to report a vulnerability privately.

## Supported Versions

Only the latest tagged release of the tool is actively supported with security fixes. Older releases may be patched on a best-effort basis when the fix is trivial.

## Reporting a Vulnerability

If you believe you have found a security vulnerability in this project, **please do not open a public GitHub issue.** Instead, use GitHub's private vulnerability reporting flow:

1. Go to the repository's [**Security** tab](https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/security).
2. Click **Report a vulnerability**.
3. Provide a clear description of the issue, including reproduction steps and the affected version.

You should receive an acknowledgement within 7 days. If you do not, please follow up via a GitHub Discussion (without disclosing the vulnerability details).

## Automated security scanning

Every pull request that targets `main` runs four security checks. Each check is also re-run on a weekly schedule against `main` so we catch vulnerabilities that surface in already-merged dependencies.

| # | Tool | Workflow | Triggers | Fails on | Rationale |
|---|---|---|---|---|---|
| 1 | **CodeQL SAST** (csharp + actions) | [`codeql.yml`](.github/workflows/codeql.yml) | push/PR to `main`, weekly cron, dispatch | Build/analysis errors only — security alerts surface in the **Security → Code scanning** tab | Tuning false-positives against the `security-extended` + `security-and-quality` suites is best done as alert triage, not as a hard merge gate. PRs that *introduce* new alerts are surfaced inline. |
| 2 | **Dependency Review** | [`dependency-review.yml`](.github/workflows/dependency-review.yml) (`dependency-review` job) | PR to `main` | A PR-diff dependency change that introduces a **High** or **Critical** CVE in any scope (`runtime`, `development`, `unknown`) | Conventional baseline. Policy lives in [`.github/dependency-review-config.yml`](.github/dependency-review-config.yml) so it is a single source of truth and easy to tighten over time. |
| 3 | **NuGet Audit** | [`dependency-review.yml`](.github/workflows/dependency-review.yml) (`nuget-audit` job) | PR to `main`, weekly cron, dispatch | Any **High** or **Critical** vulnerability reported by `dotnet list package --vulnerable --include-transitive` (including transitive deps) | Catches CVEs in already-resolved deps that the PR diff didn't touch. Severity threshold matches Dependency Review so the two share one effective baseline. |
| 4 | **Gitleaks Secret Scan** | [`secret-scan.yml`](.github/workflows/secret-scan.yml) | push/PR to `main`, weekly cron, dispatch | **Any** leaked secret detected anywhere in git history | Secrets are binary — there is no "low severity" leaked credential. Config lives in [`.gitleaks.toml`](.gitleaks.toml). |

GitHub-native push protection and secret scanning are also enabled on this public repository at no cost, providing a second layer of defense before commits even reach the remote.

## Triaging findings

### CodeQL alerts (Security → Code scanning)
- New alerts on PRs appear inline as PR review comments.
- Open the alert, follow the **Show paths** link to understand the dataflow, and either fix the code or **Dismiss** with a reason (`False positive`, `Used in tests`, `Won't fix`).
- Never dismiss without a reason — the reason is the audit trail for future maintainers.

### Dependency Review / NuGet Audit failures
- Read the PR comment (Dependency Review posts on failure) or the workflow log (NuGet Audit prints each violating package with its CVE link).
- Preferred fix order:
  1. Bump the direct dependency to a patched version (`dotnet add package <name>`).
  2. If the vuln is in a transitive dep, bump the parent direct dep, or pin the transitive via `<PackageReference>` in the project file.
  3. If no fix is available, comment on the PR explaining the temporary acceptance and open a follow-up issue tagged `security`.

### Gitleaks findings
- Inspect the leaked credential and **rotate it immediately** if it was ever real. Assume any value committed to git history is compromised.
- If the match is a genuine false positive (e.g., a documentation example), add an entry to `[[allowlists]]` in `.gitleaks.toml` with a description explaining why.
- Never bypass a gitleaks failure without rotating + allowlisting.

## Contributor guidance

See [CONTRIBUTING.md](CONTRIBUTING.md#security-requirements) for the developer-facing summary: how to run scans locally, how to fix common findings, and how to add false-positive allowlists in a reviewable way.
