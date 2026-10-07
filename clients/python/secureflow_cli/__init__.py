"""Thin launcher for the native secureflow CLI, so Python / Django / FastAPI projects can run

    pip install secureflow-cli      (from Artifactory's PyPI remote, or a local wheel)
    secureflow scan

All scan logic lives in the CLI; this only finds (or downloads) it and passes the arguments through.

Resolution order, first hit wins:
  1. SECUREFLOW_CLI                  explicit path to the executable
  2. ~/.secureflow/cli/<version>/    cached download of the pinned version
  3. download from SECUREFLOW_CLI_BASE_URL/<version>/secureflow-<rid>[.exe]
     (an Artifactory generic repo, or file:///... for a local test)
  4. secureflow on the PATH, only when no base URL is configured (never this console script itself)
The pinned version wins over PATH so a stale global install can never shadow the expected CLI.
"""
import os
import platform
import shutil
import stat
import subprocess
import sys
import urllib.request
from pathlib import Path

__version__ = "0.1.0"


def _rid() -> str:
    machine = platform.machine().lower()
    arch = "arm64" if machine in ("arm64", "aarch64") else "x64"
    if sys.platform.startswith("win"):
        return f"win-{arch}"
    if sys.platform == "darwin":
        return f"osx-{arch}"
    return f"linux-{arch}"


def _exe() -> str:
    return ".exe" if sys.platform.startswith("win") else ""


def _on_path():
    found = shutil.which("secureflow")
    if not found:
        return None
    found_path = Path(found).resolve()
    # pip installs a console-script launcher named "secureflow" into the same Scripts/bin folder as the
    # interpreter; that launcher is this package, not the CLI.
    if found_path.parent == Path(sys.executable).resolve().parent:
        return None
    return str(found_path)


def _resolve_cli() -> str:
    explicit = os.environ.get("SECUREFLOW_CLI")
    if explicit:
        if not Path(explicit).is_file():
            raise RuntimeError(f"SECUREFLOW_CLI points to {explicit}, which does not exist.")
        return explicit

    version = os.environ.get("SECUREFLOW_CLI_VERSION", __version__)
    file_name = f"secureflow-{_rid()}{_exe()}"
    cached = Path.home() / ".secureflow" / "cli" / version / file_name
    if cached.is_file():
        return str(cached)

    base = os.environ.get("SECUREFLOW_CLI_BASE_URL")
    if not base:
        found = _on_path()
        if found:
            print(f"Using secureflow from PATH: {found} (set SECUREFLOW_CLI_BASE_URL to pin version {version})", file=sys.stderr)
            return found
        raise RuntimeError(
            "secureflow CLI not found. Set SECUREFLOW_CLI_BASE_URL to the folder holding "
            f"<version>/{file_name} (e.g. your Artifactory generic repo), set SECUREFLOW_CLI to the "
            "executable, or put secureflow on PATH."
        )
    url = f"{base.rstrip('/')}/{version}/{file_name}"
    print(f"Downloading secureflow CLI {version} from {url}", file=sys.stderr)
    cached.parent.mkdir(parents=True, exist_ok=True)
    tmp = cached.with_suffix(cached.suffix + f".{os.getpid()}.part")
    with urllib.request.urlopen(url) as response, open(tmp, "wb") as out:  # honours HTTPS_PROXY
        shutil.copyfileobj(response, out)
    os.replace(tmp, cached)
    if not sys.platform.startswith("win"):
        cached.chmod(cached.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return str(cached)


def main() -> int:
    try:
        cli = _resolve_cli()
    except Exception as e:  # noqa: BLE001 - surface any resolution problem as one readable line
        print(f"secureflow: {e}", file=sys.stderr)
        return 1
    return subprocess.call([cli, *sys.argv[1:]])


if __name__ == "__main__":
    sys.exit(main())
