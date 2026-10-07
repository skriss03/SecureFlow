# Scanning your application with SecureFlow

**One command, any stack:** in your project folder, run

```
secureflow scan
```

It works the same for Java/Spring (Maven or Gradle), .NET, Python (FastAPI, Flask, Django), Angular, React, Node and Go. You get a resilience, threat-model and CVE report at a URL you can open, share and come back to.

What happens when you run it (the command prints each step as it goes):

```
[1/3] Reading the project on this machine       nothing is compiled or executed; secret values are
                                                replaced by ***REDACTED*** before anything leaves
[2/3] Uploading to the SecureFlow server        only the selected config/deployment/wiring files and
                                                the list of dependency versions
[3/3] Server is analysing                       AI rebuilds the architecture, 26 resilience + STRIDE
                                                rules, CVE check of every pinned dependency
      Report: http://<server>/p/<id>            same URL every time you rescan this repo + branch
```

Want to see exactly what would be sent first? Run `secureflow scan --dry-run`: it sends nothing.

## 1. Install once

Windows (PowerShell):

```powershell
.\scripts\install-cli.ps1                     # local test: installs from this repo's artifacts/ folder
# from Artifactory:  .\scripts\install-cli.ps1 -BaseUrl https://artifactory.example.com/artifactory/generic-local/secureflow
```

Linux / macOS:

```sh
SECUREFLOW_CLI_BASE_URL=https://artifactory.example.com/artifactory/generic-local/secureflow sh scripts/install-cli.sh
```

This puts a single self-contained `secureflow` executable in `~/.secureflow/bin` and adds it to your PATH. No admin rights are needed, and the CLI runs without .NET, Java, Node or Python. Open a new terminal and check:

```
secureflow --version
```

Which server to use:

| | Local test | Shared server (OCP) |
|---|---|---|
| Server | `http://localhost:5180` (the default; nothing to set) | `SECUREFLOW_SERVER=https://secureflow.apps.<cluster>` |
| Token | not needed | `SECUREFLOW_TOKEN=<your team token>` |

You can set these once in `~/.secureflow/config.yml`:

```yaml
server: https://secureflow.apps.example.com
token: <your team token>
```

**Moving from local to the shared server is only this change.** Nothing in your project needs editing.

## 2. Scan

```
cd <your project>
secureflow scan
```

Useful options (same for every stack):

| Option | What it does |
|---|---|
| `--dry-run` | Show what would be sent, without sending anything |
| `--open` | Open the report in your browser when it's ready |
| `--fail-on high` | Exit with code 2 if an open finding is High or Critical (for gates) |
| `--group "Payments Team"` | Put the project on that team's dashboard |
| `--out report.md` | Also save the report as Markdown |
| `--path services/orders` | Scan a sub-folder (one module of a monorepo) |

### Optional: run it from your build tool

The CLI is all you need. Teams that want the scan inside their build, for example as a gate on `mvn verify` or with nothing installed on the machine, can use the wrapper for their tool. Every wrapper runs the same `secureflow scan`, so the report is identical. Each one is named `secureflow`:

| Build tool | Add to the project | Run |
|---|---|---|
| Maven | the plugin in `<build><plugins>` (below) | `mvn secureflow:scan` |
| Gradle | `id 'io.secureflow' version '0.1.0'` | `gradle secureflow` |
| npm (Angular, React, Node) | devDependency `@secureflow/cli` and a script `"secureflow": "secureflow scan"` | `npm run secureflow` |
| pip (Python) | `pip install secureflow-cli` | `secureflow scan` |
| dotnet | `dotnet tool install -g SecureFlow.Cli` | `secureflow scan` |

#### Maven

```xml
<build>
  <plugins>
    <plugin>
      <groupId>io.secureflow</groupId>
      <artifactId>secureflow-maven-plugin</artifactId>
      <version>0.1.0</version>
    </plugin>
  </plugins>
</build>
```

The plugin has no execution, so `mvn package` never runs it. To gate a build, bind it in a profile and run `mvn verify -Psecureflow`. See `samples/consumers/spring-boot-maven/pom.xml`. Each setting is available as a `-Dsecureflow.*` property, for example `-Dsecureflow.failOn=high`.

#### Gradle

