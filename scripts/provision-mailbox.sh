#!/bin/bash
# scripts/provision-mailbox.sh — create the sender mailbox in mailcow via its API.
#
# Usage:
#   MAILCOW_HOST=https://mail.example.com \
#   MAILCOW_API_KEY=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx \
#   ./scripts/provision-mailbox.sh <local-part> <domain> <password> [quota-mb]
#
# Example:
#   ./scripts/provision-mailbox.sh noreply micutu.com 'S3cretPass!' 256
#
# Notes:
# - Requires the API key to have write access (mailcow: Configuration → API).
# - The domain must already exist in mailcow.
# - After creating it, put the same credentials in .env (Smtp__User / Smtp__Password).
set -euo pipefail

LOCAL_PART="${1:?usage: provision-mailbox.sh <local-part> <domain> <password> [quota-mb]}"
DOMAIN="${2:?domain required}"
PASSWORD="${3:?password required}"
QUOTA="${4:-256}"

: "${MAILCOW_HOST:?Set MAILCOW_HOST (e.g. https://mail.example.com)}"
: "${MAILCOW_API_KEY:?Set MAILCOW_API_KEY}"

ADDR="${LOCAL_PART}@${DOMAIN}"
echo "Creating mailbox ${ADDR} on ${MAILCOW_HOST} (quota ${QUOTA} MB)..."

RESPONSE=$(curl -fsS -X POST "${MAILCOW_HOST%/}/api/v1/add/mailbox" \
  -H "X-API-Key: ${MAILCOW_API_KEY}" \
  -H "Content-Type: application/json" \
  -d @- <<JSON
{
  "local_part": "${LOCAL_PART}",
  "domain": "${DOMAIN}",
  "name": "Expense Tracker",
  "password": "${PASSWORD}",
  "password2": "${PASSWORD}",
  "quota": "${QUOTA}",
  "active": "1",
  "force_pw_update": "0"
}
JSON
)

echo "mailcow response: ${RESPONSE}"
echo
echo "If successful, set in .env:"
echo "  Smtp__User=${ADDR}"
echo "  Smtp__Password=<the password you just used>"
echo "  Smtp__From=${ADDR}"
