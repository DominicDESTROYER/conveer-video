#!/usr/bin/env bash
# Конвейер видео TOKENFALL (Linux / macOS).
#   ./run.sh                    — все сцены из conveer.json
#   ./run.sh boss_* death       — выбранные сцены (имя или шаблон с *)
#   ./run.sh list               — список сцен
#   ./run.sh --quick cell_01    — быстро и в низком качестве (640×360, 24 к/с)
# Папка игры берётся из "gameRoot" в conveer.json (по умолчанию ../game-token — рядом с конвейером).
set -euo pipefail
cd "$(dirname "$0")"
root=$(sed -n 's/.*"gameRoot"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' conveer.json | head -n 1)
root=${root:-../game-token}
case "$root" in
  /*) game="$root" ;;
  *) game="$(pwd)/$root" ;;
esac
if [ ! -d "$game/Assets/Scripts" ]; then
  echo "Не найдена игра: $game — положите game-token рядом с conveer-video или поправьте gameRoot в conveer.json" >&2
  exit 2
fi
if ! out=$(dotnet build -c Release -nologo -v q -p:GameRoot="$game" 2>&1); then
  echo "$out" | grep -E " error " >&2 || echo "$out" >&2
  exit 1
fi
exec dotnet bin/Release/net8.0/Conveer.dll "$@"