```groovy
// settings.gradle
pluginManagement { repositories { maven { url 'https://artifactory.example.com/artifactory/gradle' }; gradlePluginPortal() } }
// build.gradle
plugins { id 'io.secureflow' version '0.1.0' }
secureflow { group = 'Payments Team' }      // optional; -Psecureflow.<name> overrides
```

The task isn't attached to `build` or `check`. To make it part of `check`, add `check.dependsOn tasks.secureflow`.

#### npm (Angular, React, Node)

```json
"devDependencies": { "@secureflow/cli": "^0.1.0" },
"scripts": { "secureflow": "secureflow scan" }
```

### In the IDE

No plugin is needed. Run `secureflow scan --open` from the IDE's terminal, or save it as a one-click action:
- **IntelliJ:** add a Shell Script run configuration (or a Maven/Gradle one for `secureflow:scan` / `secureflow`).
- **VS Code:** add a task:
  ```json
  { "label": "SecureFlow scan", "type": "shell", "command": "secureflow scan --open" }
  ```
- **Visual Studio:** add an External Tool with command `secureflow`, arguments `scan --open` and initial directory `$(SolutionDir)`.

## 3. What you get

```
orders-service  (scan #3, main @ 1a2b3c4)
  Resilience 58 (D)   Security 41 (F)
  Open findings: 22  (3 Critical, 6 High, 11 Medium, 2 Low)   of which CVEs: 14
  Report: https://secureflow.apps.example.com/p/d6977a8a87
```

- **The same URL every time.** Repeat scans of the same repo and branch update one project, so the link you shared stays valid. The report shows the score history, and findings you marked Accepted stay Accepted. The key defaults to `<repo>[/<subfolder>]@<branch>`; override it with `projectKey`.
- **Group dashboards.** `group` puts the project on that team's dashboard.
- **Build gate.** `failOn` (`none` | `low` | `medium` | `high` | `critical`) exits with code 2 and fails Maven/Gradle/npm when an open finding is at or above the threshold.
- **Saved copy.** `--out report.md` also saves the Markdown report, for example as a CI artifact.

What each stack contributes:

| Stack | Architecture signals read | CVE manifests |
|---|---|---|
| Java / Spring | `pom.xml`, `build.gradle(.kts)`, `application*.yml/properties`, `bootstrap.yml`; RestTemplate, WebClient, Feign, Kafka/Rabbit listeners, JdbcTemplate, Resilience4j, Spring Security | `pom.xml` (incl. `${property}` versions and the parent), `build.gradle(.kts)`, `gradle/libs.versions.toml` |
| .NET | `*.csproj`, `Program.cs`, `appsettings*.json`; HttpClient, EF Core, Service Bus, Polly, JwtBearer | `*.csproj`, `Directory.Packages.props`, `packages.lock.json` |
| Python / Django | `requirements*.txt`, `pyproject.toml`, `settings.py`, `urls.py`, `wsgi/asgi.py`, `.env`; requests/httpx, SQLAlchemy, Celery, `DATABASES`, `CACHES`, `DEBUG`, `ALLOWED_HOSTS` | `requirements*.txt` (pinned), `pyproject.toml`, `poetry.lock`, `Pipfile.lock` |
| Angular / React / Node | `package.json`, `angular.json`, `environment*.ts`, `proxy.conf.json`, `vite/next.config.*`, `.env*`; HttpClient, fetch, axios | `package.json`, `package-lock.json`, `yarn.lock`, `pnpm-lock.yaml` (transitive) |
| Any | Dockerfile, compose, Kubernetes/OpenShift, Helm, Terraform, Bicep, CI pipelines | `go.mod` |

A monorepo is scanned in one run. Scan its root to get one model across all services, or scan a module folder (`--path services/orders`) to get a report per module.

Versions inherited from a parent BOM (Spring Boot starters without a `<version>`) are only checked when a lockfile or explicit pin is present. SecureFlow does not guess versions.

## 3b. Analysing a diagram (any tool)

You don't need the code to get an analysis. Upload the architecture diagram you already have:

```
secureflow diagram docs/architecture.vsdx --open
secureflow diagram architecture.mmd --dry-run      # parse + rules locally, nothing sent
```

In the web UI, use **Diagram from any tool**.

