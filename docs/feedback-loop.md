# Continuous-Learning Feedback Loop — Privacy & Opt‑In Guide

The assessment tool can **optionally** learn from the outcomes of past migrations to refine
future recommendations. When enabled, it records **anonymized, aggregate** metrics about a
completed migration, correlates them with the original assessment inputs, and surfaces a
*"based on N prior similar migrations"* rationale in the generated reports.

This document is the authoritative privacy statement for that feature: what is and is not
collected, how consent works, how to opt in and out, where data lives, and how to delete it.

---

## TL;DR

- **Off by default.** Nothing is collected unless you explicitly opt in.
- **No PII, no customer data.** No account/database/container names, no documents, no
  credentials, no endpoints, no connection strings, no free‑text — by construction.
- **Local first.** Data is stored only on your machine unless you deliberately configure a
  remote telemetry endpoint.
- **You stay in control.** Opt out at any time; delete the local file at any time.

---

## Privacy by design

The feedback schema is designed so that leaking identifying information is *structurally*
impossible, not merely discouraged:

- **Data minimization.** Only the aggregate signals needed to compare workloads and learn from
  outcomes are recorded (sizes, counts, complexity, recommended/deployed platform & tier,
  and success/cost/performance outcomes).
- **No free‑text fields.** Every string property is constrained to a fixed, tool‑generated
  value set (e.g. `"Azure SQL Database"`, `"General Purpose"`). There is no place to put a
  name, comment, or identifier — and a reflection‑based unit test enforces that no
  unapproved string field is ever added to the schema.
- **Categorical / bucketed values.** Where exact numbers could be sensitive, the optional
  telemetry payload is *coarsened* into buckets (see below).
- **Non‑correlatable record id.** Each record carries a fresh random `OutcomeId` (a GUID) that
  is not derived from, and cannot be linked back to, any account, tenant, or user.

---

## What **is** collected

When you opt in and record a migration outcome, the local record (`MigrationOutcome`) contains
only the following anonymized, aggregate fields:

| Category | Fields | Example |
| --- | --- | --- |
| **Schema / record** | `SchemaVersion`, `OutcomeId` (random GUID), `RecordedAtUtc` | `1`, `a1b2…`, `2026-01-01T00:00:00Z` |
| **Workload profile** (similarity fingerprint) | `ComplexityRating`, `ContainerCount`, `SizeBucket`, `TotalDocumentCount`, `TotalDataSizeGb`, `MaxProvisionedRUs`, `IndexRecommendationCount`, `RecommendedPlatform`, `RecommendedTier` | `Medium`, `3`, `Medium`, `500000`, `4.7`, `4000`, `5`, `Azure SQL Database`, `General Purpose` |
| **Success** | `Status`, `ActualMigrationDays`, `DataCompletenessPercent` | `Succeeded`, `4`, `100` |
| **Performance** | `DeployedPlatform`, `DeployedTier`, `AvgResourceUtilizationPercent`, `PeakResourceUtilizationPercent`, `AvgQueryLatencyMs`, `PerformanceSatisfactory` | `Azure SQL Database`, `General Purpose`, `42`, `78`, `12`, `true` |
| **Cost (actual vs estimate)** | `EstimatedMonthlyCostUsd`, `ActualMonthlyCostUsd`, `EstimatedMigrationCostUsd`, `ActualMigrationCostUsd` | `1200`, `1100`, `5000`, `5200` |

All platform/tier strings come from the tool's own recommendation value set; the rating/status
fields are enums.

### Optional remote telemetry (coarsened)

If — and only if — you configure a `FeedbackLoop:TelemetryEndpoint`, an additional **coarsened**
summary (`CoarsenedOutcome`) is POSTed there alongside the local write. It is *more* aggregated
than the local record: exact counts and dollar amounts are replaced with **buckets**.

