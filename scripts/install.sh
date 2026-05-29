#!/bin/sh
# Hina one-liner installer for Linux and macOS.
#
#   curl -fsSL https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.sh | bash
#
# Environment overrides:
#   HINA_VERSION         pin a release tag (e.g. v1.2.3). default: latest
#   HINA_INSTALL_DIR     destination directory.            default: $HOME/.local/bin
#   HINA_NO_MODIFY_PATH  set to 1 to skip editing shell rc files
#   HINA_ACTION          bypass interactive menu when an existing install is
#                        detected. one of:
#                          reinstall | purge | verify | exit
#                          uninstall-full | uninstall-configs | uninstall-binary
#   HINA_PURGE_YES       set to 1 to confirm clean reinstall non-interactively
#   HINA_UNINSTALL_YES   set to 1 to confirm uninstall-full / uninstall-configs
#                        non-interactively (mode 'uninstall-binary' is unaffected)
#   HINA_NO_CHECKSUM     set to 1 to skip SHA-256 verification (debug only)

set -eu

REPO="Arutosio/Hina"
DEST="${HINA_INSTALL_DIR:-${HOME}/.local/bin}"
TAG="${HINA_VERSION:-}"
NO_MODIFY_PATH="${HINA_NO_MODIFY_PATH:-0}"
ACTION_OVERRIDE="${HINA_ACTION:-}"
PURGE_YES="${HINA_PURGE_YES:-0}"
UNINSTALL_YES="${HINA_UNINSTALL_YES:-0}"
NO_CHECKSUM="${HINA_NO_CHECKSUM:-0}"

# Auto-prefix HINA_VERSION with 'v' if user passed a bare version like 1.2.3
# (GitHub release tags use the v-prefixed form).
case "$TAG" in
    ""|v*) ;;
    [0-9]*) TAG="v$TAG" ;;
esac

# Tighten umask so the temp dir, lock pid file, and staged binary aren't
# world-readable/writable if the caller has a permissive default.
umask 077

