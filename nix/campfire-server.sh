# shellcheck shell=bash
# Starts the ASP.NET Core Campfire server.
# Exit, Ctrl-C, or hangup stops that process tree.
# The SQLite database under storage/db is left in place.

usage() {
  cat <<'EOF'
Usage: campfire-server [PORT]
       campfire-server [--port PORT] [--bind HOST]

  nix run .#server
  nix run .#server -- 4000
  nix run .#server -- --port 4000 --bind 127.0.0.1

Runs the ASP.NET Core Campfire server.
The default port is 3000, or the PORT environment variable when that is set.
The default bind address is 127.0.0.1.

Closing this process stops the server.
storage/db/campfire.sqlite is kept so the next run still has the app database.
EOF
}

# Invoked by the EXIT trap, including after HUP, INT, and TERM.
# shellcheck disable=SC2329
cleanup() {
  status=$?
  trap - EXIT HUP INT TERM
  stop_group "$server_pid" || true
  exit "$status"
}

# Invoked by cleanup.
# shellcheck disable=SC2329
stop_group() {
  local pid="$1"
  local waited=0
  if [[ -z "$pid" ]]; then
    return 0
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    wait "$pid" 2>/dev/null || true
    return 0
  fi
  kill -TERM -- "-${pid}" 2>/dev/null || kill -TERM "$pid" 2>/dev/null || true
  while [[ "$waited" -lt 50 ]]; do
    if ! kill -0 "$pid" 2>/dev/null; then
      wait "$pid" 2>/dev/null || true
      return 0
    fi
    sleep 0.1
    waited=$((waited + 1))
  done
  kill -KILL -- "-${pid}" 2>/dev/null || kill -KILL "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  return 0
}

port="${PORT:-3000}"
bind_host="127.0.0.1"

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h | --help)
      usage
      exit 0
      ;;
    --port)
      if [[ $# -lt 2 ]]; then
        echo "campfire-server: --port needs a value" >&2
        exit 1
      fi
      port="$2"
      shift 2
      ;;
    --port=*)
      port="${1#*=}"
      shift
      ;;
    --bind)
      if [[ $# -lt 2 ]]; then
        echo "campfire-server: --bind needs a value" >&2
        exit 1
      fi
      bind_host="$2"
      shift 2
      ;;
    --bind=*)
      bind_host="${1#*=}"
      shift
      ;;
    --)
      shift
      ;;
    *)
      if [[ "$1" =~ ^[0-9]+$ ]]; then
        port="$1"
        shift
      else
        echo "campfire-server: unexpected argument: $1" >&2
        usage >&2
        exit 1
      fi
      ;;
  esac
done

if [[ -z "$bind_host" ]]; then
  echo "campfire-server: --bind needs a host" >&2
  exit 1
fi
if [[ ! "$port" =~ ^[0-9]+$ ]] || [[ "$port" -lt 1 ]] || [[ "$port" -gt 65535 ]]; then
  echo "campfire-server: port must be between 1 and 65535" >&2
  exit 1
fi

root="$(git rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$root" || ! -f "$root/src/Campfire.Web/Campfire.Web.csproj" ]]; then
  echo "campfire-server: run this from the Campfire checkout" >&2
  exit 1
fi
cd "$root" || exit 1

probe_host="$bind_host"
if [[ "$probe_host" == "0.0.0.0" ]]; then
  probe_host="127.0.0.1"
fi
if { echo >/dev/tcp/"$probe_host"/"$port"; } >/dev/null 2>&1; then
  echo "campfire-server: ${probe_host}:${port} is already in use" >&2
  exit 1
fi

server_pid=""
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

export ASPNETCORE_URLS="http://${bind_host}:${port}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export CAMPFIRE_DB="${CAMPFIRE_DB:-${root}/storage/db/campfire.sqlite}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_MULTILEVEL_LOOKUP=0

display_host="$bind_host"
if [[ "$display_host" == "0.0.0.0" ]]; then
  display_host="127.0.0.1"
fi
echo "campfire-server: http://${display_host}:${port}"
echo "campfire-server: database ${CAMPFIRE_DB} (kept)"

setsid dotnet run --project "$root/src/Campfire.Web/Campfire.Web.csproj" --no-launch-profile &
server_pid=$!

set +e
wait "$server_pid"
status=$?
set -e
exit "$status"
