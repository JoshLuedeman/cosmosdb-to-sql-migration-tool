# End-to-End Test Harness

_Placeholder — populated by sub-issue **#181** under parent **#125**._

This folder will host the **full-pipeline E2E test harness** that wires real
services (with the mock Azure SDK layer from `../Mocks/`) end-to-end:

```
CosmosDbAnalysisService
    -> DataQualityAnalysisService
    -> SqlMigrationAssessmentService
    -> DataFactoryEstimateService
    -> SqlProjectGenerationService
    -> ReportGenerationService
```

E2E tests must:

- Run with **no Azure resources** (use the `Mocks/` harness).
- Complete in **< 60 seconds** total.
- Be the smoke-test entry point every Wave-2+ parent runs before declaring done.

See parent issue #125 and the `../Mocks/README.md` for context.