main() {
    err()  { printf 'hina-install: %s\n' "$*" >&2; exit 1; }
    info() { printf 'hina-install: %s\n' "$*"; }

    need() {
        command -v "$1" >/dev/null 2>&1 || err "missing required tool: $1"
    }

    need curl
    need tar
    need uname
    need mktemp

    detect_os() {
        case "$(uname -s)" in
            Linux)  echo linux ;;
            Darwin) echo macos ;;
            *)      err "unsupported OS: $(uname -s). Download a release manually from https://github.com/${REPO}/releases" ;;
        esac
    }

    detect_arch() {
        case "$(uname -m)" in
            x86_64|amd64)   echo x64 ;;
            aarch64|arm64)  echo arm64 ;;
            *)              err "unsupported architecture: $(uname -m). Download a release manually from https://github.com/${REPO}/releases" ;;
        esac
    }

    registry_dir() {
        case "$OS" in
            linux) echo "${XDG_DATA_HOME:-${HOME}/.local/share}/hina" ;;
            macos) echo "${HOME}/Library/Application Support/Hina" ;;
        esac
    }

    sha256_of() {
        if command -v sha256sum >/dev/null 2>&1; then
            sha256sum "$1" | awk '{print $1}'
        else
            shasum -a 256 "$1" | awk '{print $1}'
        fi
    }

    # Run a binary with a short timeout if `timeout` is available, otherwise
    # invoke it raw. Keeps a stuck/corrupt installed binary from blocking the
    # installer forever during version probes and smoke tests.
    bounded_run() {
        if command -v timeout >/dev/null 2>&1; then
            timeout 10 "$@"
        elif command -v gtimeout >/dev/null 2>&1; then
            gtimeout 10 "$@"
        else
            "$@"
        fi
    }

    # Extract a bare X.Y.Z from arbitrary text (e.g. "hina 1.0.0" or "v1.0.0").
    ver_num() {
        printf '%s' "$1" | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -n1
    }

    # Compare two X.Y.Z versions. Prints -1 if $1<$2, 0 if equal, 1 if $1>$2.
    # POSIX-only (no sort -V, which macOS lacks): split on '.' and compare numerically.
    semver_cmp() {
        a_major=${1%%.*}; a_rest=${1#*.}; a_minor=${a_rest%%.*}; a_patch=${a_rest#*.}
        b_major=${2%%.*}; b_rest=${2#*.}; b_minor=${b_rest%%.*}; b_patch=${b_rest#*.}
        for pair in "$a_major $b_major" "$a_minor $b_minor" "$a_patch $b_patch"; do
            x=${pair% *}; y=${pair#* }
            x=${x:-0}; y=${y:-0}
            if [ "$x" -lt "$y" ]; then echo -1; return; fi
            if [ "$x" -gt "$y" ]; then echo 1; return; fi
        done
        echo 0
    }

    # macOS-specific: the NativeAOT binary dynamically links against
    # Homebrew openssl@3 + brotli (release.yml builds with them). Catch the
    # missing-deps case up front with an actionable message rather than
    # letting the smoke test fail later with a generic dyld error.
    check_macos_runtime_deps() {
        [ "$OS" = "macos" ] || return 0
        if [ "$ARCH" = "arm64" ]; then
            brew_prefix=/opt/homebrew
        else
            brew_prefix=/usr/local
        fi
        missing=""
        # libssl.3.dylib lives under openssl@3
        if ! ls "${brew_prefix}/opt/openssl@3/lib/libssl.3.dylib" >/dev/null 2>&1; then
            missing="${missing} openssl@3"
        fi
        # brotli ships libbrotlicommon.1.dylib + libbrotlidec.1.dylib
        if ! ls "${brew_prefix}/opt/brotli/lib/libbrotlicommon"*.dylib >/dev/null 2>&1; then
            missing="${missing} brotli"
        fi
        if [ -n "$missing" ]; then
            if ! command -v brew >/dev/null 2>&1; then
                cat >&2 <<EOF
hina-install: Homebrew is required on macOS to provide runtime libraries
(openssl@3 + brotli) that the hina binary links against.

  Install Homebrew first: https://brew.sh
  Then run: brew install${missing}
  Then re-run this installer.
EOF
                exit 1
            fi
            cat >&2 <<EOF
hina-install: missing macOS runtime libraries:${missing}

The hina binary dynamically links against these Homebrew packages.
Install them with:

  brew install${missing}

Then re-run this installer.
EOF
            exit 1
        fi
    }

    have_tty() {
        # Real open-test: -r/-w metadata can lie when the process has no
        # controlling terminal (ENXIO on open even though stat() reports rw).
        (: </dev/tty >/dev/tty) 2>/dev/null
    }

    read_tty() {
        if have_tty; then
            IFS= read -r reply < /dev/tty || reply=""
            printf '%s' "$reply"
        else
            printf ''
        fi
    }

    resolve_latest_tag() {
        api_url="https://api.github.com/repos/${REPO}/releases/latest"
        tag="$(curl -fsSL "$api_url" 2>/dev/null | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')"
        if [ -z "$tag" ]; then
            location="$(curl -fsSI "https://github.com/${REPO}/releases/latest" 2>/dev/null | tr -d '\r' | awk -F'/' 'tolower($1) ~ /^location:/ { print $NF }')"
            tag="$location"
        fi
        [ -n "$tag" ] || err "could not resolve latest release tag. Set HINA_VERSION=v... manually."
        echo "$tag"
    }

    download_with_retry() {
        url="$1"
        out_partial="${2}.partial"
        out_final="$2"

        attempt=1
        while [ "$attempt" -le 5 ]; do
            if curl --fail --location --proto '=https' --tlsv1.2 \
                    --connect-timeout 15 --max-time 600 \
                    -C - -o "$out_partial" "$url"; then
                mv "$out_partial" "$out_final"
                return 0
            fi
            info "download attempt $attempt failed, retrying in ${attempt}s..."
            sleep "$attempt"
            attempt=$((attempt + 1))
        done
        err "download failed after 5 attempts: $url"
    }

    fetch_optional() {
        # 0 = fetched, 1 = 404 / network fail (caller decides)
        url="$1"
        out="$2"
        curl --fail --location --proto '=https' --tlsv1.2 \
             --connect-timeout 15 --max-time 60 \
             -o "$out" "$url" 2>/dev/null
    }

    verify_archive_checksum() {
        archive="$1"
        sha_url="$2"

        if [ "$NO_CHECKSUM" = "1" ]; then
            info "HINA_NO_CHECKSUM=1, skipping SHA-256 verify"
            return 0
        fi

        sha_file="${archive}.sha256"
        if fetch_optional "$sha_url" "$sha_file"; then
            expected="$(awk '{print $1}' "$sha_file" | head -n1)"
            [ -n "$expected" ] || err "downloaded .sha256 is empty or malformed"
            actual="$(sha256_of "$archive")"
            if [ "$expected" != "$actual" ]; then
                err "SHA-256 mismatch for $(basename "$archive") — expected $expected, got $actual. Archive may be corrupt or tampered."
            fi
            info "checksum verified (SHA-256)"
        else
            info "no SHA-256 published for ${TAG} (older release?). Falling back to structural tarball check only."
        fi
    }

    acquire_lock() {
        lock_dir="$1"
        mkdir -p "$(dirname "$lock_dir")"
        if mkdir "$lock_dir" 2>/dev/null; then
            echo "$$" > "$lock_dir/pid"
            return 0
        fi
        old_pid="$(cat "$lock_dir/pid" 2>/dev/null || true)"
        if [ -n "$old_pid" ] && kill -0 "$old_pid" 2>/dev/null; then
            err "another install is in progress (pid=$old_pid). If this is wrong: rm -rf $lock_dir"
        fi
        info "stale lock from pid=$old_pid found, overriding"
        rm -rf "$lock_dir"
        mkdir "$lock_dir" || err "could not acquire lock at $lock_dir"
        echo "$$" > "$lock_dir/pid"
    }

    release_lock() {
        [ -n "${LOCK_DIR:-}" ] && rm -rf "$LOCK_DIR"
    }

    cleanup() {
        release_lock
        [ -n "${TMPDIR_INSTALL:-}" ] && rm -rf "$TMPDIR_INSTALL"
    }

    # --- uninstall helpers ---------------------------------------------------

    remove_binary() {
        if [ -e "$existing_bin" ] || [ -L "$existing_bin" ]; then
            ts="$(date +%s)"
            mv "$existing_bin" "${existing_bin}.uninst-${ts}"
            rm -f "${existing_bin}.uninst-${ts}"
            info "removed ${existing_bin}"
        else
            info "binary already absent at ${existing_bin}"
        fi
    }

    # Strip the marker-delimited PATH block this installer added to shell rc
    # files. Idempotent: skips files without the marker. Atomic per-file via
    # tmp + rename.
    remove_path_stanza() {
        [ "$NO_MODIFY_PATH" = "1" ] && return 0
        marker_begin='# >>> hina installer >>>'
        marker_end='# <<< hina installer <<<'
        for rc_file in \
            "${ZDOTDIR:-$HOME}/.zshrc" \
            "${HOME}/.bash_profile" \
            "${HOME}/.bashrc" \
            "${HOME}/.profile" \
            "${HOME}/.config/fish/config.fish"
        do
            [ -f "$rc_file" ] || continue
            grep -qF "$marker_begin" "$rc_file" || continue
            tmp_rc="${rc_file}.hina-tmp.$$"
            awk -v b="$marker_begin" -v e="$marker_end" '
                $0 == b { skip = 1; next }
                $0 == e { skip = 0; next }
                !skip
            ' "$rc_file" > "$tmp_rc"
            mv -f "$tmp_rc" "$rc_file"
            info "removed PATH entry from ${rc_file}"
        done
    }

    do_uninstall() {
        mode="$1"  # uninstall-full | uninstall-configs | uninstall-binary
        reg_dir="$(registry_dir)"

        case "$mode" in
            uninstall-full)
                cat <<WARN

WARNING - full uninstall will permanently delete:
  - ${existing_bin}
  - All hina-managed apps (via 'hina uninstall <name>' per app)
  - ${reg_dir}
      (registry.json, Apps/, descriptors/, pinned publisher keys)
  - PATH entry for ${DEST} in your shell rc files
WARN
                ;;
            uninstall-configs)
                cat <<WARN

WARNING - this will permanently delete:
  - ${existing_bin}
  - Configs at ${reg_dir}
      (registry.json, descriptors/, pinned publisher keys)
  - PATH entry for ${DEST} in your shell rc files

KEPT (left as orphan files on disk; hina will no longer manage them):
  - ${reg_dir}/Apps/
WARN
                ;;
            uninstall-binary)
                cat <<INFO

This will remove:
  - ${existing_bin}

KEPT untouched:
  - ${reg_dir} (full registry + apps + keys preserved)
  - PATH entry in your shell rc files

Reinstall hina later to resume the previous state.
INFO
                ;;
        esac

        # --- confirmation gate ---
        case "$mode" in
            uninstall-full|uninstall-configs)
                if have_tty && [ "$UNINSTALL_YES" != "1" ]; then
                    printf 'Type "yes" to confirm: ' > /dev/tty
                    confirm="$(read_tty)"
                    [ "$confirm" = "yes" ] || err "aborted by user"
                elif [ "$UNINSTALL_YES" != "1" ]; then
                    err "non-interactive '${mode}' requires HINA_UNINSTALL_YES=1 as a second gate"
                fi
                ;;
            uninstall-binary)
                if have_tty; then
                    printf 'Proceed? [y/N]: ' > /dev/tty
                    confirm="$(read_tty)"
                    case "$confirm" in
                        y|Y|yes|YES) ;;
                        *) err "aborted by user" ;;
                    esac
                fi
                ;;
        esac

        # --- per-mode actions ---
        case "$mode" in
            uninstall-full)
                # Enumerate apps and let `hina uninstall` handle each app's
                # side-effects (desktop entries, shims, fonts, HKCU MIME).
                # Fail-soft + idempotent — never abort the script on a
                # single-app failure.
                if [ -x "$existing_bin" ]; then
                    app_list="$(bounded_run "$existing_bin" list 2>/dev/null \
                                | awk 'NR>1 && $1 != "" {print $1}' || true)"
                    if [ -n "$app_list" ]; then
                        printf '%s\n' "$app_list" | while IFS= read -r app; do
                            [ -z "$app" ] && continue
                            info "uninstalling app: $app"
                            bounded_run "$existing_bin" uninstall "$app" >/dev/null 2>&1 \
                                || info "  warning: 'hina uninstall $app' returned non-zero (continuing)"
                        done
                    fi
                else
                    info "binary missing or not executable; skipping per-app teardown"
                fi
                if [ -d "$reg_dir" ]; then
                    ts="$(date +%s)"
                    mv "$reg_dir" "${reg_dir}.uninst-${ts}"
                    rm -rf "${reg_dir}.uninst-${ts}"
                    info "removed ${reg_dir}"
                fi
                remove_binary
                remove_path_stanza
                info "uninstall (full) complete"
                ;;
            uninstall-configs)
                if [ -f "${reg_dir}/registry.json" ]; then
                    rm -f "${reg_dir}/registry.json"
                    info "removed ${reg_dir}/registry.json"
                fi
                if [ -d "${reg_dir}/descriptors" ]; then
                    ts="$(date +%s)"
                    mv "${reg_dir}/descriptors" "${reg_dir}/descriptors.uninst-${ts}"
                    rm -rf "${reg_dir}/descriptors.uninst-${ts}"
                    info "removed ${reg_dir}/descriptors"
                fi
                remove_binary
                remove_path_stanza
                if [ -d "${reg_dir}/Apps" ]; then
                    info "Apps/ at ${reg_dir}/Apps preserved as orphans (hina no longer tracks them)"
                fi
                info "uninstall (configs) complete"
                ;;
            uninstall-binary)
                remove_binary
                info "uninstall (binary only) complete; ${reg_dir} preserved"
                ;;
        esac
    }

    # --- platform + version ---------------------------------------------------
    OS="$(detect_os)"
    ARCH="$(detect_arch)"
    check_macos_runtime_deps

    if [ -z "$TAG" ]; then
        info "resolving latest release..."
        TAG="$(resolve_latest_tag)"
    fi
    info "target: hina ${TAG} (${OS}-${ARCH})"

    ARCHIVE_NAME="Hina-${OS}-${ARCH}.tar.gz"
    URL="https://github.com/${REPO}/releases/download/${TAG}/${ARCHIVE_NAME}"
    SHA_URL="${URL}.sha256"

    # --- existing install detection + menu -----------------------------------
    existing_bin="${DEST}/hina"
    if [ -d "$existing_bin" ] && [ ! -L "$existing_bin" ]; then
        err "${existing_bin} exists but is a directory, not a file. Remove or rename it before installing."
    fi
    existing_ver=""
    existing_num=""
    target_num="$(ver_num "$TAG")"
    if [ -x "$existing_bin" ] && [ -f "$existing_bin" -o -L "$existing_bin" ]; then
        # `hina version` prints "hina X.Y.Z"; fall back to --version for older binaries.
        existing_ver="$(bounded_run "$existing_bin" version 2>/dev/null | head -n1)"
        [ -z "$existing_ver" ] && existing_ver="$(bounded_run "$existing_bin" --version 2>/dev/null | head -n1)"
        [ -z "$existing_ver" ] && existing_ver="unknown"
        existing_num="$(ver_num "$existing_ver")"
    fi

    # Relationship of the installed build to the release we're targeting.
    # one of: behind | same | ahead | unknown
    ver_rel="unknown"
    if [ -n "$existing_num" ] && [ -n "$target_num" ]; then
        case "$(semver_cmp "$existing_num" "$target_num")" in
            -1) ver_rel="behind" ;;
             0) ver_rel="same" ;;
             1) ver_rel="ahead" ;;
        esac
    fi

    if [ -n "$ACTION_OVERRIDE" ]; then
        action="$ACTION_OVERRIDE"
        case "$action" in
            reinstall|purge|verify|exit|uninstall-full|uninstall-configs|uninstall-binary) ;;
            *) err "invalid HINA_ACTION=$action (use: reinstall|purge|verify|exit|uninstall-full|uninstall-configs|uninstall-binary)" ;;
        esac
        [ -n "$existing_ver" ] && info "existing install detected (${existing_ver}); HINA_ACTION=${action}"
    elif [ -n "$existing_ver" ]; then
        if have_tty; then
            case "$ver_rel" in
                behind) status_line="A newer release is available (${existing_num} -> ${target_num}). Choose [1] to update." ;;
                same)   status_line="You already have the target version (${target_num}). Up to date." ;;
                ahead)  status_line="Installed (${existing_num}) is newer than the target (${target_num})." ;;
                *)      status_line="Could not determine the installed version." ;;
            esac
            opt1_label="Reinstall"
            [ "$ver_rel" = "behind" ] && opt1_label="Update      "
            cat <<MENU

