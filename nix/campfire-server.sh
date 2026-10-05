# shellcheck shell=bash
# Starts the Campfire development server and a private Redis.
# Exit, Ctrl-C, or hangup stops both and deletes the Redis data directory.
# The development SQLite database under storage/db is left in place.
# Gems stay in $XDG_CACHE_HOME/once-campfire-dotnet (or ~/.cache).

usage() {
  cat <<'EOF'
Usage: campfire-server [PORT]
       campfire-server [--port PORT] [--bind HOST]

  nix run .#server
  nix run .#server -- 4000
  nix run .#server -- --port 4000 --bind 127.0.0.1

Runs Campfire in development and a Redis used only by this process.
The default port is 3000, or the PORT environment variable when that is set.
The default bind address is 127.0.0.1.

Closing this process stops Rails and Redis and deletes that Redis's data.
storage/db/development.sqlite3 is kept so the next run still has the app database.
EOF
}

# Invoked by the EXIT trap, including after HUP, INT, and TERM.
# shellcheck disable=SC2329
cleanup() {
  status=$?
  trap - EXIT HUP INT TERM
  stop_group "$rails_pid" || true
  stop_group "$redis_pid" || true
  rm -rf "$runtime" || true
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
  while [[ "$waited" -lt 30 ]]; do
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
if [[ -z "$root" || ! -f "$root/Gemfile" || ! -f "$root/bin/rails" ]]; then
  echo "campfire-server: run this from the Campfire checkout" >&2
  exit 1
fi
cd "$root" || exit 1

probe_host="$bind_host"
if [[ "$probe_host" == "0.0.0.0" ]]; then
  probe_host="127.0.0.1"
fi
if ruby -e 'require "socket"; TCPSocket.new(ARGV[0], Integer(ARGV[1])).close' "$probe_host" "$port" 2>/dev/null; then
  echo "campfire-server: ${probe_host}:${port} is already in use" >&2
  exit 1
fi

runtime="$(mktemp -d "${TMPDIR:-/tmp}/campfire-server.XXXXXX")"
redis_pid=""
rails_pid=""
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

cache_root="${XDG_CACHE_HOME:-${HOME}/.cache}/once-campfire-dotnet"
lock_hash="$(sha256sum "$root/Gemfile.lock" | cut -d ' ' -f 1)"
bundle_path="${cache_root}/bundle/${lock_hash}"
gem_home="${cache_root}/gem-home"
mkdir -p "$bundle_path" "$gem_home" "${cache_root}/bundler-home" "${runtime}/app-bundle"

export GEM_HOME="$gem_home"
export GEM_PATH="$gem_home"
export PATH="${gem_home}/bin:${PATH}"
export BUNDLE_PATH="$bundle_path"
export BUNDLE_USER_HOME="${cache_root}/bundler-home"
export BUNDLE_USER_CONFIG="${runtime}/bundler-config"
export BUNDLE_APP_CONFIG="${runtime}/app-bundle"
export BUNDLE_GEMFILE="${root}/Gemfile"
export BUNDLE_WITHOUT="test"
export BUNDLE_FROZEN="true"
make_jobs="$(nproc)"
MAKEFLAGS="-j${make_jobs}"
export MAKEFLAGS
export RAILS_ENV="development"
export PIDFILE="${runtime}/puma.pid"

bundle="${gem_home}/bin/bundle"
if [[ ! -x "$bundle" ]] || ! "$bundle" --version | grep -q '4\.0\.13'; then
  echo "campfire-server: installing bundler 4.0.13"
  gem install bundler -v 4.0.13 --no-document
fi

echo "campfire-server: ruby $(ruby -e 'print RUBY_VERSION')"
echo "campfire-server: installing gems into ${bundle_path}"
"$bundle" install --jobs "$(nproc)" --retry 3

redis_port="$(ruby -e 'require "socket"; s = TCPServer.new("127.0.0.1", 0); puts s.addr[1]; s.close')"
ruby -e 'Process.setsid; exec(*ARGV)' -- \
  redis-server \
  --bind 127.0.0.1 \
  --port "$redis_port" \
  --daemonize no \
  --appendonly no \
  --save "" \
  --dir "$runtime" \
  --protected-mode yes \
  --logfile "${runtime}/redis.log" \
  --pidfile "${runtime}/redis.pid" \
  --dbfilename dump.rdb &
redis_pid=$!

redis_ready=0
for _ in {1..50}; do
  if redis-cli -h 127.0.0.1 -p "$redis_port" ping 2>/dev/null | grep -q PONG; then
    redis_ready=1
    break
  fi
  if ! kill -0 "$redis_pid" 2>/dev/null; then
    echo "campfire-server: redis exited before it answered" >&2
    cat "${runtime}/redis.log" >&2 || true
    exit 1
  fi
  sleep 0.1
done
if [[ "$redis_ready" -ne 1 ]]; then
  echo "campfire-server: redis did not answer on 127.0.0.1:${redis_port}" >&2
  cat "${runtime}/redis.log" >&2 || true
  exit 1
fi

export REDIS_URL="redis://127.0.0.1:${redis_port}/0"
export PORT="$port"
export BIND="tcp://${bind_host}:${port}"

if ! "$bundle" exec ruby -e 'require "redis"; abort unless Redis.new(url: ENV.fetch("REDIS_URL")).ping == "PONG"'; then
  echo "campfire-server: the redis gem could not reach ${REDIS_URL}" >&2
  exit 1
fi

echo "campfire-server: preparing the database"
"$bundle" exec bin/rails db:prepare

display_host="$bind_host"
if [[ "$display_host" == "0.0.0.0" ]]; then
  display_host="127.0.0.1"
fi
echo "campfire-server: http://${display_host}:${port}"
echo "campfire-server: redis ${REDIS_URL} (removed on exit)"
echo "campfire-server: database storage/db/development.sqlite3 (kept)"

ruby -e 'Process.setsid; exec(*ARGV)' -- "$bundle" exec bin/rails server &
rails_pid=$!

set +e
wait "$rails_pid"
status=$?
set -e
exit "$status"
