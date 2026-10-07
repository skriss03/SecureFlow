using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SecureFlow.Ai;
using SecureFlow.Api.Auth;
using SecureFlow.Api.Options;
using SecureFlow.Api.Projects;
using SecureFlow.Cli;
using SecureFlow.Core.Model;
using SecureFlow.Core.Samples;
using SecureFlow.Ingest;
using Xunit;

namespace SecureFlow.Core.Tests;

/// <summary>
/// The developer-integration path: manifest parsing for each stack, secret redaction, client
/// configuration precedence, and the server pieces a CLI upload relies on (project keys, rescans,
/// retention, token auth, report URLs).
/// </summary>
public class DeveloperIntegrationTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "secureflow-devint-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { /* temp dir */ }
    }

    private static string Sample(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SecureFlow.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(new[] { dir ?? ".", "samples" }.Concat(parts).ToArray());
    }

    private static List<DependencyRef> ParseSample(params string[] parts) =>
        ManifestParser.Parse(parts[^1], File.ReadAllText(Sample(parts)));

    private static bool Has(IEnumerable<DependencyRef> deps, string eco, string name, string version) =>
        deps.Any(d => d.Ecosystem == eco && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && d.Version == version);

    // ---------- manifests, one per stack ----------

    [Fact]
    public void Maven_pom_resolves_property_versions_and_the_parent()
    {
        var deps = ParseSample("consumers", "spring-boot-maven", "pom.xml");
        Assert.True(Has(deps, "Maven", "org.apache.logging.log4j:log4j-core", "2.14.1"), "${log4j.version} should resolve from <properties>");
        Assert.True(Has(deps, "Maven", "org.springframework.boot:spring-boot-starter-parent", "2.6.5"));
        Assert.True(Has(deps, "Maven", "org.postgresql:postgresql", "42.2.18"));
        // Versions managed by the parent BOM are unknowable without resolving it, so they are skipped, not guessed.
        Assert.DoesNotContain(deps, d => d.Name.EndsWith(":spring-boot-starter-web"));
    }

    [Fact]
    public void Gradle_build_file_and_version_catalog_are_parsed()
    {
        var build = ParseSample("consumers", "spring-boot-gradle", "build.gradle");
        Assert.True(Has(build, "Maven", "org.yaml:snakeyaml", "1.29"));
        Assert.True(Has(build, "Maven", "org.springframework.boot:spring-boot", "2.5.12"), "Boot plugin version decides the framework BOM");
        Assert.True(Has(build, "Maven", "org.postgresql:postgresql", "42.3.1"));

        var catalog = ParseSample("consumers", "spring-boot-gradle", "gradle", "libs.versions.toml");
        Assert.True(Has(catalog, "Maven", "com.fasterxml.jackson.core:jackson-databind", "2.9.10"));
    }

    [Fact]
    public void Npm_lockfiles_include_transitive_dependencies()
    {
        var lockDeps = ParseSample("consumers", "angular-app", "package-lock.json");
        Assert.True(Has(lockDeps, "npm", "lodash", "4.17.15"), "the lockfile pins what ^4.17.15 resolved to");
        Assert.True(Has(lockDeps, "npm", "tslib", "1.14.1"), "nested node_modules paths are transitive deps");
        Assert.True(Has(lockDeps, "npm", "@angular/core", "11.0.5"));

        var yarn = ParseSample("consumers", "react-app", "yarn.lock");
        Assert.True(Has(yarn, "npm", "follow-redirects", "1.14.0"));
        Assert.True(Has(yarn, "npm", "@secureflow/cli", "0.1.0"), "scoped names keep their leading @");

        var pnpm = ManifestParser.Parse("pnpm-lock.yaml", "lockfileVersion: '9.0'\n\npackages:\n\n  axios@0.21.0:\n    resolution: {integrity: x}\n\n  '@scope/pkg@1.2.3':\n    resolution: {integrity: y}\n\nsnapshots:\n\n  axios@0.21.0:\n    dependencies: {}\n");
        Assert.True(Has(pnpm, "npm", "axios", "0.21.0"));
        Assert.True(Has(pnpm, "npm", "@scope/pkg", "1.2.3"));
    }

    [Fact]
    public void Python_requirements_pyproject_and_poetry_lock_are_parsed()
    {
        var req = ParseSample("consumers", "fastapi-app", "requirements.txt");
        Assert.True(Has(req, "PyPI", "requests", "2.19.1"));
        Assert.True(Has(req, "PyPI", "PyYAML", "5.3.1"));

        var pyproject = ParseSample("consumers", "django-app", "pyproject.toml");
        Assert.True(Has(pyproject, "PyPI", "Django", "3.2.0"));
        Assert.True(Has(pyproject, "PyPI", "Pillow", "8.1.0"), "exact Poetry pins count");
        Assert.DoesNotContain(pyproject, d => d.Name == "python");

        var poetry = ParseSample("consumers", "django-app", "poetry.lock");
        Assert.True(Has(poetry, "PyPI", "urllib3", "1.26.4"));
    }

    [Fact]
    public void Dotnet_and_go_manifests_are_parsed()
    {
        var central = ManifestParser.Parse("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"12.0.1\" /></ItemGroup></Project>");
        Assert.True(Has(central, "NuGet", "Newtonsoft.Json", "12.0.1"));

        var nugetLock = ManifestParser.Parse("packages.lock.json", "{\"version\":1,\"dependencies\":{\"net8.0\":{\"System.Text.Json\":{\"type\":\"Transitive\",\"resolved\":\"8.0.0\"},\"MyLib\":{\"type\":\"Project\"}}}}");
        Assert.True(Has(nugetLock, "NuGet", "System.Text.Json", "8.0.0"));
        Assert.Single(nugetLock);

        var go = ManifestParser.Parse("go.mod", "module x\n\nrequire github.com/gin-gonic/gin v1.6.0\n\nrequire (\n\tgolang.org/x/net v0.0.1 // indirect\n)\n");
        Assert.True(Has(go, "Go", "github.com/gin-gonic/gin", "v1.6.0"));
        Assert.True(Has(go, "Go", "golang.org/x/net", "v0.0.1"));
    }

    [Fact]
    public void A_malformed_manifest_yields_nothing_instead_of_throwing()
    {
        Assert.Empty(ManifestParser.Parse("package-lock.json", "{ not json"));
        Assert.Empty(ManifestParser.Parse("pom.xml", "<project><unclosed>"));
    }

    [Fact]
    public async Task Repo_digest_carries_resolved_dependencies_for_cve_scanning()
    {
        var ingest = new RepoIngest(new RepoIngestOptions { WorkDir = Path.Combine(_dataDir, "tmp") });
        var digest = await ingest.BuildAsync(Sample("consumers", "angular-app"), null, null, CancellationToken.None);
        Assert.Contains(digest.Dependencies, d => d.Name == "lodash" && d.Version == "4.17.15" && d.ManifestPath == "package-lock.json");
        // Lockfiles feed the dependency list only; they never take AI budget as file content.
        Assert.DoesNotContain(digest.Files, f => f.Path == "package-lock.json");
        Assert.Contains(digest.Files, f => f.Path == "src/environments/environment.prod.ts");
    }

    // ---------- redaction ----------

    [Fact]
    public void Redaction_removes_literal_secrets_but_keeps_keys_references_and_line_numbers()
    {
        var redactor = new SecretRedactor(new RedactionOptions());
        const string yaml = "spring:\n  datasource:\n    password: hunter2\n    username: app\napi:\n  token: ${API_TOKEN}\n";
        var (text, count) = redactor.Redact("application.yml", yaml);
        Assert.Equal(1, count);
        Assert.Contains("password: ***REDACTED***", text);
        Assert.Contains("token: ${API_TOKEN}", text);
        Assert.Contains("username: app", text);
        Assert.Equal(yaml.Split('\n').Length, text.Split('\n').Length);

        var (json, jsonCount) = redactor.Redact("appsettings.json", "{ \"ConnectionStrings\": { \"Db\": \"Server=db;User Id=sa;Password=Pa55w0rd;\" }, \"ApiKey\": \"abc123def\" }");
        Assert.Equal(2, jsonCount);
        Assert.DoesNotContain("Pa55w0rd", json);
        Assert.DoesNotContain("abc123def", json);

        var (code, codeCount) = redactor.Redact("settings.py", "SECRET_KEY = os.environ['SECRET_KEY']\nDB_PASSWORD = \"literal-pass\"\n");
        Assert.Equal(1, codeCount);
        Assert.Contains("os.environ['SECRET_KEY']", code);

        var (url, _) = redactor.Redact("main.py", "engine = create_engine(\"postgresql://u:pw123@db/x\")");
        Assert.Contains("postgresql://u:***REDACTED***@db/x", url);
    }

    [Fact]
    public void Redaction_can_be_disabled_by_configuration()
    {
        var (text, count) = new SecretRedactor(new RedactionOptions { Enabled = false }).Redact("a.env", "PASSWORD=x");
        Assert.Equal(0, count);
        Assert.Equal("PASSWORD=x", text);
    }

    // ---------- configuration ----------

    [Fact]
    public void Ingest_option_defaults_match_the_original_constants()
    {
        var o = new RepoIngestOptions();
        Assert.Equal(480_000, o.BudgetChars);
        Assert.Equal(24_000, o.PerFileCapChars);
        Assert.Equal(140, o.MaxFiles);
        Assert.Equal(40, o.MaxWiringFiles);
        Assert.Contains("node_modules", o.EffectiveSkipDirs);
    }

    [Fact]
    public async Task Extra_priority_patterns_from_configuration_pull_in_more_files()
    {
        var dir = Path.Combine(_dataDir, "custom");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "topology.cfg"), "orders -> payments");
        var plain = await new RepoIngest(new RepoIngestOptions { WorkDir = Path.Combine(_dataDir, "tmp") }).BuildAsync(dir, null, null, CancellationToken.None);
        Assert.DoesNotContain(plain.Files, f => f.Path == "topology.cfg");

        var custom = await new RepoIngest(new RepoIngestOptions { WorkDir = Path.Combine(_dataDir, "tmp"), ExtraPriorityPatterns = [@"\.cfg$"] }).BuildAsync(dir, null, null, CancellationToken.None);
        Assert.Contains(custom.Files, f => f.Path == "topology.cfg");
    }

    [Fact]
    public void Mini_yaml_reads_flat_nested_and_list_settings()
    {
        var y = MiniYaml.Parse("""
            # comment
            server: "http://sf:5180"   # trailing comment
            group: Payments Team
            ingest:
              maxFiles: 50
              extraSkipDirs:
                - generated
                - legacy
              extraWiringSignals: [GrpcChannel, "@GrpcClient"]
            redaction:
              enabled: false
            """);
        Assert.Equal("http://sf:5180", y["server"]);
        Assert.Equal("Payments Team", y["group"]);
        Assert.Equal("50", y["ingest.maxFiles"]);
        Assert.Equal(new List<string> { "generated", "legacy" }, y["ingest.extraSkipDirs"]);
        Assert.Equal(new List<string> { "GrpcChannel", "@GrpcClient" }, y["ingest.extraWiringSignals"]);
        Assert.Equal("false", y["redaction.enabled"]);
        Assert.False(y.ContainsKey("ingest"));
    }

    [Fact]
    public void Client_settings_resolve_flag_then_env_then_repo_file_then_default()
    {
        var repo = Path.Combine(_dataDir, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".secureflow.yml"), "group: from-file\nname: file-name\nfailOn: high\n");
        var previousGroup = Environment.GetEnvironmentVariable("SECUREFLOW_GROUP");
        var previousConfig = Environment.GetEnvironmentVariable("SECUREFLOW_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("SECUREFLOW_CONFIG", Path.Combine(_dataDir, "no-user-file.yml"));
            Environment.SetEnvironmentVariable("SECUREFLOW_GROUP", "from-env");
            var cfg = ClientConfig.Load(new[] { "scan", "--fail-on", "critical" }, repo);

            Assert.Equal("critical", cfg.Get("failOn"));       // flag beats file
            Assert.StartsWith("flag", cfg.Source("failOn"));
            Assert.Equal("from-env", cfg.Get("group"));        // env beats file
            Assert.Equal("file-name", cfg.Get("name"));        // file beats default
            Assert.Equal("http://localhost:5180", cfg.Server); // default
            Assert.Equal("default", cfg.Source("server"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SECUREFLOW_GROUP", previousGroup);
            Environment.SetEnvironmentVariable("SECUREFLOW_CONFIG", previousConfig);
        }
    }

    [Fact]
    public void Env_var_and_flag_names_are_derived_from_setting_keys()
    {
        Assert.Equal("SECUREFLOW_PROJECT_KEY", ClientConfig.EnvName("projectKey"));
        Assert.Equal("SECUREFLOW_INGEST_BUDGET_CHARS", ClientConfig.EnvName("ingest.budgetChars"));
        Assert.Equal("project-key", ClientConfig.FlagName("projectKey"));
        Assert.Equal("insecure-skip-tls-verify", ClientConfig.FlagName("insecureSkipTlsVerify"));
    }

    // ---------- server ----------

    private (ProjectStore Store, ProjectPipeline Pipeline) NewServer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDir"] = _dataDir })
            .Build();
        var store = new ProjectStore(config, new TestEnv(), NullLogger<ProjectStore>.Instance);
        var pipeline = new ProjectPipeline(store, new AiRegistry { Error = "no provider in tests" }, NullLogger<ProjectPipeline>.Instance);
        return (store, pipeline);
    }

    [Fact]
    public void Rule_analysis_never_retires_vulnerability_findings()
    {
        var (_, pipeline) = NewServer();
        var p = new Project { Model = SampleModels.ShopFast() };
        pipeline.Analyze(p, "first");
        p.Findings.Add(new Finding { Id = "Vabc", RuleId = "CVE-2021-44228", Category = FindingCategory.Vulnerability, Source = FindingSource.Rule, Severity = Severity.Critical, Status = FindingStatus.Open, Title = "log4j" });

        pipeline.Analyze(p, "second");

        var cve = Assert.Single(p.Findings, f => f.Id == "Vabc");
        Assert.Equal(FindingStatus.Open, cve.Status);
    }

    [Fact]
    public async Task Rescan_keeps_the_project_id_and_adds_history()
    {
        var (store, pipeline) = NewServer();
        var p = new Project { Name = "orders", Key = "orders@main" };
        pipeline.Start(p, (_, _) => Task.FromResult(SampleModels.ShopFast()));
        await WaitFor(() => p.Status == ProjectStatus.Ready);
        var historyAfterFirst = p.History.Count;

        Assert.Same(p, store.FindByKey("ORDERS@main"));
        pipeline.Rescan(p, (_, _) => Task.FromResult(SampleModels.Healthy()));
        await WaitFor(() => p.Status == ProjectStatus.Ready && p.ScanCount == 2);

        Assert.Equal(2, p.ScanCount);
        Assert.True(p.History.Count > historyAfterFirst);
        Assert.Contains(p.Findings, f => f.Status == FindingStatus.Fixed); // ShopFast problems the healthy model no longer has
    }

    [Fact]
    public void Retention_removes_only_finished_projects_past_the_cutoff()
    {
        var (store, _) = NewServer();
        var old = store.Add(new Project { Name = "old", Status = ProjectStatus.Ready });
        var running = store.Add(new Project { Name = "running", Status = ProjectStatus.Extracting });
        var fresh = store.Add(new Project { Name = "fresh", Status = ProjectStatus.Ready });
        old.UpdatedAt = running.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-31);

        var service = new RetentionService(store, new SecureFlowOptions { Retention = { Days = 30 } }, NullLogger<RetentionService>.Instance);
        var removed = service.Sweep();

        Assert.Equal(new[] { old.Id }, removed);
        Assert.Null(store.Get(old.Id));
        Assert.NotNull(store.Get(running.Id));
        Assert.NotNull(store.Get(fresh.Id));
    }

    [Theory]
    [InlineData(false, "POST", null, 200)]
    [InlineData(true, "POST", null, 401)]
    [InlineData(true, "POST", "Bearer wrong", 401)]
    [InlineData(true, "POST", "Bearer t1", 200)]
    [InlineData(true, "GET", null, 200)]
    public async Task Token_auth_guards_writes_only_when_enabled(bool enabled, string method, string? header, int expected)
    {
        var options = new SecureFlowOptions { Auth = { Enabled = enabled, Tokens = ["t1"] } };
        var middleware = new TokenAuthMiddleware(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; }, options);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = "/api/scans";
        ctx.Response.Body = new MemoryStream();
        if (header is not null) ctx.Request.Headers.Authorization = header;

        await middleware.InvokeAsync(ctx);
        Assert.Equal(expected, ctx.Response.StatusCode);
    }

    [Fact]
    public void Report_urls_use_the_configured_public_base_url_or_the_request()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost", 5180);

        Assert.Equal("http://localhost:5180/p/abc", new SecureFlowOptions().ReportUrl(ctx.Request, "abc"));
        Assert.Equal("https://secureflow.apps.ocp.example.com/p/abc",
            new SecureFlowOptions { PublicBaseUrl = "https://secureflow.apps.ocp.example.com/" }.ReportUrl(ctx.Request, "abc"));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "condition not reached in time");
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SecureFlow.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