Hina is already installed.
  Installed: ${existing_ver}
  Location:  ${existing_bin}
  Target:    ${TAG}
  Status:    ${status_line}

What would you like to do?
  [1] ${opt1_label}    - replace binary; keep installed apps and registry
  [2] Clean reinstall - WIPES registry, apps, and pinned publisher keys
  [3] Integrity check - verify installed binary against release SHA-256
  [4] Exit            - leave installation as-is
  [5] Uninstall       - remove hina (with options)

MENU
            printf 'Choice [1-5] (default 4): ' > /dev/tty
            choice="$(read_tty)"
            case "$choice" in
                1|reinstall) action=reinstall ;;
                2|clean|purge) action=purge ;;
                3|verify|check) action=verify ;;
                4|exit|""|q|quit) action=exit ;;
                5|uninstall|remove)
                    cat <<SUBMENU

Uninstall options:
  [a] Full     - remove hina + all installed apps + configs.
                 Per-app teardown via 'hina uninstall <name>' first so
                 side-effects (desktop entries, shortcuts, fonts) are
                 cleaned properly.
  [b] Configs  - remove hina + configs (registry, keys, descriptors).
                 KEEPS app binaries on disk as orphan files. hina will
                 no longer track them.
  [c] Binary   - remove only the hina binary. KEEPS apps + configs
                 intact. Reinstalling hina resumes the previous state.

