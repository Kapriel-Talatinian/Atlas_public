#!/usr/bin/env bash
# Atlas VPS bootstrap — run this once on a fresh Ubuntu 22.04/24.04 VPS.
# Usage:  curl -fsSL https://raw.githubusercontent.com/Kapriel-Talatinian/Atlas_public/main/deploy/install.sh | bash
set -euo pipefail

REPO_URL="${ATLAS_REPO:-https://github.com/Kapriel-Talatinian/Atlas_public.git}"
INSTALL_DIR="${ATLAS_DIR:-$HOME/atlas}"

echo ">>> Installing Docker (if missing)…"
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh
  sudo usermod -aG docker "$USER" || true
  echo ">>> Docker installed. Log out and log back in so the 'docker' group takes effect, then re-run this script."
  exit 0
fi

if [ ! -d "$INSTALL_DIR/.git" ]; then
  echo ">>> Cloning Atlas into $INSTALL_DIR"
  git clone "$REPO_URL" "$INSTALL_DIR"
else
  echo ">>> $INSTALL_DIR already exists, pulling latest"
  git -C "$INSTALL_DIR" pull --ff-only
fi

cd "$INSTALL_DIR"

if [ ! -f .env ]; then
  cp deploy/.env.example .env
  sed -i "s/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=$(openssl rand -hex 24)/" .env
  echo ">>> .env created with a random POSTGRES_PASSWORD."
  echo ">>> Edit .env to set TELEGRAM_BOT_TOKEN, TELEGRAM_CHAT_ID, DOMAIN, ATLAS_DASHBOARD_URL."
  echo ">>> Then run:  docker compose up -d --build"
  exit 0
fi

echo ">>> Starting Atlas stack (this may take a few minutes on first run)…"
docker compose up -d --build

echo ">>> Done. Tail bot logs with:  docker compose logs -f bot-worker"
