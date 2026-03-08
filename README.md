# Atlas — Institutional Crypto Options Desk

Plateforme API + UI pour options crypto (BTC/ETH/SOL) avec:
- analytics quant (surface, calibration, signaux, arbitrage, optimizer Greeks),
- desk d'exécution en paper trading,
- moteur de risque pré-trade,
- marge (initial/maintenance),
- kill-switch,
- monitoring ops (metrics, alerts, health, stale/fallback market data).

## 1) Stack et périmètre

- Backend: `ASP.NET Core (.NET 8)`
- Frontend: `React + Vite + Recharts`
- Actifs supportés: `BTC`, `ETH`, `SOL`
- Données options: multi-source résiliente `Bybit + Deribit` (fallback + stale detection)
- Trading: **paper trading** (pas d'envoi d'ordres réel vers exchange)

## 2) Architecture réelle

```text
atlas/
├── Atlas.sln
├── src/
│   ├── Atlas.Core/                 # pricing models + greeks
│   │   ├── Models/
│   │   │   ├── BlackScholes.cs
│   │   │   ├── HestonModel.cs
│   │   │   ├── MonteCarlo.cs
│   │   │   ├── BinomialTree.cs
│   │   │   ├── SabrModel.cs
│   │   │   └── ImpliedVolSolver.cs
│   ├── Atlas.ToxicFlow/            # toxic flow engine
│   ├── Atlas.Exchange/             # exchange abstraction
│   └── Atlas.Api/
│       ├── Controllers/
│       │   ├── OptionsController.cs
│       │   ├── TradingController.cs
│       │   ├── PricingController.cs
│       │   ├── ToxicFlowController.cs
│       │   └── SystemController.cs
│       ├── Services/
│       │   ├── OptionsAnalyticsService.cs
│       │   ├── PaperTradingService.cs
│       │   ├── BybitOptionsMarketDataService.cs      # service résilient (Bybit+Deribit)
│       │   └── SystemMonitoringService.cs
│       └── Middleware/
│           └── RequestObservabilityMiddleware.cs
├── tests/Atlas.Tests/
└── frontend/
    ├── src/App.jsx
    └── src/components/
```

## 3) Démarrage local (fiable)

### Prérequis

- `.NET SDK 8.x`
- `Node.js 18+`
- `npm`

### Backend API

Depuis la racine repo:

```bash
dotnet run --project src/Atlas.Api
```

Backend par défaut: `http://127.0.0.1:5000`

Si tu es déjà dans `frontend/`:

```bash
dotnet run --project ../src/Atlas.Api --urls http://127.0.0.1:5000
```

ou via script:

```bash
cd frontend
npm run api
```

### Swagger

- `http://127.0.0.1:5000/swagger`

### Frontend

```bash
cd frontend
npm install
npm run dev
```

Frontend: `http://127.0.0.1:5173`

## 4) Variables d'environnement

### Frontend (`frontend/.env.example`)

- `VITE_API_BASE_URL`: base API explicite (ex: `http://127.0.0.1:5000` en local, URL publique API en Railway)
- `VITE_PROXY_TARGET`: cible proxy Vite pour `/api` en local (`npm run dev`)

Exemple local:

```bash
cd frontend
VITE_API_BASE_URL=http://127.0.0.1:5000 npm run dev
```

### API (`src/Atlas.Api/.env.example`)

- `ASPNETCORE_ENVIRONMENT`: `Production` ou `Development`
- `PORT`: port runtime (injecté automatiquement par Railway)
- `CORS_ALLOWED_ORIGINS`: liste d'origines autorisées séparées par virgules

## 5) Résolution des erreurs "Failed to fetch" / `ECONNREFUSED`

1. Démarrer d'abord l'API (`dotnet run ...`).
2. Vérifier que l'API écoute bien sur `127.0.0.1:5000`.
3. Depuis `frontend/`, utiliser `../src/Atlas.Api` (et non `src/Atlas.Api`).
4. Si port custom, aligner `VITE_API_BASE_URL` ou `VITE_PROXY_TARGET`.
5. En production, définir `VITE_API_BASE_URL` vers l'URL publique de l'API (pas de fallback localhost).

