#!/bin/bash
# Restore a trusted SQL backup in one transaction; SQL errors roll it all back.
set -euo pipefail
umask 077

if [[ -z "${1:-}" ]]; then
  echo "Usage: $0 <backup-file.sql.gz>" >&2
  exit 1
fi
BACKUP_FILE=$(realpath -e -- "$1")
[[ -f "$BACKUP_FILE" ]] || { echo "Backup must be a regular file." >&2; exit 1; }
gzip -t -- "$BACKUP_FILE"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
cd "$ROOT"

echo "Restoring from $BACKUP_FILE"
echo "WARNING: A current backup drops and recreates its tables in the configured database."
echo "Use only a trusted backup. Any SQL error aborts the complete transaction."
read -rp "Continue? [y/N] " confirm
[[ "$confirm" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 0; }

gunzip -c -- "$BACKUP_FILE" | docker compose exec -T db sh -c \
  'exec psql --single-transaction --set ON_ERROR_STOP=on -U "$POSTGRES_USER" "$POSTGRES_DB"'
echo "Restore complete."
