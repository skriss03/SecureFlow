# SecureFlow

**AI-enabled resilience and threat modeling.** Feed it an architecture diagram, a whiteboard photo, a draw.io file or a git repository. It rebuilds the architecture as a model, runs deterministic resilience and STRIDE rules with evidence, simulates blast radius, proposes fixes with AI, and re-scores live. Works at design time and as continuous feedback in CI.

Built for a one-day hackathon. Runs on one machine with one command.

## Why it is different

| | Microsoft Threat Modeling Tool | SecureFlow |
|---|---|---|
| Input | Draw by hand | Photo, diagram, draw.io, or **the repo itself** |
| Scope | Security (STRIDE) | **Resilience + Security** in one engine |
| Findings | Rule templates | Rules **with evidence** (`path:line`) plus AI context |
| Failure analysis | None | **Blast-radius simulation**: click a node, see what dies |
| Fixing | Manual | AI-proposed fix, **applied and re-scored live** |
| Continuous | No | CLI gate for PRs, model as code |
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

## Architecture

```
web/            React + Vite + React Flow (dagre layout, heat colouring, blast overlay)
src/
  SecureFlow.Core    canonical model, rule catalog, scoring, blast radius   (pure C#, unit-tested)
  SecureFlow.Ai      IArchitectureAi: Anthropic (primary) + OpenAI, JSON-schema output, replay cache
  SecureFlow.Ingest  repo digest (git clone --depth 1 + file selection), draw.io parser
  SecureFlow.Api     ASP.NET Core Minimal API, SSE progress, file-backed project store, markdown report
  SecureFlow.Cli     analyze / simulate / rules / digest / drawio, exit code 2 for CI gates
tests/SecureFlow.Core.Tests
samples/            demo repo with deliberate flaws, draw.io diagram, CI gate workflow
```

**Provider-agnostic AI.** `Ai:Provider` in `appsettings.json` (or the `Ai__Provider` env var) switches between `anthropic` (default `claude-opus-5`) and `openai`. Both use strict JSON-schema structured output so the model parses first time.

**Replay cache.** Every AI response is content-addressed and stored under `data/cache`. Identical inputs replay from disk. Rehearse once online, then run `.\scripts\run.ps1 -Offline` to prove the demo does not depend on the network.

**Model as code.** Download the model JSON from the Report tab, commit it, and gate pull requests with `samples/ci/secureflow-gate.yml`.

## CLI

```
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