SUBMENU
                    printf 'Choice [a/b/c]: ' > /dev/tty
                    submode="$(read_tty)"
                    case "$submode" in
                        a|full|A) action=uninstall-full ;;
                        b|configs|B) action=uninstall-configs ;;
                        c|binary|bin|C) action=uninstall-binary ;;
                        *) err "invalid uninstall option: $submode" ;;
                    esac
                    ;;
                *) err "invalid choice: $choice" ;;
            esac
        else
            # non-interactive default: upgrade only when the installed build is strictly
            # behind the target. Same or ahead = no-op (idempotent); unknown = upgrade to
            # be safe. Override with HINA_ACTION.
            case "$ver_rel" in
                behind)
                    info "non-interactive: ${existing_num} is behind ${target_num}, upgrading (set HINA_ACTION to override)"
                    action=reinstall
                    ;;
                same)
                    info "non-interactive: already at ${target_num}, nothing to do (set HINA_ACTION to override)"
                    action=exit
                    ;;
                ahead)
                    info "non-interactive: installed ${existing_num} is newer than target ${target_num}, leaving as-is (set HINA_ACTION=reinstall to force)"
                    action=exit
                    ;;
                *)
                    info "non-interactive: could not determine installed version, upgrading (set HINA_ACTION to override)"
                    action=reinstall
                    ;;
            esac
        fi
    else
        action=install
    fi

    case "$action" in
        exit)
            info "no changes made"
            exit 0
            ;;
        verify)
            do_verify
            exit 0
            ;;
        install|reinstall|purge|uninstall-full|uninstall-configs|uninstall-binary) ;;
        *) err "internal: unknown action $action" ;;
    esac

    # --- acquire lock for filesystem-modifying actions -----------------------
    mkdir -p "$DEST"
    LOCK_DIR="${DEST}/.hina-install.lock"
    acquire_lock "$LOCK_DIR"
    trap cleanup EXIT INT TERM

    # --- uninstall short-circuits (no download, no tmpdir needed) -----------
    case "$action" in
        uninstall-full|uninstall-configs|uninstall-binary)
            do_uninstall "$action"
            exit 0
            ;;
    esac

    # --- tmpdir for install/reinstall/purge ----------------------------------
    TMPDIR_INSTALL="$(mktemp -d 2>/dev/null || mktemp -d -t hina-install)"
    mkdir -p "$TMPDIR_INSTALL/staging"

    # --- purge (if requested) -------------------------------------------------
    if [ "$action" = "purge" ]; then
        reg_dir="$(registry_dir)"
        cat <<WARN

