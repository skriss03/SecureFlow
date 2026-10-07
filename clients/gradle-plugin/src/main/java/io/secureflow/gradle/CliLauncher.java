package io.secureflow.gradle;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.net.URI;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.function.Consumer;

/**
 * Finds the native secureflow CLI and runs it. Resolution order, first hit wins:
 * <ol>
 *   <li>explicit path (plugin {@code cliPath} / {@code -Dsecureflow.cliPath})</li>
 *   <li>{@code SECUREFLOW_CLI} environment variable</li>
 *   <li>a cached download of the pinned version in {@code ~/.secureflow/cli/<version>/}</li>
 *   <li>a download from {@code <binaryBaseUrl>/<version>/secureflow-<rid>[.exe]}, where binaryBaseUrl comes from the
 *       plugin setting or {@code SECUREFLOW_CLI_BASE_URL} (an Artifactory folder, or file:///... for a local test)</li>
 *   <li>{@code secureflow} on the PATH, only when no base URL is configured</li>
 * </ol>
 * The pinned version wins over PATH so a stale global install can never shadow the CLI this plugin expects.
 * The scan logic itself lives only in the CLI, so every build tool behaves identically.
 * (The Maven plugin carries the same class; keep the two in step.)
 */
final class CliLauncher {
    private final Consumer<String> log;

    CliLauncher(Consumer<String> log) {
        this.log = log;
    }

    Path resolve(String cliPath, String version, String binaryBaseUrl) throws IOException {
        if (notBlank(cliPath)) return existing(Paths.get(cliPath), "cliPath");
        String env = System.getenv("SECUREFLOW_CLI");
        if (notBlank(env)) return existing(Paths.get(env), "SECUREFLOW_CLI");

        String fileName = "secureflow-" + rid() + (isWindows() ? ".exe" : "");
        Path cached = Paths.get(System.getProperty("user.home"), ".secureflow", "cli", version, fileName);
        if (Files.isRegularFile(cached)) return cached;

        String base = notBlank(binaryBaseUrl) ? binaryBaseUrl : System.getenv("SECUREFLOW_CLI_BASE_URL");
        if (!notBlank(base)) {
            String exe = isWindows() ? "secureflow.exe" : "secureflow";
            String pathVar = System.getenv("PATH");
            if (pathVar != null) {
                for (String dir : pathVar.split(File.pathSeparator)) {
                    if (dir.isEmpty()) continue;
                    Path candidate = Paths.get(dir, exe);
                    if (Files.isRegularFile(candidate)) {
                        log.accept("Using secureflow from PATH: " + candidate + " (set SECUREFLOW_CLI_BASE_URL to pin version " + version + ")");
                        return candidate;
                    }
                }
            }
            throw new IOException("secureflow CLI not found. Install it on PATH, set SECUREFLOW_CLI to its path, "
                    + "or set SECUREFLOW_CLI_BASE_URL (or the binaryBaseUrl setting) to the folder that holds "
                    + "<version>/" + fileName + " (e.g. your Artifactory generic repo).");
        }
        String url = base.replaceAll("/+$", "") + "/" + version + "/" + fileName;
        log.accept("Downloading secureflow CLI " + version + " from " + url);
        Files.createDirectories(cached.getParent());
        Path tmp = Files.createTempFile(cached.getParent(), fileName, ".part");
        try (InputStream in = URI.create(url).toURL().openStream()) {
            Files.copy(in, tmp, StandardCopyOption.REPLACE_EXISTING);
        }
        Files.move(tmp, cached, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
        if (!isWindows() && !cached.toFile().setExecutable(true)) log.accept("warning: could not mark " + cached + " executable");
        return cached;
    }

    /**
     * Runs the CLI and forwards each output line to {@code output}. Gradle executes tasks in a daemon, so
     * inheritIO() would print to the daemon's console instead of the user's terminal.
     */
    int run(Path cli, List<String> args, File workDir, Consumer<String> output) throws IOException, InterruptedException {
        List<String> cmd = new ArrayList<>();
        cmd.add(cli.toString());
        cmd.addAll(args);
        Process process = new ProcessBuilder(cmd).directory(workDir).redirectErrorStream(true).start();
        try (java.io.BufferedReader reader = new java.io.BufferedReader(
                new java.io.InputStreamReader(process.getInputStream(), java.nio.charset.StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) output.accept(line);
        }
        return process.waitFor();
    }

    /** Adds "--flag value" only when the value is set, so env vars and .secureflow.yml still apply otherwise. */
    static void addOpt(List<String> args, String flag, Object value) {
        if (value == null) return;
        String s = value.toString();
        if (s.isBlank()) return;
        args.add(flag);
        args.add(s);
    }

    static String rid() {
        String os = System.getProperty("os.name", "").toLowerCase(Locale.ROOT);
        String arch = System.getProperty("os.arch", "").toLowerCase(Locale.ROOT);
        String a = arch.contains("aarch64") || arch.contains("arm64") ? "arm64" : "x64";
        if (os.contains("win")) return "win-" + a;
        if (os.contains("mac") || os.contains("darwin")) return "osx-" + a;
        return "linux-" + a;
    }

    private static boolean isWindows() {
        return System.getProperty("os.name", "").toLowerCase(Locale.ROOT).contains("win");
    }

    private static boolean notBlank(String s) {
        return s != null && !s.isBlank();
    }

    private static Path existing(Path p, String source) throws IOException {
        if (!Files.isRegularFile(p)) throw new IOException(source + " points to " + p + ", which does not exist.");
        return p;
    }
}
