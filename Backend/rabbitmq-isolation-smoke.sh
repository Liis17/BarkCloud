#!/usr/bin/env bash
set -euo pipefail

[[ "${BARKCLOUD_ISOLATED_INFRA:-}" == YES ]] || {
  echo "Refusing RabbitMQ smoke checks without BARKCLOUD_ISOLATED_INFRA=YES." >&2
  exit 2
}
[[ "${BARKCLOUD_APPS_STOPPED:-}" == YES ]] || {
  echo "Stop backend applications and set BARKCLOUD_APPS_STOPPED=YES." >&2
  exit 2
}
: "${BARKCLOUD_RABBITMQ_MANAGEMENT_URL:?Set the management URL of the isolated RabbitMQ node.}"
: "${RABBITMQ_USERS_PASSWORD:?Export the Users RabbitMQ password.}"
: "${RABBITMQ_IDENTITY_PASSWORD:?Export the Identity RabbitMQ password.}"
: "${RABBITMQ_FILES_PASSWORD:?Export the Files RabbitMQ password.}"
: "${RABBITMQ_NOTIFICATION_PASSWORD:?Export the Notification RabbitMQ password.}"
: "${RABBITMQ_TORRENT_PASSWORD:?Export the Torrent RabbitMQ password.}"
export RABBITMQ_VIRTUAL_HOST=${RABBITMQ_VIRTUAL_HOST:-/}

if command -v docker >/dev/null 2>&1 \
   && docker compose config --services 2>/dev/null | grep -qx 'cloud-rabbitmq'; then
  rabbitmqctl() { docker compose exec -T cloud-rabbitmq rabbitmqctl "$@"; }
elif command -v rabbitmqctl >/dev/null 2>&1; then
  rabbitmqctl() { command rabbitmqctl "$@"; }
else
  echo "RabbitMQ CLI is required to temporarily authorize the HTTP API smoke test." >&2
  exit 2
fi

# The management API requires a management tag; resource ACLs still restrict the tested operations.
# The opt-in isolated smoke test removes these temporary tags on exit.
smoke_users=(barkcloud_users barkcloud_identity barkcloud_files barkcloud_torrent barkcloud_notification)
cleanup_tags() {
  local user
  for user in "${smoke_users[@]}"; do
    rabbitmqctl set_user_tags "$user" >/dev/null 2>&1 || true
  done
}
trap cleanup_tags EXIT
for user in "${smoke_users[@]}"; do
  rabbitmqctl set_user_tags "$user" management >/dev/null
done

python3 - <<'PY'
import base64
import json
import os
import secrets
import urllib.error
import urllib.parse
import urllib.request

base = os.environ["BARKCLOUD_RABBITMQ_MANAGEMENT_URL"].rstrip("/")
vhost = urllib.parse.quote(os.environ.get("RABBITMQ_VIRTUAL_HOST", "/"), safe="")

def request(user, password, method, path, payload=None, denied=False):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request(base + path, data=data, method=method)
    token = base64.b64encode(f"{user}:{password}".encode()).decode()
    req.add_header("Authorization", "Basic " + token)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            body = response.read()
            if denied:
                raise AssertionError(f"{user} unexpectedly succeeded on {method} {path}")
            return json.loads(body) if body else None
    except urllib.error.HTTPError as error:
        body = error.read().decode(errors="replace").lower()
        if not denied:
            raise AssertionError(f"{user} request failed ({error.code}) for {method} {path}") from None
        if error.code not in (400, 401, 403, 500) or not any(
            marker in body for marker in ("access_refused", "access refused", "not_authorized", "not authorized")
        ):
            raise AssertionError(f"Expected an authorization denial, got HTTP {error.code} for {method} {path}") from None
        return None

def name(value):
    return urllib.parse.quote(value, safe="")

def declare_exchange(user, password, exchange):
    request(user, password, "PUT", f"/api/exchanges/{vhost}/{name(exchange)}", {
        "type": "fanout", "durable": True, "auto_delete": False, "arguments": {}
    })

def declare_queue(user, password, queue):
    temporary = "-bus-" in queue
    request(user, password, "PUT", f"/api/queues/{vhost}/{name(queue)}", {
        "durable": not temporary, "auto_delete": temporary, "arguments": {}
    })

