#!/usr/bin/env bash
# Создаёт (или обновляет) Podman-секреты vlk из appsettings.json, например из бекапа VPS:
#   bash deploy/import-secrets.sh ~/restore/root/<бот>/out/appsettings.json
# Значения нигде не печатаются. После импорта файл с токенами можно удалить.
set -euo pipefail
cfg=${1:?usage: $0 <appsettings.json>}

get() {  # get <ключ через двоеточие>[|запасной ключ]: значение из JSON, ошибка если пусто или заглушка
  python3 -I -c '
import json, sys
cfg = json.load(open(sys.argv[1]))
for key in sys.argv[2].split("|"):
    v = cfg
    try:
        for p in key.split(":"):
            v = v[p]
        break
    except (KeyError, TypeError):
        v = None
if v is None:
    sys.exit(sys.argv[2] + ": нет в конфиге")
v = str(v)
if not v.strip() or "PLACEHOLDER" in v.upper():
    sys.exit(sys.argv[2] + ": пусто или заглушка")
sys.stdout.write(v)' "$cfg" "$1"
}

while read -r secret key; do
  value=$(get "$key")
  printf %s "$value" | podman secret create --replace "$secret" - >/dev/null
  echo "ok: $secret"
done <<'LIST'
vlk-telegram-token Token|TelegramBot:Token
vlk-ai-apikey AiApikey
vlk-admin-ids ADMIN_IDS
vlk-basic-url BASIC_URL
LIST