| Tool | File | How it's read |
|---|---|---|
| draw.io / diagrams.net | `.drawio`, editable `.drawio.png` / `.drawio.svg` | parsed exactly, no AI |
| Microsoft Visio | `.vsdx` | parsed exactly (stencils, connectors, containers) |
| Lucidchart | File → Export → Visio (`.vsdx`) | parsed exactly, via the Visio reader |
| Mermaid | `.mmd`, `.mermaid`, ```` ```mermaid ```` blocks in `.md` | parsed exactly (flowchart, architecture-beta, C4) |
| PlantUML / C4-PlantUML | `.puml`, `.plantuml` | parsed exactly |
| Structurizr DSL | `.dsl` | parsed exactly |
| Excalidraw | `.excalidraw` | parsed exactly |
| Gliffy (Confluence / Jira) | `.gliffy` | parsed exactly (best effort; falls back to the AI) |
| Figma, Miro, Canva, PowerPoint, anything else | PNG / JPEG / PDF / SVG export | read by the AI |
| Visio `.vsd` (old), Figma `.fig` | | not readable: save as `.vsdx` / export PNG, SVG or PDF |

What "parsed exactly" means:
- Shapes, labels, arrows and zones are read from the file itself, with no AI and the same result every time.
- The same system drawn in two tools gives the same findings.
- Text formats (Mermaid, PlantUML, Structurizr) cite `file:line` as evidence.

The AI is used only for pictures and exports without structure. It is also the fallback when a structured file has nothing the parser can use.

How the diagram is read:
- **Component types** come from the shape or stencil (a cylinder or "Database" stencil is a database) and from words in the label ("Redis", "Kafka", "Key Vault").
- **Annotations** in labels are picked up, for example `x2` (replicas), `multi-AZ`, `backup`, `HTTPS`, `JWT`, `mTLS` and `no auth`.
- **Dashed arrows** are treated as asynchronous.
- **Boxes drawn around other boxes** (zones, subgraphs, Visio containers) become trust boundaries.

Adding the replica count, protocol and auth to your labels gives a much more precise report.

`samples/diagrams/` has one system (NorthBank Payments) in every format, with notes on how each file was made and which ones were checked in their real tool.

## 4. Settings reference

Each setting is resolved in this order:
1. the command-line flag
2. the `SECUREFLOW_*` environment variable
3. `.secureflow.yml` in the repo (searched upward to the git root)
4. `~/.secureflow/config.yml` (or the file named by `SECUREFLOW_CONFIG`)
5. the built-in default

Run `secureflow config` to see the value in effect and where it came from.

| Key (`.secureflow.yml`) | Flag | Env var | Default |
|---|---|---|---|
| `server` | `--server` | `SECUREFLOW_SERVER` | `http://localhost:5180` |
| `token` | `--token` | `SECUREFLOW_TOKEN` | — |
| `group` | `--group` | `SECUREFLOW_GROUP` | — |
| `projectKey` | `--project-key` | `SECUREFLOW_PROJECT_KEY` | `<repo>[/<subdir>]@<branch>` |
| `name` | `--name` | `SECUREFLOW_NAME` | repo or folder name |
| `failOn` | `--fail-on` | `SECUREFLOW_FAIL_ON` | `none` |
| `wait` | `--wait` / `--no-wait` | `SECUREFLOW_WAIT` | `true` |
| `timeoutSeconds` | `--timeout-seconds` | `SECUREFLOW_TIMEOUT_SECONDS` | `900` |
| `out` | `--out` | `SECUREFLOW_OUT` | — |
| `open` | `--open` | `SECUREFLOW_OPEN` | `false` |
| `insecureSkipTlsVerify` | `--insecure-skip-tls-verify` | `SECUREFLOW_INSECURE_SKIP_TLS_VERIFY` | `false` |
| `ingest.budgetChars`, `ingest.maxFiles`, `ingest.perFileCapChars` | `--ingest-budget-chars`, ... | `SECUREFLOW_INGEST_BUDGET_CHARS`, ... | server defaults |
| `ingest.extraSkipDirs`, `ingest.extraPriorityPatterns`, `ingest.extraWiringSignals` | (list, comma-separated) | same pattern | — |
| `redaction.enabled`, `redaction.keyPattern` | `--no-redact` | `SECUREFLOW_REDACTION_ENABLED` | `true` |

