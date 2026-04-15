#!/usr/bin/env bash
# Atlas VPS minimal bootstrap — bot + Telegram monitoring, nothing else.
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Kapriel-Talatinian/Atlas_public/main/deploy/install-minimal.sh | bash
set -euo pipefail

REPO_URL="${ATLAS_REPO:-https://github.com/Kapriel-Talatinian/Atlas_public.git}"
INSTALL_DIR="${ATLAS_DIR:-$HOME/atlas}"

echo ""
echo "========================================"
echo " Atlas minimal VPS setup"
echo "========================================"
echo ""

# 1. Docker
if ! command -v docker >/dev/null 2>&1; then
  echo ">>> Installing Docker..."
  curl -fsSL https://get.docker.com | sh
  sudo usermod -aG docker "$USER" || true
  echo ""
  echo "----------------------------------------"
  echo " Docker installed."
  echo " LOG OUT and LOG BACK IN, then re-run:"
  echo "   curl -fsSL https://raw.githubusercontent.com/Kapriel-Talatinian/Atlas_public/main/deploy/install-minimal.sh | bash"
  echo "----------------------------------------"
  exit 0
fi

# 2. Clone / update repo
if [ ! -d "$INSTALL_DIR/.git" ]; then
  echo ">>> Cloning Atlas into $INSTALL_DIR"
  git clone "$REPO_URL" "$INSTALL_DIR"
else
  echo ">>> $INSTALL_DIR exists, pulling latest"
  git -C "$INSTALL_DIR" pull --ff-only
fi

cd "$INSTALL_DIR"

# 3. Prepare .env
if [ ! -f .env ]; then
  cp deploy/.env.minimal.example .env
  echo ""
  echo "========================================"
  echo " .env created at: $INSTALL_DIR/.env"
  echo "========================================"
  echo ""
  echo " Edit it now and fill at minimum:"
  echo "   - TELEGRAM_BOT_TOKEN"
  echo "   - TELEGRAM_CHAT_ID"
  echo ""
  echo " Commands to run next:"
  echo "   cd $INSTALL_DIR"
  echo "   nano .env"
  echo "   docker compose -f docker-compose.minimal.yml up -d --build"
  echo "   docker compose -f docker-compose.minimal.yml logs -f"
  echo ""
  echo " For live trading, see deploy/MIGRATION.md"
  echo "========================================"
  exit 0
fi

# 4. Start
echo ">>> Building and starting bot..."
docker compose -f docker-compose.minimal.yml up -d --build

echo ""
echo "========================================"
echo " Done."
echo "========================================"
echo ""
echo " Check the bot is healthy:"
echo "   docker compose -f docker-compose.minimal.yml logs -f"
echo ""
echo " On Telegram, send /ping to verify."
echo " Send /menu to see all commands."
echo "========================================"
