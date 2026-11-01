#!/usr/bin/env bash
# Ручная настройка сервера без CI: берёт настройки из старого appsettings.json
# (например из бекапа VPS), создаёт Podman-секреты и пишет ~/.config/vlk/vlk.env.
#   bash deploy/import-secrets.sh ~/restore/root/<бот>/out/appsettings.json
# Значения нигде не печатаются. После импорта файл с токенами можно удалить.
set -euo pipefail
cfg=${1:?usage: $0 <appsettings.json>}

get() {  # get <ключ через двоеточие>[|запасной ключ]: значение из JSON, ошибка если нет, пусто или заглушка
  python3 -I -c '
import json, sys
cfg = json.load(open(sys.argv[1]))
v = None
for key in sys.argv[2].split("|"):
    try:
        v = cfg
        for p in key.split(":"):
            v = v[p]
        break
    except (KeyError, TypeError):
        v = None
if v is None or not str(v).strip() or "PLACEHOLDER" in str(v).upper():
    sys.exit(sys.argv[2] + ": нет, пусто или заглушка")
sys.stdout.write(str(v))' "$cfg" "$1"
}

while read -r secret key; do
  get "$key" | podman secret create --replace "$secret" - >/dev/null
  echo "ok: секрет $secret"
done <<'LIST'
vlk-token Token|TelegramBot:Token
vlk-aiapikey AiApikey
LIST

admin_ids=$(get ADMIN_IDS)
basic_url=$(get BASIC_URL)
envfile=~/.config/vlk/vlk.env
mkdir -p "$(dirname "$envfile")"
( umask 077; printf 'ADMIN_IDS=%s\nBASIC_URL=%s\n' "$admin_ids" "$basic_url" > "$envfile" )
echo "ok: $envfile"