Locating the CLI (wrappers only): `SECUREFLOW_CLI` (explicit path), `SECUREFLOW_CLI_BASE_URL` (download folder), `SECUREFLOW_CLI_VERSION` (defaults to the wrapper's version). The pinned version always wins over a `secureflow` found on PATH.

Example `.secureflow.yml`. Keep the server and token out of the repo:

```yaml
name: orders-service
group: Payments Team
failOn: high
ingest:
  extraSkipDirs: [generated, legacy]
  extraWiringSignals:
    - GrpcChannel
```

## 5. Server settings (for the SecureFlow owner)

All settings are in `src/SecureFlow.Api/appsettings.json`. Each one can be overridden by an environment variable, written as `Section__Key`.

| Setting | Purpose | Local | OCP |
|---|---|---|---|
| `SecureFlow__PublicBaseUrl` | host used in report links | request host | Route host |
| `SecureFlow__Auth__Enabled`, `SecureFlow__Auth__Tokens__N` | bearer tokens for write calls (`ProtectReads` also guards reads) | off | on; tokens from a Secret |
| `SecureFlow__Retention__Enabled`, `__Days` | delete projects idle for N days | off | on, 30 |
| `SecureFlow__Upload__MaxDigestBytes` | largest accepted upload | 10 MB | 10 MB |
| `SecureFlow__Scan__AutoDependencyScan` | CVE check on every scan | on | on |
| `SecureFlow__Scan__ReuseProjectByKey` | same key updates the same project | on | on |
| `SecureFlow__Scan__AllowWithoutAi` | accept uploads with no AI provider (CVE-only report) | on | your choice |
| `Diagrams__MaxUploadBytes`, `__AiFallback`, `__AiTextFallback`, `__DisabledFormats__N`, `__ExtraTypeKeywords__<Type>__N` | diagram upload limit, AI fallback, formats to switch off, extra words that mark a component type | defaults | defaults |
| `DependencyScan__OsvBaseUrl` | OSV API or internal mirror | osv.dev | mirror if no egress |
| `Ingest__*` | digest budgets, skip dirs, extra patterns | defaults | defaults |

## 6. Moving to OCP

1. Build and push the image: `docker build -t <registry>/secureflow:<version> .` (base images are build args, so they can come from an internal mirror).
2. Create the secret with the AI key and team tokens (see `deploy/openshift/secret.example.yaml`).
3. Set the Route host in `deploy/openshift/secureflow.yaml` and `SecureFlow__PublicBaseUrl` in `deploy/openshift/configmap.yaml`, then run `oc apply -f deploy/openshift/`.
4. Publish the clients to Artifactory:
   - Run `scripts/publish-clients.ps1 -Wrappers`.
   - Upload `artifacts/cli/<version>/*` and `scripts/install-cli.ps1` / `install-cli.sh` to a generic repo.
   - Deploy the Maven plugin: `mvn deploy -DaltDeploymentRepository=...`.
   - Publish the Gradle plugin: `gradle publish -Psecureflow.publishUrl=...`.
   - Publish the npm `.tgz`, the Python wheel and the NuGet package to their Artifactory repos.
5. Tell developers: run the installer once, set `SECUREFLOW_SERVER` and `SECUREFLOW_TOKEN`, then `secureflow scan` in any project. Everything else stays the same as the local test.

## 7. Trying it locally (end to end)

```powershell
.\scripts\run.ps1                         # 1. server on http://localhost:5180
.\scripts\publish-clients.ps1             # 2. build the CLI (add -Wrappers for the Maven/Gradle/npm/pip wrappers)
.\scripts\install-cli.ps1                 # 3. put `secureflow` on your PATH; open a new terminal afterwards

cd samples\consumers\spring-boot-maven     # 4. any project, any stack
secureflow scan --open
```

The apps in `samples/consumers/` (Spring Maven, Spring Gradle, Angular, React, FastAPI, Django) each have deliberate flaws and at least one known-vulnerable dependency pinned. The Maven sample, for example, pins log4j 2.14.1, so Log4Shell appears as a Critical CVE.

Without an AI key on the server, the report covers dependency CVEs only and says that the architecture was not analysed. Set `ANTHROPIC_API_KEY` (or OpenAI or Foundry) for the full model, rules and scores.