## 6) Fonctionnalités institutionnelles implémentées

### Exécution

- Slippage estimé et réalisé (`slippagePct`)
- Frais effectifs (`fees`, `effectiveFeeRate`)
- Score de qualité d'exécution (`executionQualityScore`)
- Fills détaillés par tentative (`fills[]`)
- `filledQuantity` / `remainingQuantity`

### State machine d'ordres

- États: `Received`, `Accepted`, `PartiallyFilled`, `Filled`, `Rejected`, `Cancelled`, `Expired`
- Idempotence via `clientOrderId`
- Anti-duplicate (fingerprint fenêtre courte)
- Retries configurables (`maxRetries`)
- Partial fills (`allowPartialFill`)
- Trace d'état (`stateTrace`)

### Risque pré-trade

Contrôles sur:
- notionnel ordre,
- taille ordre,
- limites Greeks portefeuille (`delta/gamma/vega/theta`),
- concentration,
- exposition par asset,
- perte journalière,
- max open orders,
- kill-switch.

### Marge et liquidation

- Initial margin + maintenance margin
- Equity, available margin, margin ratio
- Déclenchement liquidation explicite si maintenance cassée
- Notifications + activation kill-switch automatique lors d'une liquidation

### Market data résiliente

- Sources: Bybit + Deribit
- Fallback automatique
- Détection stale data
- Health détaillé par source/asset

### Monitoring / observabilité

- Middleware requêtes: latence, status HTTP, logs
- Metrics/gauges/counters
- Alerts actives
- Snapshot ops consolidé
- Header `X-Trace-Id`

### Validation quant

- Tests unitaires pricing et Greeks
- Tests de non-régression modèles (BS/Heston/SABR)
- Cohérence Greeks vs finite differences

## 7) Référence API complète

### Pricing (`/api/pricing`)

- `GET /api/pricing/compare`
- `GET /api/pricing/greeks`
- `GET /api/pricing/implied-vol`

### Toxic Flow (`/api/toxicflow`)

- `GET /api/toxicflow/dashboard`
- `GET /api/toxicflow/clusters`
- `GET /api/toxicflow/counterparties`
- `GET /api/toxicflow/alerts`
- `GET /api/toxicflow/counterparty/{id}`
- `GET /api/toxicflow/history`

### Options (`/api/options`)

- `GET /api/options/assets?assets=BTC,ETH,SOL`
- `GET /api/options/expiries?asset=BTC`
- `GET /api/options/chain?asset=BTC&expiry=YYYY-MM-DD&type=all&limit=220`
- `GET /api/options/surface?asset=BTC&limit=600`
- `GET /api/options/models?symbol=...`
- `GET /api/options/calibration?asset=BTC&expiry=YYYY-MM-DD`
- `GET /api/options/signals?asset=BTC&expiry=YYYY-MM-DD&type=all&limit=140`
- `GET /api/options/regime?asset=BTC`
- `GET /api/options/recommendations?asset=BTC&expiry=YYYY-MM-DD&size=1&riskProfile=balanced`
- `GET /api/options/optimizer?asset=BTC&expiry=YYYY-MM-DD&size=1&riskProfile=balanced&targetDelta=0&targetVega=0&targetTheta=0`
- `GET /api/options/exposure-grid?asset=BTC&expiry=YYYY-MM-DD&maxExpiries=6&maxStrikes=24`
- `GET /api/options/arbitrage?asset=BTC&expiry=YYYY-MM-DD&limit=120`
- `GET /api/options/strategies/presets?asset=BTC&expiry=YYYY-MM-DD&size=1`
- `POST /api/options/strategies/analyze`
- `GET /api/options/stream?asset=BTC&expiry=YYYY-MM-DD&chainLimit=80` (SSE)

### Trading (`/api/trading`)