WARNING - clean reinstall will permanently delete:
  - ${reg_dir}
      (registry.json, Apps/, descriptors/, pinned publisher keys)
  - ${existing_bin}

All previously installed Hina apps will become orphaned files
(desktop entries, shell shortcuts, fonts) that you'll need to clean
up manually. To preserve apps, abort now and back up ${reg_dir}.

WARN
        if have_tty && [ "$PURGE_YES" != "1" ]; then
            printf 'Type "yes" to confirm clean reinstall: ' > /dev/tty
            confirm="$(read_tty)"
            [ "$confirm" = "yes" ] || err "aborted by user"
        elif [ "$PURGE_YES" != "1" ]; then
            err "non-interactive purge requires HINA_PURGE_YES=1 as an explicit second gate"
        fi

        ts="$(date +%s)"
        if [ -d "$reg_dir" ]; then
            mv "$reg_dir" "${reg_dir}.purged-${ts}"
            rm -rf "${reg_dir}.purged-${ts}"
            info "removed ${reg_dir}"
        fi
        if [ -e "$existing_bin" ]; then
            mv "$existing_bin" "${existing_bin}.purged-${ts}"
            rm -f "${existing_bin}.purged-${ts}"
            info "removed ${existing_bin}"
        fi
        action=install
    fi

    # --- download + verify ----------------------------------------------------
    archive_path="${TMPDIR_INSTALL}/${ARCHIVE_NAME}"
    info "downloading ${URL}"
    download_with_retry "$URL" "$archive_path"

    tar tzf "$archive_path" >/dev/null 2>&1 \
        || err "downloaded archive is not a valid tar.gz (likely truncated or corrupt)"

    verify_archive_checksum "$archive_path" "$SHA_URL"

    # --- extract --------------------------------------------------------------
    tar -xzf "$archive_path" -C "${TMPDIR_INSTALL}/staging"
    new_bin="$(find "${TMPDIR_INSTALL}/staging" -type f -name hina 2>/dev/null | head -n1)"
    [ -n "$new_bin" ] || err "no hina binary found inside ${ARCHIVE_NAME}"
    chmod 0755 "$new_bin"

    # --- atomic install (backup -> stage -> rename -> smoke -> drop backup) --
    backup=""
    if [ -d "${DEST}/hina" ] && [ ! -L "${DEST}/hina" ]; then
        err "${DEST}/hina exists but is a directory, not a file. Remove or rename it first."
    fi
    if [ -e "${DEST}/hina" ] || [ -L "${DEST}/hina" ]; then
        backup="${DEST}/.hina.bak.$$"
        # -P preserves a symlink as a symlink in the backup so a rollback
        # restores the original link instead of severing it.
        cp -Pp "${DEST}/hina" "$backup"
    fi
    staged="${DEST}/.hina.new.$$"
    cp "$new_bin" "$staged"
    chmod 0755 "$staged"

    if ! mv -f "$staged" "${DEST}/hina"; then
        rm -f "$staged"
        if [ -n "$backup" ] && [ -f "$backup" ]; then
            mv -f "$backup" "${DEST}/hina"
        fi
        err "atomic rename failed; rolled back"
    fi

    # post-install smoke test; rollback on failure. Capture stderr to give an
    # actionable hint when the failure is a missing runtime library
    # (e.g. user lacks `brew install openssl@3 brotli` on macOS, or glibc
    # is too old on Linux) rather than a generic "smoke failed".
    smoke_err_file="${TMPDIR_INSTALL}/smoke.err"
    set +e
    bounded_run "${DEST}/hina" --help >/dev/null 2>"$smoke_err_file"
    smoke_exit=$?
    set -e
    smoke_err="$(cat "$smoke_err_file" 2>/dev/null || true)"
    if [ "$smoke_exit" -ne 0 ]; then
        hint=""
        case "$smoke_err" in
            *"dyld"*"Library not loaded"*|*"image not found"*|*"no LC_RPATH"*)
                hint=" — missing dynamic library. On macOS: 'brew install openssl@3 brotli'."
                ;;
            *libssl*|*libcrypto*)
                hint=" — missing OpenSSL runtime. Install your distro's libssl3 / openssl package."
                ;;
            *libbrotli*)
                hint=" — missing brotli runtime. Install your distro's libbrotli package (or 'brew install brotli')."
                ;;
            *GLIBC_*)
                hint=" — system glibc is too old for the pre-built binary. Use a newer distro or build from source."
                ;;
        esac
        if [ -n "$backup" ] && [ -f "$backup" ]; then
            mv -f "$backup" "${DEST}/hina"
            [ -n "$smoke_err" ] && printf '%s\n' "$smoke_err" >&2
            err "installed binary failed smoke test; rolled back to previous version${hint}"
        else
            [ -n "$smoke_err" ] && printf '%s\n' "$smoke_err" >&2
            err "installed binary failed smoke test and no backup to restore${hint}"
        fi
    fi
    [ -n "$backup" ] && rm -f "$backup"
    info "installed ${DEST}/hina"

    # --- PATH setup -----------------------------------------------------------
    on_path=0
    case ":$PATH:" in
        *":$DEST:"*) on_path=1 ;;
    esac

    modify_rc() {
        rc_file="$1"
        line_export="$2"
        marker_begin='# >>> hina installer >>>'
        marker_end='# <<< hina installer <<<'

        if [ -f "$rc_file" ] && grep -qF "$marker_begin" "$rc_file"; then
            info "PATH entry already present in ${rc_file} (skipping)"
            return 0
        fi

        mkdir -p "$(dirname "$rc_file")"
        tmp_rc="${rc_file}.hina-tmp.$$"
        [ -f "$rc_file" ] && cp -p "$rc_file" "$tmp_rc" || : > "$tmp_rc"
        {
            printf '\n%s\n' "$marker_begin"
            printf '%s\n' "$line_export"
            printf '%s\n' "$marker_end"
        } >> "$tmp_rc"
        mv -f "$tmp_rc" "$rc_file"
        info "added PATH entry to ${rc_file}"
    }

    if [ "$on_path" -eq 0 ]; then
        if [ "$NO_MODIFY_PATH" = "1" ]; then
            info "${DEST} is not on PATH. Add it manually: export PATH=\"${DEST}:\$PATH\""
        else
            shell_name="$(basename "${SHELL:-/bin/sh}")"
            case "$shell_name" in
                zsh)
                    modify_rc "${ZDOTDIR:-$HOME}/.zshrc" "export PATH=\"${DEST}:\$PATH\""
                    ;;
                bash)
                    if [ "$OS" = "macos" ]; then
                        modify_rc "${HOME}/.bash_profile" "export PATH=\"${DEST}:\$PATH\""
                    else
                        modify_rc "${HOME}/.bashrc" "export PATH=\"${DEST}:\$PATH\""
                    fi
                    ;;
                fish)
                    modify_rc "${HOME}/.config/fish/config.fish" "set -gx PATH ${DEST} \$PATH"
                    ;;
                *)
                    modify_rc "${HOME}/.profile" "export PATH=\"${DEST}:\$PATH\""
                    ;;
            esac
            info "restart your shell or run: export PATH=\"${DEST}:\$PATH\""
        fi
    fi

    ver_line="$(bounded_run "${DEST}/hina" --version 2>/dev/null | head -n1 || true)"
    [ -n "$ver_line" ] && info "verified: ${ver_line}"

    cat <<EOF

