package io.secureflow.gradle;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Properties;

import org.gradle.api.DefaultTask;
import org.gradle.api.GradleException;
import org.gradle.api.file.DirectoryProperty;
import org.gradle.api.file.RegularFileProperty;
import org.gradle.api.provider.ListProperty;
import org.gradle.api.provider.Property;
import org.gradle.api.tasks.Input;
import org.gradle.api.tasks.Internal;
import org.gradle.api.tasks.Optional;
import org.gradle.api.tasks.TaskAction;
import org.gradle.work.DisableCachingByDefault;

/** {@code gradle secureflow}: runs `secureflow scan` and fails the build on exit code 2 (the --fail-on gate). */
@DisableCachingByDefault(because = "A scan reports on the current state of a remote server; it is never up to date.")
public abstract class SecureFlowScanTask extends DefaultTask {
    @Internal public abstract DirectoryProperty getScanPath();
    @Input @Optional public abstract Property<String> getServer();
    @Internal public abstract Property<String> getToken(); // never an @Input: keep secrets out of build caches and scans
    @Input @Optional public abstract Property<String> getTeamGroup();
    @Input @Optional public abstract Property<String> getProjectKey();
    @Input @Optional public abstract Property<String> getProjectName();
    @Input @Optional public abstract Property<String> getFailOn();
    @Input @Optional public abstract Property<Boolean> getWaitForResult();
    @Input @Optional public abstract Property<Integer> getTimeoutSeconds();
    @Internal public abstract RegularFileProperty getOut();
    @Input @Optional public abstract Property<Boolean> getOpenReport();
    @Input @Optional public abstract Property<Boolean> getSkip();
    @Input @Optional public abstract Property<String> getCliPath();
    @Input @Optional public abstract Property<String> getCliVersion();
    @Input @Optional public abstract Property<String> getBinaryBaseUrl();
    @Input @Optional public abstract ListProperty<String> getExtraArgs();

    public SecureFlowScanTask() {
        getOutputs().upToDateWhen(t -> false);
    }

    @TaskAction
    public void scan() {
        if (getSkip().getOrElse(false)) {
            getLogger().lifecycle("SecureFlow scan skipped (secureflow.skip=true).");
            return;
        }
        File dir = getScanPath().get().getAsFile();
        List<String> args = new ArrayList<>();
        args.add("scan");
        CliLauncher.addOpt(args, "--path", dir.getAbsolutePath());
        CliLauncher.addOpt(args, "--server", getServer().getOrNull());
        CliLauncher.addOpt(args, "--token", getToken().getOrNull());
        CliLauncher.addOpt(args, "--group", getTeamGroup().getOrNull());
        CliLauncher.addOpt(args, "--project-key", getProjectKey().getOrNull());
        CliLauncher.addOpt(args, "--name", getProjectName().getOrNull());
        CliLauncher.addOpt(args, "--fail-on", getFailOn().getOrNull());
        CliLauncher.addOpt(args, "--wait", getWaitForResult().getOrNull());
        CliLauncher.addOpt(args, "--timeout-seconds", getTimeoutSeconds().getOrNull());
        CliLauncher.addOpt(args, "--out", getOut().isPresent() ? getOut().get().getAsFile().getAbsolutePath() : null);
        CliLauncher.addOpt(args, "--open", getOpenReport().getOrNull());
        args.addAll(getExtraArgs().getOrElse(List.of()));

        CliLauncher launcher = new CliLauncher(msg -> getLogger().lifecycle(msg));
        int exit;
        try {
            String version = getCliVersion().isPresent() ? getCliVersion().get() : pluginVersion();
            Path cli = launcher.resolve(getCliPath().getOrNull(), version, getBinaryBaseUrl().getOrNull());
            getLogger().info("Using {}", cli);
            // quiet() so the report URL shows even with -q; it is the one thing the user ran this for.
            exit = launcher.run(cli, args, dir, line -> getLogger().quiet(line));
        } catch (IOException e) {
            throw new GradleException("SecureFlow scan could not run: " + e.getMessage(), e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new GradleException("SecureFlow scan interrupted", e);
        }
        if (exit == 2) throw new GradleException("SecureFlow gate failed: open findings at or above '" + getFailOn().getOrNull() + "'. See the report URL above.");
        if (exit != 0) throw new GradleException("SecureFlow scan failed (exit " + exit + "). See the output above.");
    }

    private static String pluginVersion() throws IOException {
        try (InputStream in = SecureFlowScanTask.class.getResourceAsStream("secureflow-client.properties")) {
            Properties p = new Properties();
            if (in != null) p.load(in);
            return p.getProperty("version", "0.1.0");
        }
    }
}
