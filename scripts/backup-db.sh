#!/bin/bash
# Private PostgreSQL SQL backup; publish only after a complete successful dump.
set -euo pipefail
umask 077

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
BACKUP_DIR="${EXPENSE_BACKUP_DIR:-$ROOT/backups}"
TIMESTAMP=$(date -u +"%Y-%m-%d_%H-%M-%S_%N")
FILENAME="expense_tracker_${TIMESTAMP}.sql.gz"

[[ ! -L "$BACKUP_DIR" ]] || { echo "Backup directory cannot be a symlink." >&2; exit 1; }
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
PARTIAL=$(mktemp "$BACKUP_DIR/.dump-XXXXXX")
trap 'rm -f -- "$PARTIAL"' EXIT
cd "$ROOT"

# Resolve credentials inside the actual database container, never from guessed
# host defaults. pipefail catches pg_dump failures even when gzip exits normally.
docker compose exec -T db sh -c \
  'exec pg_dump --clean --if-exists --no-owner --no-privileges -U "$POSTGRES_USER" "$POSTGRES_DB"' \
  | gzip -n > "$PARTIAL"
gzip -t -- "$PARTIAL"
mv -- "$PARTIAL" "$BACKUP_DIR/$FILENAME"
echo "Backup complete: $BACKUP_DIR/$FILENAME"

# Retention follows the existing 30-day policy, only after a successful backup.
find "$BACKUP_DIR" -maxdepth 1 -type f -name 'expense_tracker_*.sql.gz' -mtime +30 -delete