| Field | What it is |
| --- | --- |
| `SchemaVersion` | Schema version integer |
| `ComplexityRating`, `SizeBucket` | Workload complexity & size bucket |
| `ContainerCountBucket` | Bucketed container count (a range, not an exact number) |
| `RecommendedPlatform` / `RecommendedTier` | Tool‑generated labels |
| `DeployedPlatform` / `DeployedTier` | Tool‑generated labels |
| `Status`, `PerformanceSatisfactory` | Outcome status and a boolean |
| `MonthlyCostVarianceBucket` | Bucketed cost variance (a range, not a dollar amount) |
| `MigrationDaysBucket` | Bucketed migration duration (a range) |

No telemetry endpoint is configured by default, so **nothing leaves your machine** unless you
add one yourself.

---

## What is **NOT** collected

The following are **never** recorded or transmitted, in either the local or telemetry payload:

- Cosmos DB **account, database, or container names**
- **Document data**, field values, sample data, or query text
- **Credentials**, connection strings, keys, tokens, or endpoints/URLs
- **IP addresses**, hostnames, usernames, tenant or subscription identifiers
- Any **free‑text** notes or comments
- Any other **personally identifiable information (PII)** or customer data

---

## How consent works

Consent is **default‑OFF**. The effective decision is resolved from several inputs using a
**privacy‑first precedence** (highest wins):

1. **Environment opt‑out** — `COSMOS2SQL_FEEDBACK_OPTOUT=1` *(absolute; always wins)*
2. **Command line** — `--enable-feedback` / `--disable-feedback`
3. **Configuration** — `FeedbackLoop:Enabled` (`true` / `false`)
4. **Environment opt‑in** — `COSMOS2SQL_FEEDBACK_OPTIN=1` *(only applies when nothing above expresses a preference)*
5. **Default** — disabled (nothing is collected)

The absolute opt‑out mirrors the convention used by `DOTNET_CLI_TELEMETRY_OPTOUT`: setting it
guarantees collection is off, regardless of any other setting.

On every run the tool prints a transparent consent notice stating the current status, where
data is stored, exactly what is and isn't collected, and how to change your choice.

---

## How to opt **in**

Pick whichever fits your workflow:

- **Per run (CLI):**
  ```bash
  CosmosToSqlAssessment --enable-feedback
  ```
- **Persistent (configuration)** in `appsettings.json` (or environment‑specific overrides):
  ```json
  {
    "FeedbackLoop": {
      "Enabled": true
    }
  }
  ```
- **Environment variable:**
  ```bash
  # Windows (PowerShell)
  $env:COSMOS2SQL_FEEDBACK_OPTIN = "1"
  # Linux / macOS
  export COSMOS2SQL_FEEDBACK_OPTIN=1
  ```

## Recording a migration outcome (`feedback record`)

Opting in only *permits* collection — to actually populate the local store with how a migration
turned out, import an anonymized outcome with the additive subcommand:

```bash
CosmosToSqlAssessment feedback record --import-outcome outcome.json --enable-feedback
```

- `--import-outcome <file.json>` points at a JSON document that deserializes to a
  `MigrationOutcome` (the same anonymized, aggregate schema described above — no names, endpoints,
  credentials, or PII). Property names are matched case-insensitively.
- The command **reuses the existing opt-in consent** (it does not introduce a new data category).
  With consent granted (via `--enable-feedback`, `FeedbackLoop:Enabled=true`, or the opt-in env
  var) the outcome is appended to the store at the location shown below, and — if a telemetry
  endpoint is configured — a coarsened summary is sent too.
- With consent **denied** (the default), the command is a **no-op**: it prints the consent status,
  records nothing, and exits successfully (`0`). A missing or malformed file exits `1`.

Recorded outcomes are read back automatically the next time an assessment runs, so the
recommendation refinement (see below) can attribute results to *N prior similar migrations*.

## How to opt **out**

Opt‑out always wins. Any of these disables collection:

- **Per run (CLI):** `--disable-feedback`
- **Configuration:** `"FeedbackLoop": { "Enabled": false }`
- **Absolute environment opt‑out:**
  ```bash
  # Windows (PowerShell)
  $env:COSMOS2SQL_FEEDBACK_OPTOUT = "1"
  # Linux / macOS
  export COSMOS2SQL_FEEDBACK_OPTOUT=1
  ```

> Passing both `--enable-feedback` and `--disable-feedback` in the same invocation is rejected
> as a configuration error.

---

## Where your data is stored

By default, outcomes are appended to a per‑user, local **JSON‑lines** file:

| OS | Default location |
| --- | --- |
| Windows | `%LOCALAPPDATA%\CosmosToSqlAssessment\feedback\migration-outcomes.jsonl` |
| Linux / macOS | `$XDG_DATA_HOME` (or `~/.local/share`) `/CosmosToSqlAssessment/feedback/migration-outcomes.jsonl` |

You can override the path with `FeedbackLoop:StorePath`. The exact location is shown in the
consent notice printed at runtime.

### Deleting / purging your data

The data is yours and stays on your machine. To delete it, simply remove the file:

```bash
# Windows (PowerShell)
Remove-Item "$env:LOCALAPPDATA\CosmosToSqlAssessment\feedback\migration-outcomes.jsonl"
# Linux / macOS
rm ~/.local/share/CosmosToSqlAssessment/feedback/migration-outcomes.jsonl
```

There is no central server, no account, and nothing to request deletion from — unless you
explicitly configured a `TelemetryEndpoint`, in which case data retention/deletion at that
endpoint is governed by whoever operates it.

> **Reading is not consent‑gated.** Refinement reads your *own* previously stored local
> outcomes to improve your *own* recommendations even when collection is currently disabled.
> Opt‑out stops *new* writes and any transmission; deleting the file removes the history.

---

## How refined recommendations are attributed

When enough comparable history exists, the report includes a
**"Recommendations Based on Prior Migrations"** section. The tool:

1. Builds a `WorkloadProfile` "fingerprint" for the current assessment.
2. Finds prior **successful** outcomes whose profiles are sufficiently *similar* (a weighted
   similarity score over complexity, size, container count, and provisioned RUs).
3. Requires a minimum number of comparable samples before it will adjust anything — otherwise
   it defers to the baseline and says so.
4. Ranks the candidate platform/tier configurations by a conservative lower‑bound of their
   observed satisfaction rate, and reports whether prior outcomes **confirm** or **refine** the
   baseline, along with a confidence level.

The rendered rationale always states the sample size it is based on, e.g.:

> *"Based on 6 prior similar migrations, Azure SQL Managed Instance (Business Critical)
> performed satisfactorily."*

This keeps every learned recommendation **transparent and attributable** — you can always see
how many prior migrations informed it and how confident the tool is.

---

## Summary of settings

| Setting | Type | Default | Effect |
| --- | --- | --- | --- |
| `--enable-feedback` | CLI flag | — | Opt in for this run |
| `--disable-feedback` | CLI flag | — | Opt out for this run |
| `feedback record --import-outcome <file>` | CLI subcommand | — | Import an anonymized outcome into the store (consent‑gated; no‑op when denied) |
| `FeedbackLoop:Enabled` | config (bool?) | unset → off | Persistent opt in/out |
| `FeedbackLoop:StorePath` | config (string) | per‑user path | Override local store location |
| `FeedbackLoop:TelemetryEndpoint` | config (string) | unset | If set, also POST a coarsened summary |
| `COSMOS2SQL_FEEDBACK_OPTOUT` | env var | unset | `1`/`true` → absolute opt out (always wins) |
| `COSMOS2SQL_FEEDBACK_OPTIN` | env var | unset | `1`/`true` → opt in when nothing else decides |

---

## Related documentation

- [Configuration](configuration.md)
- [Usage](usage.md)
- [Architecture](architecture.md)
