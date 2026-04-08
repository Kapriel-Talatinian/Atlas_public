# Atlas War-Machine Backlog

This backlog is ordered by real ROI, not by visual appeal.

## Priority Legend

- `P0`: required before serious live deployment
- `P1`: strong edge / strong operational value
- `P2`: powerful but depends on earlier foundations
- `P3`: polish or later research track

## P0 - Production Hardening

### WMG-001 - Introduce runtime roles

Priority: `P0`

Goal:

- split `api` and `bot-worker` responsibilities

Files:

- [`Program.cs`](/Users/kapriel.talatinian/Desktop/atlas/src/Atlas.Api/Program.cs)
- new `RuntimeRoleOptions` binding

Acceptance criteria:

- API role serves HTTP and never runs trading loop
- worker role runs trading loop and can serve minimal health endpoints
- local dev `all` mode still works

### WMG-002 - Postgres bot state repository

Priority: `P0`

Goal:

- replace `shared-portfolio.json` as source of truth

Files:

- new `IBotStateRepository.cs`
- new `PostgresBotStateRepository.cs`
- new bot runtime DTOs

Acceptance criteria:

- runtime state loads from Postgres
- cycle writes are transactional
- restart restores state cleanly

### WMG-003 - Leader election with fencing

Priority: `P0`

Goal:

- prevent duplicate bot cycles across replicas

Files:

- new `IBotLeaderElectionService.cs`
- new `PostgresBotLeaderElectionService.cs`

Acceptance criteria:

- first worker acquires lease
- second worker cannot run while lease valid
- stale worker cannot persist after losing lease

### WMG-004 - Replace generic hosted bot loop

Priority: `P0`

Goal:

- make bot loop explicit and replica-safe

Files:

- new `ExperimentalBotWorkerService.cs`
- remove legacy always-on hosted loop and route all bot execution through the worker service

Acceptance criteria:

- worker logs leadership transitions
- worker skips cycles if not leader
- worker recovers portfolio on startup

### WMG-005 - Runtime health and recovery endpoints

Priority: `P0`

Goal:

- give operators direct visibility into bot authority and state

Files:

- new runtime controller
- DTOs for health / leader / recovery

Acceptance criteria:

- endpoint returns current instance id, role, lease status, last cycle info
- recovery endpoint can rebuild state from persisted data without trading

## P1 - Market Intelligence

### WMG-010 - Dealer Positioning Engine

Priority: `P1`

Goal:

- estimate gamma walls, vanna walls, charm pressure, pin zones

Outputs:

- `gammaWallLevel`
- `vannaWallLevel`
- `pinScoreByStrike`
- `hedgePressureScore`

Acceptance criteria:

- endpoint returns ranked positioning zones
- bot ranking uses positioning penalty / boost
- audit trail records positioning rationale

### WMG-011 - Event Vol Engine

Priority: `P1`

Goal:

- model event premium and crush risk

Outputs:

- event premium score
- pre-event vs post-event expected move
- recommended structure class

Acceptance criteria:

- event risk enters candidate ranking
- bot can reject bad timing around event premium

### WMG-012 - Cross-asset capital allocation overlay

Priority: `P1`

Goal:

- allocate risk between BTC / ETH / SOL using marginal edge per unit risk

Acceptance criteria:

- one asset cannot dominate without stronger edge
- allocation reacts to drawdown and concentration

## P1 - Trade Lifecycle Management

### WMG-020 - Dynamic hedging engine

Priority: `P1`

Goal:

- manage structures after entry

Capabilities:

- delta hedge policy
- gamma scalp policy
- vega reduction rule
- expiry roll rule

Acceptance criteria:

- each open trade has hedge policy
- hedge actions are audited and attributed separately

### WMG-021 - Exit engine upgrade

Priority: `P1`

Goal:

- move beyond stop/target/time only

Add:

- neural reversal exit
- regime deterioration exit
- dealer pressure exit
- event timing exit
- EV decay exit

Acceptance criteria:

- closed trade explains exact exit path
- audits distinguish signal error vs timing error vs hedge error

### WMG-022 - TCA / execution quality analytics

Priority: `P1`

Goal:

- measure whether theoretical edge survives execution

Metrics:

- arrival price
- slippage vs expected
- implementation shortfall
- adverse selection

Acceptance criteria:

- every trade stores execution quality metrics
- bad execution can penalize future setup ranking

## P2 - Portfolio Construction

### WMG-030 - Portfolio optimizer

Priority: `P2`

Goal:

- choose the best book, not only the best isolated trade

Constraints:

- capital
- margin
- delta/gamma/vega/theta
- concentration
- correlation

Acceptance criteria:

- optimizer can recommend add / reduce / replace
- bot uses marginal contribution to decide new trades

### WMG-031 - Capital efficiency scoring

Priority: `P2`

Goal:

- score structures by expected return per unit margin and drawdown

Acceptance criteria:

- candidate ranking includes margin efficiency
- same edge with lower capital usage ranks higher

## P2 - Replay And Learning

### WMG-040 - Replay engine

Priority: `P2`

Goal:

- replay cycles and compare versions safely

Acceptance criteria:

- one historical day can be replayed deterministically
- outputs include trade diffs and equity diffs by version

### WMG-041 - Post-trade forensics

Priority: `P2`

Goal:

- explain why a trade worked or failed

Breakdown:

- signal quality
- execution quality
- timing quality
- hedge quality
- regime fit

Acceptance criteria:

- audit trail categorizes each losing trade by dominant failure class

### WMG-042 - Champion / challenger model lane

Priority: `P2`

Goal:

- evaluate alternative brains without risking state corruption

Acceptance criteria:

- challenger runs read-only
- production book remains owned by champion
- comparison report available by day / week / month

## P3 - Later Research / Frontend Reset

### WMG-050 - Full institutional frontend redesign

Priority: `P3`

Goal:

- redesign around book commander, RV, dealer positioning, event vol, hedge console

Note:

- do after runtime and intelligence layers are stable

### WMG-051 - Training pipeline for deeper ML

Priority: `P3`

Goal:

- dataset extraction, offline training, registry, shadow deployment

### WMG-052 - Real exchange execution

Priority: `P3`

Goal:

- move from paper runtime to safe staged real execution architecture

## What To Remove Or Keep Simplified

These should stay out or remain minimal until the engine is stronger:

- user-exposed bot hyperparameters in UI
- decorative metrics without trade impact
- strategies with poor liquidity / poor executability
- duplicate analytics panels
- local filesystem source of truth for bot state

## Recommended Next Build Order

1. `WMG-001`
2. `WMG-002`
3. `WMG-003`
4. `WMG-004`
5. `WMG-005`
6. `WMG-010`
7. `WMG-011`
8. `WMG-020`
9. `WMG-021`
10. `WMG-030`
11. `WMG-040`
12. `WMG-042`

That order gives the highest ratio of safety, edge, and compounding value.
