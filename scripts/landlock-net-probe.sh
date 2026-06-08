#!/usr/bin/env bash
#
# Landlock network-sandbox integration probe.
#
# Proves that Hina ENFORCES the `network` capability via Landlock net rules
# (ABI >= 4 / kernel 6.7+). It starts a local TCP listener, then runs a
# sandboxed python3 twice:
#   1. with --deny-network  -> the connect() must be DENIED (EACCES).
#   2. without --deny-network -> the connect() must SUCCEED (control: proves we
#      don't break networking when the capability is granted).
#
# On a kernel older than 6.7 (Landlock ABI < 4) Hina cannot enforce net scoping
# and logs "network not enforced"; the probe then SKIP-passes so the CI job stays
# green on older runners. Also skip-passes if python3 is unavailable.
#
# Usage: landlock-net-probe.sh <hina-exec> [hina-exec-args...]
set -u

if [ "$#" -lt 1 ]; then
  echo "usage: $0 <hina-exec> [args...]" >&2
  exit 2
fi
HINA=( "$@" )

PY="$(command -v python3 || true)"
if [ -z "$PY" ]; then
  echo "SKIP: python3 not available; passing."
  exit 0
fi

PORT=19099
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; [ -n "${LISTENER_PID:-}" ] && kill "$LISTENER_PID" 2>/dev/null' EXIT
APP="$WORK/app"; mkdir -p "$APP"
READY="$WORK/ready"

# Local TCP listener (NOT sandboxed). Writes a ready marker once bound.
python3 - "$PORT" "$READY" <<'PY' &
import socket, sys, time
port = int(sys.argv[1]); ready = sys.argv[2]
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", port)); s.listen()
open(ready, "w").write("ready")
time.sleep(60)
PY
LISTENER_PID=$!

# Wait for the listener to bind (up to ~3s).
for _ in $(seq 1 30); do [ -f "$READY" ] && break; sleep 0.1; done
if [ ! -f "$READY" ]; then
  echo "SKIP: listener failed to bind; passing."
  exit 0
fi

CONNECT="import socket
try:
    s = socket.socket(); s.settimeout(3); s.connect(('127.0.0.1', $PORT)); print('RESULT=OK')
except PermissionError:
    print('RESULT=BLOCKED')
except Exception as e:
    print('RESULT=ERR:%s' % e)"

DENY_ERR="$WORK/deny.err"
DENY_OUT="$(
  "${HINA[@]}" dev sandbox-run --app-dir "$APP" --allow /proc:ro --deny-network \
    -- "$PY" -c "$CONNECT" 2>"$DENY_ERR"
)"
ALLOW_OUT="$(
  "${HINA[@]}" dev sandbox-run --app-dir "$APP" --allow /proc:ro \
    -- "$PY" -c "$CONNECT" 2>/dev/null
)"

echo "---- deny-network stdout ----"; echo "$DENY_OUT"
echo "---- deny-network stderr ----"; cat "$DENY_ERR"
echo "---- allow-network stdout ----"; echo "$ALLOW_OUT"
echo "---- kernel: $(uname -r) ----"

if grep -q "network not enforced" "$DENY_ERR" || echo "$DENY_OUT" | grep -q "network not enforced"; then
  echo "SKIP: Landlock net scoping unsupported (kernel $(uname -r), ABI < 4); passing."
  exit 0
fi

if echo "$DENY_OUT" | grep -q "RESULT=BLOCKED" && echo "$ALLOW_OUT" | grep -q "RESULT=OK"; then
  echo "PASS: TCP connect denied under --deny-network, allowed without it."
  exit 0
fi

echo "FAIL: expected deny=BLOCKED + allow=OK (got deny='$DENY_OUT' allow='$ALLOW_OUT')." >&2
exit 1
