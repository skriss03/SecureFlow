# SecureFlow

**AI-enabled resilience and threat modeling.** Feed it an architecture diagram from any tool (draw.io, Visio, Lucidchart, Mermaid, PlantUML/C4, Structurizr, Excalidraw, Gliffy, or an image / PDF / SVG export from Figma, Miro or anything else), a whiteboard photo, or a git repository. It rebuilds the architecture as a model, runs deterministic resilience and STRIDE rules with evidence, simulates blast radius, proposes fixes with AI, and re-scores live. Works at design time and as continuous feedback in CI.

Built for a one-day hackathon. Runs on one machine with one command.

## Why it is different

| | Microsoft Threat Modeling Tool | SecureFlow |
|---|---|---|
| Input | Draw by hand | **Any diagram tool** (draw.io, Visio, Lucidchart, Mermaid, PlantUML/C4, Structurizr, Excalidraw, Gliffy, images, PDF, SVG), photo, or **the repo itself** |
| Scope | Security (STRIDE) | **Resilience + Security** in one engine |
| Findings | Rule templates | Rules **with evidence** (`path:line`) plus AI context |
| Failure analysis | None | **Blast-radius simulation**: click a node, see what dies |
| Fixing | Manual | AI-proposed fix, **applied and re-scored live** |
| Continuous | No | `secureflow scan` from Maven, Gradle, npm, pip, dotnet or CI; report URL + build gate |
| AI dependency | n/a | Optional: rules, scores and simulation run without any key |

## Quick start (Windows)

```powershell
.\scripts\setup.ps1           # installs .NET 10, Node LTS, builds everything, runs tests
[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY', '<key>', 'User')   # or OPENAI_API_KEY
.\scripts\run.ps1             # http://localhost:5180
```

Without a key everything except image/repo extraction, the executive summary and fix proposals still works (use the built-in samples or a draw.io file).

Development mode with hot reload: `.\scripts\run.ps1 -Dev` (API on :5180, Vite on :5173).

## The loop

1. **Ingest**: image (vision model), repo (shallow clone → digest → model with file:line evidence), draw.io (deterministic parser, no AI), or JSON.
2. **Analyze**: 26 rules (14 resilience, 12 STRIDE) evaluate the model graph. Every finding names components, flows, evidence, rationale and mitigation.
3. **Score**: Resilience and Security 0–100 on a diminishing curve; an open Critical caps a score at 49, an open High at 74.
4. **Review** (AI): executive summary, most-likely-bad-day, top 5 consolidated risks, contextual findings the rules cannot see, quick wins.
5. **Simulate**: kill any component; synchronous callers without a circuit breaker go down, async or circuit-broken callers degrade. Deterministic and instant.
6. **Fix**: AI proposes a model patch plus copy-pasteable code (Polly/Resilience handlers, Kubernetes YAML, Terraform...). Apply it, watch the score move, export the report.

## Diagrams from any tool

Upload a diagram (web UI → *Diagram from any tool*, or `secureflow diagram <file>`). Files with structure are parsed exactly, with no AI and the same result every time:

