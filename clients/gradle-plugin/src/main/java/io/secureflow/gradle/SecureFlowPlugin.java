package io.secureflow.gradle;

import org.gradle.api.Plugin;
import org.gradle.api.Project;
import org.gradle.api.provider.Provider;
import org.gradle.api.provider.ProviderFactory;

/**
 * Adds the {@code secureflow { }} extension and the {@code secureflow} task. The task is not wired into
 * {@code build} or {@code check}: it runs only when asked for ({@code gradle secureflow}), or when a
 * build opts in with {@code check.dependsOn tasks.secureflow}.
 */
public class SecureFlowPlugin implements Plugin<Project> {
    @Override
    public void apply(Project project) {
        SecureFlowExtension ext = project.getExtensions().create("secureflow", SecureFlowExtension.class);
        ext.getPath().convention(project.getRootProject().getLayout().getProjectDirectory());
        ProviderFactory providers = project.getProviders();

        project.getTasks().register("secureflow", SecureFlowScanTask.class, t -> {
            t.setGroup("verification");
            t.setDescription("Scans this project with SecureFlow and prints the report URL.");
            // -Psecureflow.<name> on the command line (or in gradle.properties) wins over the build script.
            t.getScanPath().set(ext.getPath());
            t.getServer().set(prop(providers, "server").orElse(ext.getServer()));
            t.getToken().set(prop(providers, "token").orElse(ext.getToken()));
            t.getTeamGroup().set(prop(providers, "group").orElse(ext.getGroup()));
            t.getProjectKey().set(prop(providers, "projectKey").orElse(ext.getProjectKey()));
            t.getProjectName().set(prop(providers, "name").orElse(ext.getName()));
            t.getFailOn().set(prop(providers, "failOn").orElse(ext.getFailOn()));
            t.getWaitForResult().set(prop(providers, "wait").map(Boolean::parseBoolean).orElse(ext.getWaitForResult()));
            t.getTimeoutSeconds().set(prop(providers, "timeoutSeconds").map(Integer::parseInt).orElse(ext.getTimeoutSeconds()));
            t.getOut().set(ext.getOut());
            t.getOpenReport().set(prop(providers, "open").map(Boolean::parseBoolean).orElse(ext.getOpen()));
            t.getSkip().set(prop(providers, "skip").map(Boolean::parseBoolean).orElse(ext.getSkip()));
            t.getCliPath().set(prop(providers, "cliPath").orElse(ext.getCliPath()));
            t.getCliVersion().set(prop(providers, "cliVersion").orElse(ext.getCliVersion()));
            t.getBinaryBaseUrl().set(prop(providers, "binaryBaseUrl").orElse(ext.getBinaryBaseUrl()));
            t.getExtraArgs().set(ext.getExtraArgs());
        });
    }

    private static Provider<String> prop(ProviderFactory providers, String name) {
        return providers.gradleProperty("secureflow." + name);
    }
}