def bind(user, password, source, destination, destination_type):
    request(user, password, "POST",
            f"/api/bindings/{vhost}/e/{name(source)}/{destination_type}/{name(destination)}", {
                "routing_key": "", "arguments": {}
            })

def publish(user, password, exchange, payload):
    result = request(user, password, "POST", f"/api/exchanges/{vhost}/{name(exchange)}/publish", {
        "properties": {}, "routing_key": "", "payload": payload, "payload_encoding": "string"
    })
    if not result.get("routed"):
        raise AssertionError(f"Allowed publish to {exchange} was not routed")

def get_message(user, password, queue):
    result = request(user, password, "POST", f"/api/queues/{vhost}/{name(queue)}/get", {
        "count": 1, "ackmode": "ack_requeue_false", "encoding": "auto", "truncate": 50000
    })
    if not result:
        raise AssertionError(f"Expected a message in {queue}")
    return result[0].get("payload", "")

users = ("barkcloud_users", os.environ["RABBITMQ_USERS_PASSWORD"])
identity = ("barkcloud_identity", os.environ["RABBITMQ_IDENTITY_PASSWORD"])
notification = ("barkcloud_notification", os.environ["RABBITMQ_NOTIFICATION_PASSWORD"])
torrent = ("barkcloud_torrent", os.environ["RABBITMQ_TORRENT_PASSWORD"])

user_exchange = "BarkCloud.Shared.Queue.Users:UserDeleted"
user_queue = "bark-torrent-bus-" + secrets.token_hex(16)
declare_exchange(*users, user_exchange)
declare_queue(*torrent, user_queue)
bind(*torrent, user_exchange, user_queue, "q")
publish(*users, user_exchange, "audit9-allowed-user-event")
assert "audit9-allowed-user-event" in get_message(*torrent, user_queue)
print("PASS RabbitMQ allowed publish, bind, and consume")

email_exchange = "BarkCloud.Shared.Queue.Notifications:EmailNotification"
notification_base = "BarkCloud.Shared.Queue.Notifications:Notification"
email_fault = "MassTransit:Fault--BarkCloud.Shared.Queue.Notifications:EmailNotification--"
fault_base = "MassTransit:Fault"
declare_exchange(*notification, email_exchange)
declare_exchange(*notification, notification_base)
declare_exchange(*notification, email_fault)
declare_exchange(*notification, fault_base)
declare_queue(*notification, "notifications-email-handler")
declare_queue(*notification, "notifications-email-handler_error")
declare_queue(*notification, "notifications-email-handler_skipped")
bind(*notification, email_exchange, "notifications-email-handler", "q")
bind(*identity, email_exchange, notification_base, "e")
bind(*notification, email_fault, fault_base, "e")
publish(*identity, email_exchange, "audit9-email-flow")
assert "audit9-email-flow" in get_message(*notification, "notifications-email-handler")
print("PASS RabbitMQ Identity-to-Notification email flow")

def assert_email_denied(credentials, bus_prefix):
    user, password = credentials
    queue = bus_prefix + secrets.token_hex(16)
    declare_queue(user, password, queue)
    for source in (email_exchange, notification_base, email_fault):
        request(user, password, "POST", f"/api/bindings/{vhost}/e/{name(source)}/q/{name(queue)}",
                {"routing_key": "", "arguments": {}}, denied=True)
    for protected_queue in ("notifications-email-handler", "notifications-email-handler_error", "notifications-email-handler_skipped"):
        request(user, password, "POST", f"/api/queues/{vhost}/{name(protected_queue)}/get", {
            "count": 1, "ackmode": "ack_requeue_false", "encoding": "auto", "truncate": 50000
        }, denied=True)
    for exchange in (email_exchange, notification_base, email_fault):
        request(user, password, "POST", f"/api/exchanges/{vhost}/{name(exchange)}/publish", {
            "properties": {}, "routing_key": "", "payload": "audit9-forbidden", "payload_encoding": "string"
        }, denied=True)

assert_email_denied(users, "bark-users-bus-")
assert_email_denied(("barkcloud_files", os.environ["RABBITMQ_FILES_PASSWORD"]), "bark-files-bus-")
assert_email_denied(torrent, "bark-torrent-bus-")
print("PASS RabbitMQ Users/Files/Torrent email bind/read/publish denial")
PY
