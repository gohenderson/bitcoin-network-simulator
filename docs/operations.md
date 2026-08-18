# Persistence, watching, and project layout

See the main [README](../README.md) for a quick overview.

## Persistence & resume

Every run gets its own directory:

```
ScenarioResults/<timestamp>-<scenario name, or "no-scenario">/
  <scenario file>            (copied in, if one was used)
  watcher.db                 (SQLite: network convergence/recovery history — see Watching a run)
  nodes/
    <node-id>/
      blockchain.db           (SQLite: that node's chain — see below)
      metadata.json            (that node's role, hash power, mining/pool config, signing key)
```

On startup, a node resumes from its saved `blockchain.db` if it's
structurally valid and shares this build's genesis; otherwise it starts fresh.
`metadata.json` is loaded if present (so a hand-edited `HashPower`, `NodeRole`,
`CanMine`, `Pool`, or `RuleSchedule` value survives a restart) and is safe to
hand-edit — a changed `RuleSchedule` only ever affects what this node
builds AND validates from the moment it's loaded on, never existing
history: each already-mined block keeps whatever it recorded in its own
`Rules` at the time (informational only — see [What this is
not](mechanics.md#what-this-is-not) — persisted right alongside it, see the `blocks`
table below), and `TryLoadFrom`'s resume-time validation checks the
resumed chain against the *freshly-loaded* `RuleSchedule`, so a resumed
chain that was built under the old schedule but no longer matches the
new one fails to load — except `SigningKey`, which must never change once
a node has mined blocks, or its historical blocks can no longer be
verified.

Each node's `blockchain.db` (`BlockchainStore`, in `../Blockchain.cs`) holds its
local chain across two tables:

| Table | Contents |
|---|---|
| `blocks` | One row per block, keyed by height (`idx`): timestamp, previous/own hash, builder, signature, target, nonce, and that block's own recorded (informational-only) `Rules` (retarget cadence, halving schedule, max supply, ...) — see [Scenarios](scenarios.md) and [What this is not](mechanics.md#what-this-is-not). |
| `transactions` | One row per transaction, foreign-keyed to `blocks`, with a `position` column preserving in-block order. |

`PersistenceLoop.RunAsync` syncs each node's in-memory chain to its database
every 3 seconds. It only ever writes the records that actually need to
change: in the common case (blocks only ever appended) that's just the new
tail; on a reorg, it finds the height where the in-memory chain first
diverges from what's on disk, trims the persisted records from there
onward, and reinserts the replacement blocks — everything below the
divergence point is untouched, since a block never changes once mined.

## Watching a run

`ChainWatcher` polls every node's `/<node-id>/chain` every 2 seconds, independently
validates each one, and reports whether the network has converged on a single
valid tip (`Healthy`), is still settling (`Recovering`), or has a node in an
invalid state (`InvalidState`). Every observation is written directly, as it
happens, to `watcher.db` — a SQLite database in the run's result folder
(`WatcherStore`, in `../Watcher.cs`):

| Table | Contents |
|---|---|
| `run_info` | One row identifying the run: start time, port, scenario path/description. |
| `events` | The append-only event log — block-built, block-accepted, block-rejected, reorganization, and network-state-transition events — with fields like `role`, `built_by`, `nonce`, and `tx_count` broken out into real columns. |
| `audits` | One row per periodic convergence audit: state, convergence/validity flags, height range, blocks observed. |
| `audit_nodes` | Each audited node's per-audit height/tip/validity, foreign-keyed to `audits`. |

It can be queried directly (e.g. with the `sqlite3` CLI or any SQLite
library) while a run is still in progress, and is the basis for
reconstructing reports or charting a run's progression over time.

**Web dashboard.** `http://localhost:5000/dashboard/` (`../Dashboard.cs`) is a
self-contained, dependency-free HTML page — served directly by the same
`NetworkServer` every node shares, off a reserved `dashboard` path segment
that a real node id (always `NodeNetwork.NodeNameFor`'s zero-padded-index +
Greek-letter shape, e.g. `000-alpha`) can never collide with — that polls
`http://localhost:5000/dashboard/summary` (JSON) every 2 seconds and renders:
participant/mining/wallet-only/pool counts, chain height and convergence
state, top miners ranked by hash-power share and by blocks actually won
(`WatcherStore.GetWinCountsByNode`, tallied from `watcher.db`'s `events`
table), the most-connected nodes by peer count (`NodeNetwork.GetSnapshot`'s
`PeerCount` — the same structural-hub dynamic described under [Peer
topology](mechanics.md)), pool composition, and a full sortable-by-hash-power
node table. Open it in a browser while a run is in progress; no separate
process or build step is needed.

## Project layout

| File | Responsibility |
|---|---|
| `Program.cs` | Entry point / composition root: reads the scenario's phases and walks them in order, builds a `NodeNetwork`, and starts the mining scheduler, transaction generator, growth/churn loops, watcher, and persistence loops as async tasks. |
| `NodeNetwork.cs` | The live network: the node/miner registry, the peer graph (weighted outbound peer selection — see [Peer topology](mechanics.md)), node naming and default role/mining-participation policy, node creation (`AddNodeAsync`), organic growth (`GrowthLoopAsync`), and node churn/departure (`ChurnLoopAsync`/`RemoveNode`). |
| `MiningScheduler.cs` | Round-robin turn scheduling across whatever `IMiner`s currently exist — solo or pooled, reshuffled whenever a new block appears. |
| `TransactionGenerator.cs` | Synthetic transaction traffic: picks a real sender/recipient pair from live balances each round and submits a transaction. |
| `PersistenceLoop.cs` | Per-node persistence: resumes a node's chain from its `blockchain.db` at startup, then periodically syncs it back for the rest of the run. |
| `Blockchain.cs` | The blockchain data model: `Transaction`, `Block`, `ConsensusRules` (one proof-of-work/economics ruleset), `RuleSchedule` (a node's own timeline of which `ConsensusRules` is active at which height — what `ValidateChain` actually checks incoming blocks against), `ProofOfWork`, `Economics`, `Ledger`, and `Blockchain` itself (validation and fork-choice logic). Also defines `BlockchainStore` — SQLite persistence for one node's local chain (`blockchain.db`). |
| `NetworkServer.cs` | The single shared HTTP listener; routes each request by node id to that node's handler, or to `Dashboard.cs` under the reserved `dashboard` path segment. |
| `Node.cs` | Per-node request handling: `/<node-id>/chain`, `/<node-id>/tx`, `/<node-id>/receiveBlock`, `/<node-id>/receiveChain`, etc., including relaying an accepted block/chain on to this node's own other peers. Also defines `NodeRole`, `NodeIdentityRegistry` (process-wide table binding node Ids to the public keys they sign blocks with), and `NodeMetadata`/`NodeMetadataStore` (a node's persisted config — role, hash power, economic weight, consensus rules, signing key — and its `metadata.json` load/save/apply logic). |
| `Miner.cs` | `SoloMiner` — nonce search, block assembly, broadcast, and a node's signing identity. Also defines `PoolMiner` (a named group of `SoloMiner`s mining as one combined turn, with proportional reward splitting) and `IMiner` (the common interface the round-robin scheduler rotates over). |
| `Watcher.cs` | `ChainWatcher` — periodic cross-network convergence/validity auditing. Also defines `WatcherStore` — SQLite persistence for the watcher's events and audits (`watcher.db`). |
| `Dashboard.cs` | The web dashboard — see [Watching a run](#watching-a-run): builds the JSON summary from `NodeNetwork.GetSnapshot` and `WatcherStore.GetWinCountsByNode`, and serves the page that polls and renders it. |
| `Scenario.cs` | Scenario file format and loader; also computes each run's `ScenarioResults/` result directory. |
| `ElasticTaskPool.cs` | `ElasticTaskPool` — a bounded, load-scaling async worker pool; backs `NetworkServer`'s request handling. |
