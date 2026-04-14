#!/bin/bash
# scripts/restore-db.sh — Restore PostgreSQL backup via docker compose
# Usage: ./scripts/restore-db.sh backups/expense_tracker_2026-04-14_03-00-00.sql.gz
set -e

if [[ -z "$1" ]]; then
  echo "Usage: $0 <backup-file.sql.gz>"
  exit 1
fi

BACKUP_FILE="$1"

if [[ ! -f "$BACKUP_FILE" ]]; then
  echo "Error: file not found: $BACKUP_FILE"
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

cd "$ROOT"

echo "Restoring from $BACKUP_FILE ..."
echo "WARNING: This will DROP and re-create the expense_tracker schema."
read -rp "Continue? [y/N] " confirm
[[ "$confirm" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 0; }

gunzip -c "$BACKUP_FILE" | docker compose exec -T db psql \
  -U "${POSTGRES_USER:-expense_user}" \
  "${POSTGRES_DB:-expense_tracker}"

echo "Restore complete."