- draw.io (`.drawio`, editable `.drawio.png` / `.drawio.svg`)
- Visio `.vsdx`, which also covers Lucidchart and Gliffy exported as Visio
- Mermaid (`.mmd`, or ```` ```mermaid ```` blocks in Markdown)
- PlantUML and C4-PlantUML (`.puml`)
- Structurizr DSL (`.dsl`)
- Excalidraw
- Gliffy

Anything else is read by the AI: images, PDF and SVG exports from Figma, Miro or any other tool, and whiteboard photos. Every format goes through the same shape-to-architecture mapping, so the same system drawn in different tools gets the same rule findings (the AI review on top may word a few contextual notes differently). `samples/diagrams/` holds one system in every format to show it.

## Scanning your own application

Install the CLI once (`scripts/install-cli.ps1` or `install-cli.sh`), then in **any** project folder, whatever the stack:

```
secureflow scan
```

It reads the project locally, redacts secrets, uploads to the SecureFlow server and prints `Report: <server>/p/<id>`: the same report the UI shows, at a URL that stays stable across rescans. `--fail-on high` turns it into a build gate. Moving from a local server to the shared OCP instance means setting `SECUREFLOW_SERVER` and `SECUREFLOW_TOKEN`; nothing in the project changes. Supported stacks: Java/Spring (Maven, Gradle), .NET, Python (FastAPI, Flask, Django), Angular, React, Node and Go, including CVEs from their manifests and lockfiles.

Optional wrappers run the same scan from a build tool: `mvn secureflow:scan`, `gradle secureflow`, `npm run secureflow`.

Guide, settings reference and the OCP checklist: **[docs/developer-integration.md](docs/developer-integration.md)**. Try it end to end with the deliberately flawed apps in `samples/consumers/`.

## Architecture

```
web/            React + Vite + React Flow (dagre layout, heat colouring, blast overlay)
src/
  SecureFlow.Core    canonical model, rule catalog, scoring, blast radius   (pure C#, unit-tested)
  SecureFlow.Ai      IArchitectureAi: Anthropic (primary) + OpenAI, JSON-schema output, replay cache
  SecureFlow.Ingest  repo digest (git clone --depth 1 + file selection), draw.io parser
  SecureFlow.Api     ASP.NET Core Minimal API, SSE progress, file-backed project store, markdown report
  SecureFlow.Cli     scan (upload + report URL) / config / analyze / simulate / rules / digest / drawio, exit code 2 for CI gates
clients/            thin wrappers that run the CLI: maven-plugin, gradle-plugin, npm, python
deploy/openshift/   Deployment, Service, Route, PVC, ConfigMap and Secret template (with the root Dockerfile)
docs/               developer integration guide
tests/SecureFlow.Core.Tests
samples/            demo repo, consumer apps per stack (samples/consumers), draw.io diagram, CI gate workflow
```

**Provider-agnostic AI.** `Ai:Provider` in `appsettings.json` (or the `Ai__Provider` env var) switches between `anthropic` (default `claude-opus-5`) and `openai`. Both use strict JSON-schema structured output so the model parses first time.

**Replay cache.** Every AI response is content-addressed and stored under `data/cache`. Identical inputs replay from disk. Rehearse once online, then run `.\scripts\run.ps1 -Offline` to prove the demo does not depend on the network.

**CI gate.** `samples/ci/secureflow-gate.yml` runs `secureflow scan --fail-on critical` on every pull request against your SecureFlow server.

## CLI

```
secureflow scan     [--path .] [--server URL] [--token T] [--fail-on high] [--out report.md] [--dry-run]
secureflow diagram  <file> [--dry-run]          # any diagram tool; --dry-run parses and runs the rules locally
secureflow config   # resolved settings and where each came from
secureflow analyze  --sample shopfast|healthy | --model model.json  [--json] [--fail-on critical|high|medium]
secureflow simulate --sample shopfast --kill orders-db
secureflow rules
secureflow digest   --repo <path-or-url>        # what the repo scanner would send to the model
secureflow drawio   --file samples/shopfast.drawio
```

Run with `dotnet run --project src/SecureFlow.Cli -- <args>`.

## Demo script (7 minutes)

1. Problem: threat modeling is manual, security-only, done once. (30s)
2. Upload the whiteboard photo. Watch the log stream. Model appears with boundaries. (60s)
3. Findings → Critical. Open "Single point of failure: Orders DB". Rule, evidence, mitigation. (60s)
4. Model tab → click Orders DB → Simulate failure. Half the diagram goes red. (45s)
5. Scan `samples/demo-repo` (or its GitHub URL). Evidence: `src/Orders/appsettings.json:4`. (90s)
6. Propose fix on "External dependency without a circuit breaker". Apply. Scores move. (60s)
7. Report tab: executive summary, top risks, print to PDF. (45s)
8. Architecture slide: hybrid rules + AI, provider-agnostic, CI gate. (30s)

## Verification

```powershell
dotnet test tests/SecureFlow.Core.Tests/SecureFlow.Core.Tests.csproj
dotnet run --project src/SecureFlow.Cli -- analyze --sample shopfast
dotnet run --project src/SecureFlow.Cli -- drawio --file samples/shopfast.drawio
dotnet run --project src/SecureFlow.Cli -- digest --repo samples/demo-repo
```
