package io.secureflow.gradle;

import org.gradle.api.file.DirectoryProperty;
import org.gradle.api.file.RegularFileProperty;
import org.gradle.api.provider.ListProperty;
import org.gradle.api.provider.Property;

/**
 * {@code secureflow { ... }} block. Every setting is optional; unset ones fall back to {@code -Psecureflow.<name>},
 * then to the CLI's own chain (SECUREFLOW_* env vars, .secureflow.yml, ~/.secureflow/config.yml, defaults),
 * so build scripts can stay free of environment-specific values.
 */
public abstract class SecureFlowExtension {
    /** Directory to scan; defaults to the root project directory. */
    public abstract DirectoryProperty getPath();
    public abstract Property<String> getServer();
    public abstract Property<String> getToken();
    public abstract Property<String> getGroup();
    public abstract Property<String> getProjectKey();
    public abstract Property<String> getName();
    /** none | low | medium | high | critical */
    public abstract Property<String> getFailOn();
    public abstract Property<Boolean> getWaitForResult();
    public abstract Property<Integer> getTimeoutSeconds();
    public abstract RegularFileProperty getOut();
    public abstract Property<Boolean> getOpen();
    public abstract Property<Boolean> getSkip();
    public abstract Property<String> getCliPath();
    public abstract Property<String> getCliVersion();
    /** Folder (Artifactory URL or file:///) holding {@code <version>/secureflow-<rid>}. */
    public abstract Property<String> getBinaryBaseUrl();
    public abstract ListProperty<String> getExtraArgs();
}
