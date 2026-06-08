#!/usr/bin/env bash
#
# Landlock filesystem-sandbox integration probe.
#
# Runs a child (/bin/sh) under a Hina sandbox that grants:
#   - the system dirs the interpreter needs (read-only)
#   - a "documents" dir (read-write)
# but deliberately does NOT grant a "secret" dir. Under real Landlock
# enforcement the child must be DENIED reading the secret and ALLOWED writing
# the document. On a host without Landlock (old kernel / disabled), Hina logs
# "cannot enforce" and runs unsandboxed — the probe then skips with success so
# the CI job stays green on non-Landlock runners (e.g. macOS).
#
# Usage: landlock-probe.sh <hina-exec> [hina-exec-args...]
#   e.g. landlock-probe.sh ./Hina.CLI
#        landlock-probe.sh dotnet bin/.../Hina.CLI.dll
set -u

if [ "$#" -lt 1 ]; then
  echo "usage: $0 <hina-exec> [args...]" >&2
  exit 2
fi
HINA=( "$@" )

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
APP="$WORK/app"; DOCS="$WORK/docs"; SECRET="$WORK/secret"
mkdir -p "$APP" "$DOCS" "$SECRET"
echo "topsecret" > "$SECRET/key"

STDERR="$WORK/stderr"

# The inner command interpolates absolute paths (the sandboxed shell does not
# inherit our shell variables reliably across the exec boundary).
INNER="r=0; w=0
cat '$SECRET/key' >/dev/null 2>&1 && r=1
echo data > '$DOCS/out' 2>/dev/null && w=1
echo \"READ=\$r WRITE=\$w\""

OUT="$(
  "${HINA[@]}" dev sandbox-run \
    --app-dir "$APP" \
    --allow /usr:ro --allow /lib:ro --allow /lib64:ro --allow /bin:ro \
    --allow /etc:ro --allow /dev:rw \
    --allow "$DOCS:rw" \
    -- /bin/sh -c "$INNER" 2>"$STDERR"
)"

echo "---- probe stdout ----"; echo "$OUT"
echo "---- probe stderr ----"; cat "$STDERR"
echo "---- kernel: $(uname -r) ----"

if grep -q "cannot enforce" "$STDERR" || echo "$OUT" | grep -q "cannot enforce"; then
  echo "SKIP: Landlock not supported on this host (kernel $(uname -r)); passing."
  exit 0
fi

if echo "$OUT" | grep -q "READ=0 WRITE=1"; then
  echo "PASS: secret read denied, document write allowed under Landlock."
  exit 0
fi

echo "FAIL: expected 'READ=0 WRITE=1' (secret denied, docs writable)." >&2
exit 1
