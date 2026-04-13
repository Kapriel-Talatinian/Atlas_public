#!/usr/bin/env bash
# Atlas VPS minimal bootstrap — just the bot + Telegram monitoring.
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Kapriel-Talatinian/Atlas_public/main/deploy/install-minimal.sh | bash
set -euo pipefail

REPO_URL="${ATLAS_REPO:-https://github.com/Kapriel-Talatinian/Atlas_public.git}"
INSTALL_DIR="${ATLAS_DIR:-$HOME/atlas}"

echo ">>> Installing Docker (if missing)..."
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh
  sudo usermod -aG docker "$USER" || true
  echo ">>> Docker installed. Log out and back in, then re-run this script."
  exit 0
fi

if [ ! -d "$INSTALL_DIR/.git" ]; then
  echo ">>> Cloning Atlas into $INSTALL_DIR"
  git clone "$REPO_URL" "$INSTALL_DIR"
else
  echo ">>> $INSTALL_DIR exists, pulling latest"
  git -C "$INSTALL_DIR" pull --ff-only
fi

cd "$INSTALL_DIR"

if [ ! -f .env ]; then
  cp deploy/.env.minimal.example .env
  echo ""
  echo ">>> .env created."
  echo ">>> Edit it now to set TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID:"
  echo ">>>     nano .env"
  echo ">>> Then run:"
  echo ">>>     docker compose -f docker-compose.minimal.yml up -d --build"
  exit 0
fi

echo ">>> Starting bot..."
docker compose -f docker-compose.minimal.yml up -d --build

echo ""
echo ">>> Done. Monitor with:"
echo ">>>     docker compose -f docker-compose.minimal.yml logs -f"
