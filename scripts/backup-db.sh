#!/bin/bash
# scripts/backup-db.sh — Backup PostgreSQL database via docker compose
# Usage: ./scripts/backup-db.sh
# Cron (daily at 3 AM): 0 3 * * * /path/to/expense-tracker/scripts/backup-db.sh
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
BACKUP_DIR="$ROOT/backups"
TIMESTAMP=$(date +"%Y-%m-%d_%H-%M-%S")
FILENAME="expense_tracker_${TIMESTAMP}.sql.gz"

mkdir -p "$BACKUP_DIR"

cd "$ROOT"

echo "Backing up database to $BACKUP_DIR/$FILENAME ..."

docker compose exec -T db pg_dump \
  -U "${POSTGRES_USER:-expense_user}" \
  "${POSTGRES_DB:-expense_tracker}" \
  | gzip > "$BACKUP_DIR/$FILENAME"

echo "Backup complete: $BACKUP_DIR/$FILENAME"

# Optionally remove backups older than 30 days
find "$BACKUP_DIR" -name "expense_tracker_*.sql.gz" -mtime +30 -delete
