using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SecureFlow.Ai;

namespace SecureFlow.Ingest;

/// <summary>
/// Reads dependency manifests and lockfiles as text and returns concrete (ecosystem, name, version) pins.
/// Never runs a package manager or any repo code. Ranges, tags and unresolved variables are skipped: OSV
/// needs one concrete version, and guessing one would produce false positives.
/// Ecosystem names match OSV.dev: npm, NuGet, PyPI, Go, Maven.
/// </summary>
public static class ManifestParser
{
    public static bool IsManifest(string path)
    {
        var name = Path.GetFileName(path);
        return name.ToLowerInvariant() switch
        {
            "package.json" or "package-lock.json" or "npm-shrinkwrap.json" or "yarn.lock" or "pnpm-lock.yaml" => true,
            "directory.packages.props" or "packages.lock.json" => true,
            "requirements.txt" or "pipfile.lock" or "poetry.lock" or "pyproject.toml" => true,
            "go.mod" => true,
            "pom.xml" or "build.gradle" or "build.gradle.kts" or "libs.versions.toml" => true,
            _ => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                 || Regex.IsMatch(name, @"^requirements[-_.\w]*\.txt$", RegexOptions.IgnoreCase),
        };
    }

    /// <summary>Ecosystem a manifest feeds, for enabling/disabling ecosystems by configuration.</summary>
    public static string? EcosystemOf(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name is "package.json" or "package-lock.json" or "npm-shrinkwrap.json" or "yarn.lock" or "pnpm-lock.yaml") return "npm";
        if (name is "directory.packages.props" or "packages.lock.json" || name.EndsWith(".csproj")) return "NuGet";
        if (name is "pipfile.lock" or "poetry.lock" or "pyproject.toml" || name.StartsWith("requirements")) return "PyPI";
        if (name is "go.mod") return "Go";
        if (name is "pom.xml" or "build.gradle" or "build.gradle.kts" or "libs.versions.toml") return "Maven";
        return null;
    }

    /// <summary>Parses one manifest. A malformed file yields an empty list rather than an exception.</summary>
    public static List<DependencyRef> Parse(string relPath, string content)
    {
        try
        {
            var name = Path.GetFileName(relPath).ToLowerInvariant();
            var deps = name switch
            {
                "package.json" => PackageJson(content),
                "package-lock.json" or "npm-shrinkwrap.json" => PackageLock(content),
                "yarn.lock" => YarnLock(content),
                "pnpm-lock.yaml" => PnpmLock(content),
                "directory.packages.props" => MsBuildPackages(content, "PackageVersion"),
                "packages.lock.json" => NuGetLock(content),
                "pipfile.lock" => PipfileLock(content),
                "poetry.lock" => PoetryLock(content),
                "pyproject.toml" => PyProject(content),
                "go.mod" => GoMod(content),
                "pom.xml" => Pom(content),
                "build.gradle" or "build.gradle.kts" => Gradle(content),
                "libs.versions.toml" => GradleVersionCatalog(content),
                _ when name.EndsWith(".csproj") => MsBuildPackages(content, "PackageReference"),
                _ when name.StartsWith("requirements") => Requirements(content),
                _ => new List<(string, string, string)>(),
            };
            return deps
                .Where(d => d.Item2.Length > 0 && d.Item3.Length > 0)
                .Select(d => new DependencyRef { Ecosystem = d.Item1, Name = d.Item2, Version = d.Item3, ManifestPath = relPath })
                .ToList();
        }
        catch
        {
            return new List<DependencyRef>();
        }
    }

    /// <summary>One entry per (ecosystem, name, version), keeping the first manifest that declared it.</summary>
    public static List<DependencyRef> Distinct(IEnumerable<DependencyRef> deps) =>
        deps.GroupBy(d => (d.Ecosystem, d.Name.ToLowerInvariant(), d.Version)).Select(g => g.First()).ToList();

    private static readonly Regex ConcreteVersion = new(@"^v?\d+(\.\d+){0,3}([-.+][0-9A-Za-z.\-+]*)?$", RegexOptions.Compiled);

    // ---------- npm ----------

    private static List<(string, string, string)> PackageJson(string content)
    {
        var result = new List<(string, string, string)>();
        using var doc = JsonDocument.Parse(content);
        foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies" })
        {
            if (!doc.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in deps.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String && CleanNpmVersion(prop.Value.GetString()!) is { } v)
                    result.Add(("npm", prop.Name, v));
        }
        return result;
    }

    private static string? CleanNpmVersion(string raw)
    {
        raw = raw.Trim();
        // Skip ranges/tags/git/workspace refs we can't resolve to one concrete version.
        if (raw.Length == 0 || raw is "*" or "latest" || raw.Contains(' ') || raw.Contains("||")
            || raw.StartsWith("git") || raw.StartsWith("file:") || raw.StartsWith("workspace:") || raw.StartsWith("link:") || raw.StartsWith("npm:"))
            return null;
        raw = raw.TrimStart('^', '~', '>', '<', '=').Trim();
        return Regex.IsMatch(raw, @"^\d+(\.\d+){1,3}") ? raw : null;
    }

    private static List<(string, string, string)> PackageLock(string content)
    {
        var result = new List<(string, string, string)>();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
        {
            // lockfileVersion 2/3: keys are install paths, e.g. "node_modules/@scope/a/node_modules/b".
            foreach (var p in packages.EnumerateObject())
            {
                var idx = p.Name.LastIndexOf("node_modules/", StringComparison.Ordinal);
                if (idx < 0) continue; // "" is the root project itself
                if (p.Value.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.True) continue;
                var name = p.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : p.Name[(idx + "node_modules/".Length)..];
                if (p.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && ConcreteVersion.IsMatch(v.GetString()!))
                    result.Add(("npm", name, v.GetString()!));
            }
        }
        else if (root.TryGetProperty("dependencies", out var deps))
        {
            // lockfileVersion 1: nested "dependencies" objects.
            void Walk(JsonElement obj)
            {
                foreach (var d in obj.EnumerateObject())
                {
                    if (d.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && ConcreteVersion.IsMatch(v.GetString()!))
                        result.Add(("npm", d.Name, v.GetString()!));
                    if (d.Value.TryGetProperty("dependencies", out var nested) && nested.ValueKind == JsonValueKind.Object) Walk(nested);
                }
            }
            if (deps.ValueKind == JsonValueKind.Object) Walk(deps);
        }
        return result;
    }

    private static List<(string, string, string)> YarnLock(string content)
    {
        // Classic (v1):  "@scope/a@^1.0.0", "@scope/a@^1.1.0":\n  version "1.2.3"
        // Berry (v2+):   "@scope/a@npm:^1.0.0":\n  version: 1.2.3
        var result = new List<(string, string, string)>();
        string? current = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!char.IsWhiteSpace(line[0]) && line.EndsWith(':'))
            {
                var spec = line.TrimEnd(':').Split(',')[0].Trim().Trim('"');
                var at = spec.IndexOf('@', 1); // skip the leading @ of a scoped name
                current = at > 0 ? spec[..at] : null;
                continue;
            }
            if (current is null) continue;
            var m = Regex.Match(line, @"^\s+version:?\s+""?([^""\s]+)""?\s*$");
            if (m.Success)
            {
                if (ConcreteVersion.IsMatch(m.Groups[1].Value)) result.Add(("npm", current, m.Groups[1].Value));
                current = null;
            }
        }
        return result;
    }

    private static List<(string, string, string)> PnpmLock(string content)
    {
        // v5:  /name/1.2.3:   v6:  /name@1.2.3:   v9:  name@1.2.3:   (each may carry a "(peer@x)" suffix)
        var result = new List<(string, string, string)>();
        var inPackages = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith(' ') && line.EndsWith(':')) { inPackages = line is "packages:" or "snapshots:"; continue; }
            if (!inPackages) continue;
            var m = Regex.Match(line, @"^  '?/?((?:@[^/@\s]+/)?[^/@\s(']+)[@/]([0-9][^(:'\s]*)");
            if (m.Success && ConcreteVersion.IsMatch(m.Groups[2].Value)) result.Add(("npm", m.Groups[1].Value, m.Groups[2].Value));
        }
        return result;
    }

    // ---------- NuGet ----------

    private static List<(string, string, string)> MsBuildPackages(string content, string element)
    {
        var result = new List<(string, string, string)>();
        foreach (Match m in Regex.Matches(content,
            $@"<{element}\s+[^>]*Include\s*=\s*""([^""]+)""[^>]*Version\s*=\s*""([^""]+)""|<{element}\s+[^>]*Version\s*=\s*""([^""]+)""[^>]*Include\s*=\s*""([^""]+)"""))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
            var version = (m.Groups[1].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim('[', ']', ' ');
            if (Regex.IsMatch(version, @"^\d+(\.\d+){1,3}")) result.Add(("NuGet", name, version));
        }
        return result;
    }

    private static List<(string, string, string)> NuGetLock(string content)
    {
        var result = new List<(string, string, string)>();
        using var doc = JsonDocument.Parse(content);
        if (!doc.RootElement.TryGetProperty("dependencies", out var tfms)) return result;
        foreach (var tfm in tfms.EnumerateObject())
            foreach (var pkg in tfm.Value.EnumerateObject())
                if (pkg.Value.TryGetProperty("resolved", out var r) && r.ValueKind == JsonValueKind.String
                    && !(pkg.Value.TryGetProperty("type", out var t) && t.GetString() == "Project"))
                    result.Add(("NuGet", pkg.Name, r.GetString()!));
        return result;
    }

    // ---------- PyPI ----------

    private static List<(string, string, string)> Requirements(string content)
    {
        var result = new List<(string, string, string)>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Split('#')[0].Split(';')[0].Trim();
            var m = Regex.Match(line, @"^([A-Za-z0-9_.\-]+)(\[[^\]]*\])?\s*===?\s*([0-9][0-9A-Za-z.+\-]*)$");
            if (m.Success) result.Add(("PyPI", m.Groups[1].Value, m.Groups[3].Value));
        }
        return result;
    }

    private static List<(string, string, string)> PipfileLock(string content)
    {
        var result = new List<(string, string, string)>();
        using var doc = JsonDocument.Parse(content);
        foreach (var section in new[] { "default", "develop" })
        {
            if (!doc.RootElement.TryGetProperty(section, out var pkgs) || pkgs.ValueKind != JsonValueKind.Object) continue;
            foreach (var p in pkgs.EnumerateObject())
                if (p.Value.TryGetProperty("version", out var v) && v.GetString() is { } s && s.StartsWith("=="))
                    result.Add(("PyPI", p.Name, s[2..]));
        }
        return result;
    }

    private static List<(string, string, string)> PoetryLock(string content)
    {
        var result = new List<(string, string, string)>();
        foreach (var block in content.Split("[[package]]").Skip(1))
        {
            var name = Regex.Match(block, @"^name\s*=\s*""([^""]+)""", RegexOptions.Multiline);
            var version = Regex.Match(block, @"^version\s*=\s*""([^""]+)""", RegexOptions.Multiline);
            if (name.Success && version.Success) result.Add(("PyPI", name.Groups[1].Value, version.Groups[1].Value));
        }
        return result;
    }

    private static List<(string, string, string)> PyProject(string content)
    {
        var result = new List<(string, string, string)>();
        // PEP 621: dependencies = ["fastapi==0.110.0", ...] (also optional-dependencies groups).
        foreach (Match m in Regex.Matches(content, @"""([A-Za-z0-9_.\-]+)(\[[^\]]*\])?\s*==\s*([0-9][0-9A-Za-z.+\-]*)\s*(;[^""]*)?"""))
            result.Add(("PyPI", m.Groups[1].Value, m.Groups[3].Value));
        // Poetry: [tool.poetry.dependencies] name = "1.2.3" (exact pins only; ^ and ~ are ranges).
        var section = Regex.Match(content, @"\[tool\.poetry\.(?:group\.\w+\.)?dependencies\]([\s\S]*?)(?=^\[|\z)", RegexOptions.Multiline);
        if (section.Success)
            foreach (Match m in Regex.Matches(section.Groups[1].Value, @"^([A-Za-z0-9_.\-]+)\s*=\s*""=?=?([0-9][0-9A-Za-z.+\-]*)""", RegexOptions.Multiline))
                if (!m.Groups[1].Value.Equals("python", StringComparison.OrdinalIgnoreCase))
                    result.Add(("PyPI", m.Groups[1].Value, m.Groups[2].Value));
        return result;
    }

    // ---------- Go ----------

    private static List<(string, string, string)> GoMod(string content)
    {
        var result = new List<(string, string, string)>();
        foreach (Match m in Regex.Matches(content, @"^\s*(?:require\s+)?([a-zA-Z0-9._/\-]+\.[a-zA-Z0-9._/\-]+)\s+(v[0-9][0-9A-Za-z.+\-]*)\s*(?://.*)?$", RegexOptions.Multiline))
            result.Add(("Go", m.Groups[1].Value, m.Groups[2].Value));
        return result;
    }

    // ---------- Maven / Gradle ----------

    private static List<(string, string, string)> Pom(string content)
    {
        var result = new List<(string, string, string)>();
        var doc = XDocument.Parse(content);
        var root = doc.Root!;
        IEnumerable<XElement> Kids(XElement e, string local) => e.Elements().Where(x => x.Name.LocalName == local);
        string? Val(XElement e, string local) => Kids(e, local).FirstOrDefault()?.Value.Trim();

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Kids(root, "properties").SelectMany(x => x.Elements()))
            props[p.Name.LocalName] = p.Value.Trim();
        if (Val(root, "version") is { } pv) props["project.version"] = pv;
        var parent = Kids(root, "parent").FirstOrDefault();
        if (parent is not null && Val(parent, "version") is { } parentV) props["project.parent.version"] = parentV;

        string? Resolve(string? v)
        {
            for (var i = 0; v is not null && v.Contains("${") && i < 5; i++)
                v = Regex.Replace(v, @"\$\{([^}]+)\}", m => props.TryGetValue(m.Groups[1].Value, out var r) ? r : m.Value);
            return v is null || v.Contains("${") ? null : v;
        }

        // Spring Boot's parent POM is a dependency too, and the one that pins most framework versions.
        if (parent is not null && Val(parent, "groupId") is { } pg && Val(parent, "artifactId") is { } pa && Resolve(Val(parent, "version")) is { } pver)
            result.Add(("Maven", $"{pg}:{pa}", pver));

        foreach (var dep in root.Descendants().Where(e => e.Name.LocalName == "dependency"))
        {
            var g = Val(dep, "groupId"); var a = Val(dep, "artifactId"); var v = Resolve(Val(dep, "version"));
            // Versions inherited from a BOM or parent are not knowable without resolving the parent; skip them.
            if (g is null || a is null || v is null || v.StartsWith('[') || v.StartsWith('(')) continue;
            result.Add(("Maven", $"{g}:{a}", v));
        }
        return result;
    }

    private static readonly Regex GradleCoordinate = new(
        @"(?:implementation|api|compileOnly|runtimeOnly|testImplementation|testRuntimeOnly|annotationProcessor|kapt|classpath|developmentOnly)\s*\(?\s*[""']([\w.\-]+):([\w.\-]+):([\w.\-]+)(?::[\w.\-]+)?[""']",
        RegexOptions.Compiled);

    private static List<(string, string, string)> Gradle(string content)
    {
        var result = new List<(string, string, string)>();
        foreach (Match m in GradleCoordinate.Matches(content))
            if (ConcreteVersion.IsMatch(m.Groups[3].Value)) result.Add(("Maven", $"{m.Groups[1].Value}:{m.Groups[2].Value}", m.Groups[3].Value));
        // Plugin DSL: id 'org.springframework.boot' version '3.1.0' → the Boot BOM decides framework versions.
        foreach (Match m in Regex.Matches(content, @"id\s*\(?\s*[""']org\.springframework\.boot[""']\s*\)?\s*version\s*\(?\s*[""']([\w.\-]+)[""']"))
            result.Add(("Maven", "org.springframework.boot:spring-boot", m.Groups[1].Value));
        return result;
    }

    private static List<(string, string, string)> GradleVersionCatalog(string content)
    {
        var result = new List<(string, string, string)>();
        string Section(string name) => Regex.Match(content, $@"^\[{name}\]([\s\S]*?)(?=^\[|\z)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";

        var versions = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(Section("versions"), @"^\s*([\w.\-]+)\s*=\s*""([^""]+)""", RegexOptions.Multiline))
            versions[m.Groups[1].Value] = m.Groups[2].Value;

        foreach (var line in Section("libraries").Split('\n'))
        {
            string? ga = null, v = null;
            if (Regex.Match(line, @"=\s*""([\w.\-]+:[\w.\-]+):([\w.\-]+)""") is { Success: true } s) { ga = s.Groups[1].Value; v = s.Groups[2].Value; }
            else
            {
                if (Regex.Match(line, @"module\s*=\s*""([\w.\-]+:[\w.\-]+)""") is { Success: true } mod) ga = mod.Groups[1].Value;
                else if (Regex.Match(line, @"group\s*=\s*""([\w.\-]+)""") is { Success: true } gr && Regex.Match(line, @"name\s*=\s*""([\w.\-]+)""") is { Success: true } nm)
                    ga = $"{gr.Groups[1].Value}:{nm.Groups[1].Value}";
                if (Regex.Match(line, @"version\.ref\s*=\s*""([\w.\-]+)""") is { Success: true } vr) versions.TryGetValue(vr.Groups[1].Value, out v);
                else if (Regex.Match(line, @"version\s*=\s*""([\w.\-]+)""") is { Success: true } vv) v = vv.Groups[1].Value;
            }
            if (ga is not null && v is not null && ConcreteVersion.IsMatch(v)) result.Add(("Maven", ga, v));
        }
        return result;
    }
}
