#!/usr/bin/env bash
set -euo pipefail
unset DOTNET_ROOT
export DOTNET_MULTILEVEL_LOOKUP=0
app="$1"
export SNNU_MAC_TEST_HOME="$(mktemp -d)"
trap 'rm -rf "$SNNU_MAC_TEST_HOME"' EXIT
executable="$app/Contents/MacOS/SNNUAutoLogin.Mac"
file "$executable" | grep arm64
codesign --verify --deep --strict "$app"
"$executable" --core-selftest
"$executable" --smoke-test >"$SNNU_MAC_TEST_HOME/console.log" 2>&1 &
task=$!
sleep 1
"$executable"
result=0
for attempt in $(seq 1 40); do
  if ! kill -0 "$task" 2>/dev/null; then
    wait "$task" || result=$?
    break
  fi
  sleep 1
done
if kill -0 "$task" 2>/dev/null; then
  kill "$task"
  result=1
fi
if [[ "$result" != 0 || ! -f "$SNNU_MAC_TEST_HOME/smoke-ok" ]]; then
  cat "$SNNU_MAC_TEST_HOME/console.log"
  [[ ! -f "$SNNU_MAC_TEST_HOME/startup-error.txt" ]] || cat "$SNNU_MAC_TEST_HOME/startup-error.txt"
  exit 1
fi
test ! -f "$SNNU_MAC_TEST_HOME/startup-error.txt"
echo "macOS app lifecycle smoke test passed"

# Exercise Finder's LaunchServices entry point from a path with spaces and Chinese.
mkdir -p "$SNNU_MAC_TEST_HOME/中文 路径"
cp -R "$app" "$SNNU_MAC_TEST_HOME/中文 路径/SNNUAutoLogin.app"
rm "$SNNU_MAC_TEST_HOME/smoke-ok"
open -n -W --env "SNNU_MAC_TEST_HOME=$SNNU_MAC_TEST_HOME" \
  --env DOTNET_ROOT=/nonexistent --env DOTNET_MULTILEVEL_LOOKUP=0 \
  --stdout "$SNNU_MAC_TEST_HOME/finder.log" --stderr "$SNNU_MAC_TEST_HOME/finder-error.log" \
  "$SNNU_MAC_TEST_HOME/中文 路径/SNNUAutoLogin.app" --args --smoke-test &
launcher=$!
for attempt in $(seq 1 40); do
  if ! kill -0 "$launcher" 2>/dev/null; then break; fi
  sleep 1
done
if kill -0 "$launcher" 2>/dev/null; then
  kill "$launcher"
  echo "Finder launch timed out"
  exit 1
fi
wait "$launcher"
if [[ ! -f "$SNNU_MAC_TEST_HOME/smoke-ok" || -f "$SNNU_MAC_TEST_HOME/startup-error.txt" ]]; then
  cat "$SNNU_MAC_TEST_HOME/finder.log" "$SNNU_MAC_TEST_HOME/finder-error.log"
  [[ ! -f "$SNNU_MAC_TEST_HOME/startup-error.txt" ]] || cat "$SNNU_MAC_TEST_HOME/startup-error.txt"
  exit 1
fi
echo "Finder launch from a Chinese path with spaces passed"
