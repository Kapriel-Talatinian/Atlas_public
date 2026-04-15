# Railway → VPS migration

Move the bot from Railway to a self-hosted VPS without losing state or
getting 409 Conflict errors on the Telegram bot token.

**Why a 409 matters**: Telegram only allows one consumer of `getUpdates` at a
time. If the Railway bot-worker and the VPS bot-worker poll the same bot
token simultaneously, they kick each other off every second and nothing
responds. Do the cutover in order.

---

## Before you start

Collect these values from Railway (Variables tab on the `bot-worker` service):

- `TELEGRAM_BOT_TOKEN`
- `TELEGRAM_CHAT_ID`
- `POLYMARKET_API_KEY`, `POLYMARKET_API_SECRET`, `POLYMARKET_API_PASSPHRASE`
  (if you intend to trade live)
- `POLYMARKET_PRIVATE_KEY` (if live)
- `POLYMARKET_WALLET_ADDRESS`, `POLYMARKET_SIGNER_ADDRESS` (if live)

Keep them in a password manager or a scratch file you will delete after.

---

## Cutover in 5 steps

### 1. Provision the VPS and install

```bash
# on the VPS
curl -fsSL https://raw.githubusercontent.com/Kapriel-Talatinian/Atlas_public/main/deploy/install-minimal.sh | bash
# log out, log back in so the docker group takes effect
curl -fsSL https://raw.githubusercontent.com/Kapriel-Talatinian/Atlas_public/main/deploy/install-minimal.sh | bash
```

This clones the repo and generates `~/atlas/.env` from the minimal template.

### 2. Fill the `.env` on the VPS

```bash
cd ~/atlas
nano .env
```

Paste the values you collected from Railway. You do **not** need to fill the
CLOB keys if you only want paper mode to keep running.

### 3. Stop the Railway bot-worker BEFORE starting the VPS

This is the most important step. Open Railway, go to the `bot-worker` service,
click **Settings → Service → Remove Service**. Or at least **Pause** it.

(You can keep the Postgres running if you want to preserve history. The VPS
minimal deploy does not use it — it keeps its own file-based state — so the
Railway history becomes a read-only archive you can consult from the Railway
console if ever needed.)

### 4. Start the VPS stack

```bash
cd ~/atlas
docker compose -f docker-compose.minimal.yml up -d --build
docker compose -f docker-compose.minimal.yml logs -f
```

Watch the logs. You should see:

- `Polymarket live worker starting…`
- `Telegram command worker starting on instance … chatId=…`
- `Telegram command worker accepted command /…` when you send `/menu`

### 5. Verify on Telegram

Send `/ping` — you should get a `PONG` response. Send `/menu` — you should
see the control center.

---

## Paper history is not migrated

The VPS starts with a fresh file-based state. The 80 paper trades from Railway
stay in the Railway Postgres but are no longer what Telegram / the bot reads.
If you switch to live right after, this is actually the correct behaviour —
you are starting with a new real balance, not a simulated one.

---

## Going live on the VPS

Once the bot is running fine in paper on the VPS:

1. Deposit USDC on your Polygon wallet.
2. Approve USDC spending on the CTF Exchange contract
   `0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E` in MetaMask
   (one transaction, ~$0.01 in MATIC).
3. Edit `.env`:
   ```
   POLYMARKET_EXECUTION_MODE=live
   POLYMARKET_TRADING_ENABLED=true
   POLYMARKET_MAX_TRADE_USD=5        # Polymarket minimum order
   POLYMARKET_STARTING_BALANCE_USD=<your real USDC balance>
   # and the 6 key/address fields
   ```
4. Apply:
   ```
   docker compose -f docker-compose.minimal.yml up -d
   ```

Watch `docker compose -f docker-compose.minimal.yml logs -f` for
`CLOB order placed:` — that is a real on-chain order.

Use `/pause` on Telegram any time to stop new entries. `/resume` unblocks.

---

## Cleanup on Railway

Once the VPS has been running stable for a day or two and you no longer need
Railway as a fallback, delete all Railway services to stop the billing:

- `bot-worker` → Remove
- `Atlas_public` → Remove
- `atlas-frontend` / `atlas-tg` → Remove
- `Postgres` → Remove (this deletes the historic paper trades — export first
  if you want to keep them)
