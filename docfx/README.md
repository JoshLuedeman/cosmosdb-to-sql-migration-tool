# API reference site (DocFX)

This folder contains the [DocFX](https://dotnet.github.io/docfx/) configuration that
generates the **public API reference** for the Cosmos DB to SQL migration tool from the
XML documentation comments in `CosmosToSqlAssessment.csproj`.

The generated output is **not** committed — it is produced on demand locally and by CI
(`.github/workflows/docs.yml`) and published to GitHub Pages under
`/api/` by the publish job. `docfx/_site/` and `docfx/reference/` are gitignored.

## Prerequisites

- .NET 8 SDK
- DocFX is pinned as a local dotnet tool (`.config/dotnet-tools.json`), so no global
  install is needed — just restore it.

## Generate the site

From the repository root:

```bash
dotnet tool restore                       # installs the pinned DocFX version
dotnet docfx docfx/docfx.json             # metadata + build -> docfx/_site
```

Use `--warningsAsErrors` to fail on any broken cross-reference or link (this is what CI
runs):

```bash
dotnet docfx docfx/docfx.json --warningsAsErrors
```

## Preview locally

Build and serve with a local web server, then open the printed URL:

```bash
dotnet docfx docfx/docfx.json --serve
```

## Layout

| Path | Purpose |
|------|---------|
| `docfx.json` | DocFX configuration (metadata source + build/template settings). |
| `index.md` | Site landing page. |
| `toc.yml` | Top navigation bar. |
| `reference/` | **Generated** API YAML model (gitignored). |
| `_site/` | **Generated** static HTML site (gitignored). |

Only the public API surface is documented. The CLI front end, dependency-injection
composition root, and run orchestrator are `internal` and intentionally excluded.
