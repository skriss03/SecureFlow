package io.secureflow.maven;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Properties;

import org.apache.maven.plugin.AbstractMojo;
import org.apache.maven.plugin.MojoExecutionException;
import org.apache.maven.plugin.MojoFailureException;
import org.apache.maven.plugins.annotations.Mojo;
import org.apache.maven.plugins.annotations.Parameter;

/**
 * {@code mvn secureflow:scan} — scans the project with SecureFlow and prints the report URL.
 *
 * <p>Every parameter is optional. When one is not set, the CLI falls back to SECUREFLOW_* environment
 * variables, then .secureflow.yml in the repo, then ~/.secureflow/config.yml, then its defaults, so a
 * team can keep the POM free of environment-specific values (server URL, token).
 *
 * <p>Not bound to any phase by default: a normal build is never slowed down. Bind it in a profile to
 * opt in, e.g. {@code mvn verify -Psecureflow}.
 */
@Mojo(name = "scan", aggregator = true, requiresProject = true, threadSafe = true)
public class ScanMojo extends AbstractMojo {

    /** Directory to scan. Defaults to the directory Maven was run from (the reactor root). */
    @Parameter(property = "secureflow.path", defaultValue = "${session.executionRootDirectory}")
    private File path;

    /** SecureFlow server base URL, e.g. http://localhost:5180 or https://secureflow.apps.example.com */
    @Parameter(property = "secureflow.server")
    private String server;

    /** API token. Prefer SECUREFLOW_TOKEN or ~/.m2/settings.xml properties over putting it in the POM. */
    @Parameter(property = "secureflow.token")
    private String token;

    @Parameter(property = "secureflow.group")
    private String group;

    @Parameter(property = "secureflow.projectKey")
    private String projectKey;

    @Parameter(property = "secureflow.name")
    private String name;

    /** none | low | medium | high | critical. The build fails if an open finding is at or above it. */
    @Parameter(property = "secureflow.failOn")
    private String failOn;

    @Parameter(property = "secureflow.wait")
    private Boolean waitForResult;

    @Parameter(property = "secureflow.timeoutSeconds")
    private Integer timeoutSeconds;

    /** Save the Markdown report to this file as well. */
    @Parameter(property = "secureflow.out")
    private File out;

    @Parameter(property = "secureflow.open")
    private Boolean open;

    @Parameter(property = "secureflow.skip", defaultValue = "false")
    private boolean skip;

    /** Explicit path to the secureflow executable. */
    @Parameter(property = "secureflow.cliPath")
    private String cliPath;

    /** CLI version to download when it is not on the PATH. Defaults to this plugin's version. */
    @Parameter(property = "secureflow.cliVersion")
    private String cliVersion;

    /** Folder (Artifactory URL or file:///) holding {@code <version>/secureflow-<rid>}. Falls back to SECUREFLOW_CLI_BASE_URL. */
    @Parameter(property = "secureflow.binaryBaseUrl")
    private String binaryBaseUrl;

    /** Any further CLI arguments, passed through verbatim. */
    @Parameter
    private List<String> extraArgs;

    @Override
    public void execute() throws MojoExecutionException, MojoFailureException {
        if (skip) {
            getLog().info("SecureFlow scan skipped (secureflow.skip=true).");
            return;
        }
        List<String> args = new ArrayList<>();
        args.add("scan");
        CliLauncher.addOpt(args, "--path", path.getAbsolutePath());
        CliLauncher.addOpt(args, "--server", server);
        CliLauncher.addOpt(args, "--token", token);
        CliLauncher.addOpt(args, "--group", group);
        CliLauncher.addOpt(args, "--project-key", projectKey);
        CliLauncher.addOpt(args, "--name", name);
        CliLauncher.addOpt(args, "--fail-on", failOn);
        CliLauncher.addOpt(args, "--wait", waitForResult);
        CliLauncher.addOpt(args, "--timeout-seconds", timeoutSeconds);
        CliLauncher.addOpt(args, "--out", out == null ? null : out.getAbsolutePath());
        CliLauncher.addOpt(args, "--open", open);
        if (extraArgs != null) args.addAll(extraArgs);

        CliLauncher launcher = new CliLauncher(msg -> getLog().info(msg));
        int exit;
        try {
            String version = cliVersion != null && !cliVersion.isBlank() ? cliVersion : pluginVersion();
            Path cli = launcher.resolve(cliPath, version, binaryBaseUrl);
            getLog().debug("Using " + cli);
            exit = launcher.run(cli, args, path);
        } catch (IOException e) {
            throw new MojoExecutionException("SecureFlow scan could not run: " + e.getMessage(), e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new MojoExecutionException("SecureFlow scan interrupted", e);
        }

        if (exit == 2) throw new MojoFailureException("SecureFlow gate failed: open findings at or above '" + failOn + "'. See the report URL above.");
        if (exit != 0) throw new MojoExecutionException("SecureFlow scan failed (exit " + exit + "). See the output above.");
    }

    private static String pluginVersion() throws IOException {
        try (InputStream in = ScanMojo.class.getResourceAsStream("secureflow-client.properties")) {
            Properties p = new Properties();
            if (in != null) p.load(in);
            return p.getProperty("version", "0.1.0");
        }
    }
}