Hina installed. Try:
    hina --help
    hina install <url-to-hina.app.json>

Docs: https://github.com/${REPO}#readme
EOF
}

do_verify() {
    # Self-contained verify path; called from main(). Re-imports helpers via
    # the surrounding `main` function's scope using a small subshell-free
    # pattern: this function is defined inside main() above via shell hoisting?
    # No — POSIX sh does not hoist. So we inline the verify logic here using
    # only top-level helpers (which we redeclare).
    err() { printf 'hina-install: %s\n' "$*" >&2; exit 1; }
    info() { printf 'hina-install: %s\n' "$*"; }
    sha256_of() {
        if command -v sha256sum >/dev/null 2>&1; then
            sha256sum "$1" | awk '{print $1}'
        else
            shasum -a 256 "$1" | awk '{print $1}'
        fi
    }

    [ -x "${DEST}/hina" ] || err "no installed binary at ${DEST}/hina to verify"

    tmp="$(mktemp -d 2>/dev/null || mktemp -d -t hina-verify)"
    trap 'rm -rf "$tmp"' EXIT INT TERM

    info "downloading release archive for ${TAG} ..."
    archive="${tmp}/${ARCHIVE_NAME}"
    attempt=1
    while [ "$attempt" -le 5 ]; do
        if curl --fail --location --proto '=https' --tlsv1.2 \
                --connect-timeout 15 --max-time 600 \
                -C - -o "${archive}.partial" "$URL"; then
            mv "${archive}.partial" "$archive"
            break
        fi
        info "download attempt $attempt failed, retrying..."
        sleep "$attempt"
        attempt=$((attempt + 1))
    done
    [ -f "$archive" ] || err "download failed"

    sha_file="${tmp}/sha256"
    if ! curl --fail --location --proto '=https' --tlsv1.2 \
              --connect-timeout 15 --max-time 60 \
              -o "$sha_file" "$SHA_URL" 2>/dev/null; then
        err "no SHA-256 published for ${TAG} - cannot perform cryptographic integrity check. (Re-)install with HINA_ACTION=reinstall to repair if you suspect corruption."
    fi
    expected_archive="$(awk '{print $1}' "$sha_file" | head -n1)"
    actual_archive="$(sha256_of "$archive")"
    [ "$expected_archive" = "$actual_archive" ] \
        || err "release archive SHA-256 mismatch (the published asset is corrupt or has been tampered with)"

    mkdir -p "${tmp}/staging"
    tar -xzf "$archive" -C "${tmp}/staging"
    release_bin="$(find "${tmp}/staging" -type f -name hina 2>/dev/null | head -n1)"
    [ -n "$release_bin" ] || err "release archive does not contain hina binary"

    release_hash="$(sha256_of "$release_bin")"
    installed_hash="$(sha256_of "${DEST}/hina")"

    if [ "$release_hash" = "$installed_hash" ]; then
        info "integrity OK: ${DEST}/hina matches release ${TAG}"
    else
        printf '\nIntegrity check FAILED\n' >&2
        printf '  Release binary SHA-256:   %s\n' "$release_hash" >&2
        printf '  Installed binary SHA-256: %s\n' "$installed_hash" >&2
        printf '\nThe installed binary does not match the released %s artifact.\n' "$TAG" >&2
        printf 'Run installer with HINA_ACTION=reinstall to repair.\n' >&2
        exit 1
    fi
}

main "$@"
