#!/bin/bash
# dev.sh — Local development startup script (NOT for production)
# For production deployment see deploy.md
set -e

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

ROOT="$(cd "$(dirname "$0")" && pwd)"

echo "▶ Pornesc Backend (http://localhost:5000)..."
cd "$ROOT/ExpenseTracker.Api"
dotnet run --urls "http://localhost:5000" &
BACKEND_PID=$!

echo "▶ Pornesc Frontend (http://localhost:5173)..."
cd "$ROOT/expense-tracker-ui"
npm run dev &
FRONTEND_PID=$!

echo ""
echo "✓ Backend  → http://localhost:5000"
echo "✓ Frontend → http://localhost:5173"
echo "✓ API docs → http://localhost:5000/swagger"
echo ""
echo "Apasă Ctrl+C pentru a opri ambele servere."

trap "echo 'Opresc serverele...'; kill $BACKEND_PID $FRONTEND_PID 2>/dev/null" EXIT INT TERM
wait
