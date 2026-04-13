# Atlas — VPS deployment

Everything (bot, API, Telegram mini app, Postgres, reverse proxy) runs from
a single `docker compose up -d` on a small Linux VPS.

## 1. Pick a VPS

| | Minimum | Recommended |
|---|---|---|
| OS | Ubuntu 22.04 LTS | **Ubuntu 24.04 LTS** |
| RAM | 2 GB | **4 GB** |
| CPU | 1 vCPU | 2 vCPU |
| Disk | 20 GB SSD | 40 GB SSD |

Good providers:
- **Hetzner** `CX22`: 2 vCPU / 4 GB / 40 GB — ~€4.50/month (best value)
- **OVH** VPS Starter — ~€5/month
- **DigitalOcean** 4 GB droplet — $24/month

A datacenter in Europe (Falkenstein, Gravelines, Frankfurt) gives low latency
to Telegram and Polymarket endpoints.

## 2. Open DNS (recommended)

The Telegram mini app requires HTTPS on mobile. Point a domain (or a subdomain
like `atlas.yourdomain.com`) to your VPS public IP via an `A` record.

Without a domain you can still run the bot, but the Telegram Mini Web App
won't load on mobile devices.

## 3. Install Docker

```bash
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
# log out and back in so the group takes effect
```

## 4. Clone the repo

```bash
git clone https://github.com/Kapriel-Talatinian/Atlas_public.git atlas
cd atlas
```

## 5. Configure

```bash
cp deploy/.env.example .env
nano .env
```

Fill in at minimum:
- `POSTGRES_PASSWORD` — any long random string (use `openssl rand -hex 24`)
- `DOMAIN` — e.g. `atlas.yourdomain.com` (leave empty for IP-only HTTP)
- `ATLAS_DASHBOARD_URL` — e.g. `https://atlas.yourdomain.com`
- `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` — from @BotFather and @userinfobot

Leave the Polymarket CLOB keys empty for paper trading. See Section 8 for live.

## 6. Start everything

```bash
docker compose up -d --build
```

First run builds the .NET images and pulls Postgres — takes 3-5 minutes.
Subsequent starts are a few seconds.

Check it's alive:

```bash
docker compose ps
docker compose logs -f bot-worker
```

You should see `Polymarket live worker starting…` in the bot-worker logs and
within a minute or two, `Telegram command worker accepted command /...` when
you send `/menu` on Telegram.

## 7. Test the mini app

Open in a browser:

- With domain: `https://atlas.yourdomain.com`
- Without: `http://YOUR.VPS.IP`

Then on Telegram:
1. Send `/menu` to your bot — the reply has an **Open Dashboard** button.
2. In @BotFather: `/mybots → your bot → Bot Settings → Menu Button → Configure Menu Button`, paste `https://atlas.yourdomain.com` to get a permanent Telegram menu button.

## 8. Going live (real money)

Stay in paper mode for at least a few days and confirm win rate + profit
factor look healthy. Then:

1. Deposit USDC on your Polygon wallet.
2. Approve USDC spending on the Polymarket CTF Exchange contract
   `0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E` (one MetaMask transaction).
3. Create CLOB API keys on https://polymarket.com/settings.
4. In `.env`:
   ```
   POLYMARKET_EXECUTION_MODE=live
   POLYMARKET_TRADING_ENABLED=true
   POLYMARKET_API_KEY=...
   POLYMARKET_API_SECRET=...
   POLYMARKET_API_PASSPHRASE=...
   POLYMARKET_PRIVATE_KEY=0x...       # your Polygon wallet private key
   POLYMARKET_WALLET_ADDRESS=0x...
   POLYMARKET_SIGNER_ADDRESS=0x...
   POLYMARKET_MAX_TRADE_USD=5         # Polymarket minimum order size
   ```
5. `docker compose up -d` to apply.
6. Watch `docker compose logs -f bot-worker` for `CLOB order placed:` lines.

## 9. Daily operations

```bash
# Tail bot logs
docker compose logs -f bot-worker

# Tail everything
docker compose logs -f

# Stop
docker compose stop

# Pull latest code + restart
git pull
docker compose up -d --build

# Backup Postgres
docker compose exec postgres pg_dump -U atlas atlas | gzip > backup-$(date +%F).sql.gz
```

## 10. Troubleshooting

**`bot-worker` keeps restarting**
`docker compose logs bot-worker` will show the error. Common: bad
`POSTGRES_PASSWORD`, missing `TELEGRAM_BOT_TOKEN`.

**Telegram mini app shows "Connection issue"**
The frontend can't reach `/api/...`. Check `docker compose logs caddy`. If
`DOMAIN` is set but DNS isn't propagated yet, Caddy won't get a cert — wait
a few minutes or check DNS at `https://dnschecker.org`.

**CLOB orders rejected**
Check the bot-worker logs for the exact error. Likely causes:
- No USDC in wallet
- No allowance on CTF Exchange — do the approve transaction in MetaMask
- Order below the $5 minimum — bump `POLYMARKET_MAX_TRADE_USD`

**Free up disk**
```bash
docker system prune -a --volumes
```
(Careful: this removes everything including the `postgres-data` volume if
nothing else references it. Back up first.)