- `GET /api/trading/limits`
- `GET /api/trading/orders?limit=200`
- `GET /api/trading/notifications?limit=120`
- `POST /api/trading/orders/retry?maxOrders=25`
- `GET /api/trading/killswitch`
- `POST /api/trading/killswitch`
- `GET /api/trading/positions`
- `GET /api/trading/risk`
- `GET /api/trading/book?orderLimit=150`
- `POST /api/trading/orders`
- `POST /api/trading/preview`
- `POST /api/trading/stress`
- `POST /api/trading/reset`

### System / Ops (`/api/system`)

- `GET /api/system/health`
- `GET /api/system/metrics`
- `GET /api/system/alerts`
- `GET /api/system/market-data`
- `GET /api/system/ops`
- `GET /health` (health simplifié)

## 8) Payloads utiles

### `POST /api/trading/orders`

```json
{
  "symbol": "ETH-10MAR26-1700-C",
  "side": "Buy",
  "quantity": 2,
  "type": "Market",
  "limitPrice": null,
  "clientOrderId": "ORDER-001",
  "maxRetries": 3,
  "allowPartialFill": true,
  "maxSlippagePct": 0.08
}
```

### `POST /api/trading/preview`

Même payload que `orders`, retourne:
- accept/reject,
- projection risque,
- marge projetée,
- slippage/quality estimés,
- quantité estimée exécutée.

### `POST /api/trading/killswitch`

```json
{
  "isActive": true,
  "reason": "manual-risk-lock",
  "updatedBy": "desk"
}
```

## 9) Exemples `curl`

```bash
# Health ops
curl -s http://127.0.0.1:5000/api/system/ops

# Option chain ETH
curl -s "http://127.0.0.1:5000/api/options/chain?asset=ETH&limit=50"

# Préview ordre
curl -s -X POST http://127.0.0.1:5000/api/trading/preview \
  -H 'Content-Type: application/json' \
  -d '{"symbol":"ETH-10MAR26-1700-C","side":"Buy","quantity":1,"type":"Market"}'

# Kill-switch ON
curl -s -X POST http://127.0.0.1:5000/api/trading/killswitch \
  -H 'Content-Type: application/json' \
  -d '{"isActive":true,"reason":"manual lock","updatedBy":"ops"}'
```

## 10) Tests et qualité

```bash
# Backend build
dotnet build Atlas.sln -c Release

# Tests
dotnet test Atlas.sln

# Frontend build
cd frontend
npm run build
```

## 11) Limites actuelles

- Trading en simulation (`paper`) uniquement.
- Pas de persistance DB long terme (in-memory).
- Pas d'authentification/autorisation multi-utilisateurs pour le moment.

## 12) Passage vers prod réelle

1. Ajouter authn/authz + gestion des secrets.
2. Brancher execution venue réelle (connecteurs exchange).
3. Ajouter stockage persistant (orders/fills/positions/alerts/metrics).
4. Déployer observabilité externe (Prometheus/Grafana/OTel collector).
5. Ajouter runbooks d'incident + on-call 24/7.

## 13) Déploiement Railway (sans VPS)

Le repo est prêt pour un déploiement Railway en **2 services**:
- Service API avec root directory `src/Atlas.Api` et config `[railway.json](src/Atlas.Api/railway.json)`.
- Service Frontend avec root directory `frontend` et config `[railway.json](frontend/railway.json)`.

### Étapes Railway

1. Connecter le repo GitHub dans Railway.
2. Créer le service API avec root directory `src/Atlas.Api`.
3. Déployer l'API puis récupérer son URL publique (`https://...up.railway.app`).
4. Créer le service Frontend avec root directory `frontend`.
5. Définir `VITE_API_BASE_URL=https://<api-url>` dans le service Frontend.
6. Définir `CORS_ALLOWED_ORIGINS=https://<frontend-url>` dans le service API.
7. Redéployer les deux services.

### Vérifications

```bash
curl -s https://<api-url>/health
curl -s https://<api-url>/api/system/health
```

- UI: `https://<frontend-url>`
- Swagger: `https://<api-url>/swagger`

### Local inchangé

- API local: `dotnet run --project src/Atlas.Api`
- Front local: `cd frontend && npm run dev`
- Le fallback `127.0.0.1` est actif uniquement en développement.

## 14) Licence

Proprietary. All rights reserved.
