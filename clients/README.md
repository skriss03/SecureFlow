# SecureFlow clients

The primary client is the `secureflow` CLI itself (`src/SecureFlow.Cli`), installed with `scripts/install-cli.ps1` / `install-cli.sh`: `secureflow scan` in any project. These folders are optional wrappers that run the same CLI from a build tool:

| Folder | Package | Command |
|---|---|---|
| `maven-plugin/` | `io.secureflow:secureflow-maven-plugin` | `mvn secureflow:scan` |
| `gradle-plugin/` | plugin id `io.secureflow` | `gradle secureflow` |
| `npm/` | `@secureflow/cli` | `npm run secureflow` (script: `secureflow scan`) |
| `python/` | `secureflow-cli` | `secureflow scan` |
| (dotnet tool) | `SecureFlow.Cli`, built from `src/SecureFlow.Cli` | `secureflow scan` |

The wrappers hold no scan logic. They find the native CLI and run it:
1. explicit path / `SECUREFLOW_CLI`
2. the pinned version cached in `~/.secureflow/cli/<version>/`
3. a download from `SECUREFLOW_CLI_BASE_URL/<version>/secureflow-<rid>`
4. `secureflow` on PATH, only when no base URL is set

They pass through only the settings you set, so env vars and `.secureflow.yml` still apply.

`scripts/publish-clients.ps1 -Wrappers` builds all of them into `artifacts/` and installs the Java plugins into `~/.m2`. Coordinates (`io.secureflow`, `@secureflow`) are placeholders: change them to your organisation's before publishing to Artifactory. The Maven and Gradle plugins each carry a copy of `CliLauncher.java`; keep the two in step.

Usage and settings: [docs/developer-integration.md](../docs/developer-integration.md).
