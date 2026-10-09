#!/usr/bin/env bash
set -euo pipefail

: "${TG_TOKEN:?TG_TOKEN is required}"
: "${TG_CHAT:?TG_CHAT is required}"
: "${TG_MESSAGE:?TG_MESSAGE is required}"
: "${TG_ACTION_URL:?TG_ACTION_URL is required}"

# send <text> [curl args...]: печатает ответ Telegram, при ошибке API возвращает не 0
send() {
  local text="$1"
  shift
  curl -sS --fail-with-body --retry 3 -X POST "https://api.telegram.org/bot${TG_TOKEN}/sendMessage" \
    --data-urlencode "chat_id=${TG_CHAT}" \
    --data-urlencode "text=${text}" \
    --data-urlencode "reply_markup={\"inline_keyboard\":[[{\"text\":\"Открыть GitHub Action\",\"url\":\"${TG_ACTION_URL}\"}]]}" \
    "$@" && echo
}

# `_` или `*` в имени ветки/автора ломают Markdown (HTTP 400) — тогда шлём тот же текст без разметки
send "$TG_MESSAGE" --data-urlencode "parse_mode=Markdown" \
  || send "${TG_MESSAGE//[\*\`]/}" \
  || echo "::warning::Не удалось отправить уведомление в Telegram"
