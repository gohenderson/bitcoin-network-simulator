# Bitcoin Network Simulator

A single-process simulation of a Bitcoin-style peer-to-peer network. Each
"node" is an independent async worker with its own HTTP listener, its own view
of the chain, and its own mempool — mining real proof-of-work against a
public, deterministically-derived target, gossiping blocks and full chains to
peers, and resolving forks by longest-valid-chain, just like real nodes do.
There is no central coordinator: every rule (the mining target, the coinbase
reward, every account's balance, who's allowed to claim they built a block) is
independently recomputed by each node from public chain history, not trusted
from anyone's claim.

It's meant to demonstrate the *mechanism* of a proof-of-work network — forks,
reorgs, difficulty retargeting, coin issuance, balance/double-spend
enforcement, mining pools, and a handful of deliberately malicious node
behaviors — not to be a secure or production-grade implementation. See
[What this is not](#what-this-is-not).

## How it works

- **Proof-of-work.** Every block header carries a public 256-bit `Target`,
  plus the `Rules` (retarget cadence, halving schedule, max supply, ...) its
  builder used to compute it — see [Scenarios](#scenarios)' per-NodeGroup
  `Rules` and [What this is not](#what-this-is-not). Any peer can verify a
  block's hash independently by recomputing the expected target from prior
  block timestamps AND that block's own declared `Rules`
  (`ProofOfWork.ComputeExpectedTargetHex`), then checking the hash satisfies
  it. Difficulty retargets every 2016 blocks to a 10-minute-per-block goal,
  clamped to a 4x swing per retarget, by default — real Bitcoin's own
  numbers. The starting difficulty itself is *not* real Bitcoin's — see
  [What this is not](#what-this-is-not).
- **Mining.** Mining is round-robin: one node gets a turn, then the next, and
  so on, so there's exactly one CPU-bound mining attempt happening at a
  time and no per-node background threads. A turn is bounded — a node tries
  at most `HashPower` nonces before giving up — so a node with more
  simulated hash power wins a much larger share of turns without mining
  being made deterministic. Turn order is reshuffled every time a new block
  appears.
- **Forks & reorgs.** Because nodes mine independently in real time against
  the same public target, two honest nodes can find a valid block at nearly
  the same moment — a genuine, non-malicious fork. Peers gossip their full
  local chain after every block they build; any peer adopts a received
  chain whenever it's strictly longer, shares the same genesis, and is
  fully valid.
- **Peer topology.** Nodes don't form a full mesh — each keeps a small,
  fixed number of outbound peers (`OutboundPeerCount`, default 8, matching
  real Bitcoin), chosen at creation by weighted random sampling from
  whoever already exists (weight = `EconomicWeight`). Connections are
  bidirectional, so a node with a much higher `EconomicWeight` than its
  peers accumulates disproportionately many inbound connections and becomes
  a structural hub — the same dynamic that makes real, well-run,
  publicly-reachable nodes (often run by economically significant
  operators — exchanges, payment processors) relay for far more of the
  network than an ordinary node, without any special protocol role. A node
  that accepts a new block or chain from one peer relays it on to its own
  other peers, so it still reaches the whole network hop by hop as long as
  the peer graph is connected. See `NodeNetwork.cs` and the "Peer
  topology" fields under [Scenarios](#scenarios).
- **Coin issuance.** Each block's winning miner earns a coinbase reward,
  starting (by default) at 50 coins and halving every 210,000 blocks, with
  the total ever minted hard-capped at 21,000,000 — real Bitcoin's own
  numbers, and at these defaults the halving schedule's asymptotic supply
  actually converges to the cap, not just in theory. Every peer recomputes
  the expected reward for any block independently — using that block's own
  declared `Rules`, not a value the whole network is forced to share (see
  [Scenarios](#scenarios)) — and rejects a mismatch.
- **Balances & double-spends.** Every account's balance is derived purely
  from chain history (`Ledger.ComputeBalances`). A block containing a
  transaction that spends more than the sender's balance at that exact
  point in the chain is rejected outright — which transitively catches
  double-spends too.
- **Mining participation.** Mining is optional per node (`CanMine`). A
  wallet-only node is a completely normal network participant — it serves
  `/<node-id>/chain`, `/<node-id>/balances`, `/<node-id>/mempool`, validates
  and relays blocks/chains, and can send or receive coins — it just never
  gets a mining turn.
- **Mining pools.** An honest, mining-enabled node can subscribe to a named
  pool. Every current member's `HashPower` is combined into one shared
  round-robin turn; on that turn, one member is chosen (weighted by its own
  hash-power share) to build the candidate block and run the nonce search.
  The reward is paid to the pool's account and then split among members
  proportional to their hash-power share, as ordinary transactions
  immediately following the coinbase transaction in the same block.
- **Signed blocks.** Each node generates (or reloads) its own ECDSA keypair
  and registers the public half under its own Id. Every block it mines is
  signed with that key; peers reject a block whose signature doesn't verify
  against whoever it claims (`BuiltBy`) — this is what stops an
  Impersonator from redirecting a reward to itself under someone else's
  name.
- **Node roles.** Nodes are normally assigned a mix of behaviors (see
  [Node roles](#node-roles) below), overridable per node via
  `metadata.json` or a scenario file.

## Requirements

- .NET 10 SDK (see `BitcoinNetworkSimulator.csproj`)

## Running

```
dotnet run
```

With no scenario file, this starts one node, grows the network organically
(roughly doubling in size every 8 seconds, capped at 100 nodes), and runs
until you press Enter.

To run a specific scenario:

```
dotnet run -- Scenarios/mining-pool-fairness.yaml
```

(Path is relative to the current directory. `dotnet run` also picks up a
`scenario.yaml` file next to the executable automatically, if present, when
no argument is given.)

While it's running, query any node over HTTP by its id, e.g.:

```
curl http://localhost:5000/000-alpha/chain
curl http://localhost:5000/000-alpha/balances
```

## HTTP API

Every node in the network shares one real HTTP listener on port `5000`
(`NetworkServer.cs`) — a node is addressed by id as the first path segment,
e.g. `/000-alpha/chain` reaches node `000-alpha`'s `/chain` endpoint. An
unknown node id gets a 404 before the request ever reaches a node.

| Endpoint | Method | Description |
|---|---|---|
| `/<node-id>/chain` | GET | That node's full local chain. |
| `/<node-id>/balances` | GET | Every account's balance, computed from that node's chain. |
| `/<node-id>/mempool` | GET | Transactions that node has accepted but not yet mined. |
| `/<node-id>/tx` | POST | Submit a transaction (`{"From", "To", "Amount"}`) to that node's mempool. |
| `/<node-id>/receiveBlock` | POST | Peer-to-peer: offer a single new block to append to that node's tip. |
| `/<node-id>/receiveChain` | POST | Peer-to-peer: offer a full candidate chain; adopted if longer and valid. |

## Scenarios

A scenario file is a YAML mapping with a top-level **`Phases`** list and an
optional top-level **`NodeRules`** list. `Phases` is the run's timeline,
applied in order: phase 0's settings and `NodeGroups` take effect
immediately; each later phase's settings and `NodeGroups` take over once
the previous phase's `DurationSeconds` elapses — so a single run can model
a network changing over time (e.g. a slow-growth early era, then a
pool-dominated high-growth one, then a mature era with churn) instead of
being fixed for its whole duration. A field a phase leaves out inherits
whatever the previous phase had in effect (or the built-in default, for
phase 0) — a phase only needs to state what's actually changing. YAML
comments (`#`) are fair game for narrating *why* a phase is shaped the way
it is. See [`Scenario.cs`](Scenario.cs) for the full field-by-field format.
Single-phase example:

```yaml
Phases:
  - Description: 15 nodes, fixed: 10 plain solo miners plus a 5-member pool.
    DurationSeconds: 900
    AutoGrowth: false
    NodeGroups:
      - { Count: 10, Role: Honest, HashPower: 1, CanMine: true }
      - { Count: 5, Role: Honest, HashPower: 50, CanMine: true, Pool: cooperative }
```

`NodeRules` is a named library of consensus/economics rulesets (retarget
cadence, halving schedule, max supply, ...) that `NodeGroups` entries point
to by name via `RulesName`, instead of every group that happens to share
one repeating the same block. Omitted, a group's `RulesName` defaults to
real Bitcoin's own numbers (see [Per-NodeGroup fields](#scenarios) below);
pointed at different named rules, different groups can genuinely follow
*different* rules within the same network, since every block carries its
own builder's rules with it rather than everyone being forced to share one
value:

```yaml
NodeRules:
  - Name: conservative
    HalvingIntervalBlocks: 210000
    MaxSupply: 21000000
  - Name: aggressive
    HalvingIntervalBlocks: 2100
    MaxSupply: 210000

Phases:
  - Description: A slower, more conservative pool alongside a faster one.
    DurationSeconds: 900
    AutoGrowth: false
    NodeGroups:
      - { Count: 5, Role: Honest, HashPower: 10, Pool: conservative, RulesName: conservative }
      - { Count: 5, Role: Honest, HashPower: 10, Pool: aggressive, RulesName: aggressive }
```

A `Description` longer than a line or two reads better as a folded block
scalar (`>-`) than one unbroken line — see any file in `Scenarios/` for the
convention: wrap the prose at a reasonable column width, and YAML folds the
line breaks back into single spaces at parse time, so the value is exactly
the same string either way.

```yaml
- Description: >-
    15 nodes, fixed: 10 plain solo miners plus a 5-member pool. Demonstrates
    MINING POOLS: the pool mines as one combined entity instead of five
    separate weaker shots, and splits its reward proportionally among members.
  DurationSeconds: 900
```

Multi-phase example — a slow genesis era, then pools emerge, then growth
stops and nodes start churning, running indefinitely once fully mature:

```yaml
Phases:
  # Genesis era: one node, no growth yet.
  - Description: Genesis era
    DurationSeconds: 300
    AutoGrowth: false
    NodeGroups:
      - { Count: 1, Role: Honest, HashPower: 1, CanMine: true }

  # Early growth: organic growth turns on.
  - Description: Early growth
    DurationSeconds: 600
    AutoGrowth: true
    GrowthIntervalSeconds: 8
    GrowthRate: 2.0
    MaxNodes: 50

  # Pools emerge: a mining pool joins mid-run, growth slows.
  - Description: Pools emerge
    DurationSeconds: 600
    GrowthRate: 1.2
    NodeGroups:
      - { Count: 5, Role: Honest, HashPower: 50, CanMine: true, Pool: cooperative }

  # Mature network: growth stops, nodes start churning, runs until Enter.
  - Description: Mature network
    AutoGrowth: false
    ChurnIntervalSeconds: 30
    ChurnRate: 0.05
    ChurnMinNodes: 20
```

**Editor autocomplete.** [`Scenarios/scenario.schema.json`](Scenarios/scenario.schema.json)
is a JSON Schema for the format above — every field's type, valid range, and
the same documentation as `Scenario.cs`'s doc comments, surfaced as
autocomplete and on-hover tooltips in an editor with YAML language server
support (e.g. VS Code's [YAML extension](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml)).
Each file in `Scenarios/` opts in via a leading modeline comment:

```yaml
# yaml-language-server: $schema=./scenario.schema.json
```

A new scenario file needs that same line (adjust the relative path if it
lives outside `Scenarios/`) to get the same autocomplete and inline
validation — e.g. a typo'd field name, an invalid `Role` value, or a bare
top-level list left over from before `Phases`/`NodeRules` existed gets
flagged in the editor before the file is ever run.

Per-phase fields (inside `Phases`):

- `NodeGroups` — nodes to add when this phase begins, applied in order (see [Per-NodeGroup fields](#scenarios) below for each group's own fields), added on top of whatever already exists from earlier phases. Empty/omitted on phase 0 specifically falls back to the normal single-node default start; empty/omitted on any later phase just means no explicit nodes that phase.
- `AutoGrowth` (default `true`) — whether the network keeps growing organically on top of `NodeGroups`.
- `GrowthIntervalSeconds` / `GrowthRate` / `MaxNodes` — override organic growth's pace/rate/cap. `GrowthRate` is a multiplier on the current node count applied each tick (default `2.0`, doubling; `1.5` adds 50% per tick).
- `GrowthJitterSeconds` — random +/- range applied to `GrowthIntervalSeconds` each tick, so growth doesn't land on a perfectly regular schedule (default `0`, no jitter).
- `GrowthMinSeedNodes` — floor the network tops up to, one node per tick, before `GrowthRate` scaling takes over (default `0`, no floor — rate scaling applies from the first tick).
- `GrowthMaliciousFraction` / `GrowthWalletOnlyFraction` — override the role/mining-participation mix for auto-created nodes (the initial dynamic-start node, plus every node organic growth adds — not `NodeGroups`, which set `Role`/`CanMine` explicitly per group). Defaults `0.5` and `1/3`, matching the simulator's original fixed cycling. Organically-grown nodes also get `ConsensusRules`' own defaults (real Bitcoin's numbers), same as a `NodeGroups` entry with no `RulesName`.
- `ChurnIntervalSeconds` / `ChurnRate` / `ChurnMinNodes` — nodes leaving the live network, growth's counterpart. `ChurnRate` is the fraction of the current node count removed each tick (default `0`, disabled); `ChurnMinNodes` is a floor churn won't shrink below (default `1`). Independent of `AutoGrowth` — can run growth and churn together, or churn alone on a fixed population.
- `OutboundPeerCount` — override how many outbound peers each node picks (default 8). See [Peer topology](#how-it-works) and `EconomicWeight` above.
- `DurationSeconds` — how long this phase lasts before the next one takes over (Enter still works too, to stop the whole run early). On the *last* phase, this instead means how long the whole run lasts before automatically shutting down; omitted there means no automatic stop. Omitted on any earlier phase means that phase — and therefore the run — never advances past it, so every non-last phase should set this.

Per-NodeGroup fields:

- `Count` (default `1`) — how many identically-configured nodes this group creates.
- `Role` (default `Honest`) — see [Node roles](#node-roles) below.
- `HashPower` (default `1`) — simulated hash power; see the "Mining" note above.
- `CanMine` (default `true`) — see the "Mining participation" note above; `false` makes this group wallet-only.
- `Pool` (default none — mines solo) — see the "Mining pools" note above.
- `EconomicWeight` (default `1`) — see [Peer topology](#how-it-works) above.
- `RulesName` — name of an entry in the scenario file's top-level `NodeRules` list (below); the consensus/economics ruleset this group's nodes build blocks under. Omitted (or a name not defined in `NodeRules`, logged as a warning) means every field below defaults.

`NodeRules` — a top-level list of named rulesets, each `{ Name, ...the 8
fields below }`. **Unlike every field above, these live on the block
itself**, not just the node: a mining node stamps its own `RulesName`'s
resolved fields onto every block it builds, and any peer validates that
block purely against the rules it declares for itself, not some single
value every node is forced to share (see [What this is
not](#what-this-is-not)). All default to real Bitcoin's own numbers except
`InitialDifficultyShift`, which deliberately can't be:

- `Name` — how a `NodeGroups` entry's `RulesName` refers to this entry. Keep unique — a duplicate `Name` is a scenario-authoring mistake (the last one silently wins).
- `RetargetIntervalBlocks` / `TargetSecondsPerBlock` — how often (in blocks) difficulty retargets, and how long a block "should" take on average. Default `2016` / `600` (10 minutes).
- `MinAdjustmentFactor` / `MaxAdjustmentFactor` — clamp on how much a single retarget can swing the target. Default `0.25` / `4.0` (already real Bitcoin's own clamp).
- `InitialDifficultyShift` — starting difficulty (higher = harder). Default `8` — see [What this is not](#what-this-is-not) for why this one stays simulation-scaled.
- `InitialBlockReward` / `HalvingIntervalBlocks` — coinbase reward for the first block, and how often (in blocks) it halves. Default `50` / `210000`.
- `MaxSupply` — hard cap on total coins ever minted. Default `21000000` — with the default `InitialBlockReward`/`HalvingIntervalBlocks` pair, the halving series actually converges to this asymptotically, so the cap binds for real, not just in theory.

Included scenarios, in [`Scenarios/`](Scenarios/):

| File | Demonstrates |
|---|---|
| `quick-demo.yaml` | A fast sanity check — a handful of modest miners, short duration. |
| `hash-power-disparity.yaml` | Nodes with very different simulated hash power competing for blocks. |
| `mining-pool-fairness.yaml` | A shared pool competing against solo miners, and proportional reward splitting. |
| `wallet-only-network.yaml` | Mining-disabled, wallet-only nodes participating normally otherwise. |
| `malicious-roles-showcase.yaml` | Each malicious node role in action (see below) and how honest nodes catch it. |
| `large-scale-organic-growth.yaml` | A larger network growing over time. |
| `economic-hub-topology.yaml` | A few high-`EconomicWeight` hub nodes among many ordinary ones, with a small `OutboundPeerCount` so the hubs' disproportionate connectivity — and multi-hop relay — is visible. |

## Node roles

Most nodes are `Honest`. The others each deliberately violate one trust
assumption, to demonstrate that the network catches it:

| Role | Behavior | Caught by |
|---|---|---|
| `Equivocator` | Mines two separate valid blocks at the same height to fork the chain. | Real proof-of-work makes this genuinely costly — a deliberate fork, not a free action. |
| `Impersonator` | Claims another node's identity (`BuiltBy`) to redirect a reward. | Can only sign with its own key, which never verifies against the name it's framing. |
| `Corruptor` | Tampers with a block after finding a valid nonce. | The recomputed hash no longer matches the block's contents, and a tampered hash essentially never still satisfies the target. |
| `Withholder` | Only tells some peers about a new block. | The peers it does tell may relay it onward to the ones it excluded; any peer still behind catches up via the next round's full-chain gossip regardless. |

## Persistence & resume

Every run gets its own directory:

```
ScenarioResults/<timestamp>-<scenario name, or "no-scenario">/
  <scenario file>            (copied in, if one was used)
  watcher.db                 (SQLite: network convergence/recovery history — see Watching a run)
  nodes/
    <node-id>/
      blockchain.db           (SQLite: that node's chain — see below)
      metadata.json           (that node's role, hash power, mining/pool config, signing key)
```

On startup, a node resumes from its saved `blockchain.db` if it's
structurally valid and shares this build's genesis; otherwise it starts fresh.
`metadata.json` is loaded if present (so a hand-edited `HashPower`, `NodeRole`,
`CanMine`, `Pool`, or `Rules` value survives a restart) and is safe to
hand-edit — a changed `Rules` only ever affects blocks this node mines from
that point on, never existing history (each already-mined block keeps
whatever `Rules` got stamped onto it at the time, persisted right alongside
it — see the `blocks` table below) — except `SigningKey`, which must never
change once a node has mined blocks, or its historical blocks can no longer
be verified.

Each node's `blockchain.db` (`BlockchainStore`, in `Blockchain.cs`) holds its
local chain across two tables:

| Table | Contents |
|---|---|
| `blocks` | One row per block, keyed by height (`idx`): timestamp, previous/own hash, builder, signature, target, nonce, and that block's own declared `Rules` (retarget cadence, halving schedule, max supply, ...) — see [Scenarios](#scenarios). |
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
(`WatcherStore`, in `Watcher.cs`):

| Table | Contents |
|---|---|
| `run_info` | One row identifying the run: start time, port, scenario path/description. |
| `events` | The append-only event log — block-built, block-accepted, block-rejected, reorganization, and network-state-transition events — with fields like `role`, `built_by`, `nonce`, and `tx_count` broken out into real columns. |
| `audits` | One row per periodic convergence audit: state, convergence/validity flags, height range, blocks observed. |
| `audit_nodes` | Each audited node's per-audit height/tip/validity, foreign-keyed to `audits`. |

It can be queried directly (e.g. with the `sqlite3` CLI or any SQLite
library) while a run is still in progress, and is the basis for
reconstructing reports or charting a run's progression over time.

## Project layout

| File | Responsibility |
|---|---|
| `Program.cs` | Entry point / composition root: reads the scenario's phases and walks them in order, builds a `NodeNetwork`, and starts the mining scheduler, transaction generator, growth/churn loops, watcher, and persistence loops as async tasks. |
| `NodeNetwork.cs` | The live network: the node/miner registry, the peer graph (weighted outbound peer selection — see [Peer topology](#how-it-works)), node naming and default role/mining-participation policy, node creation (`AddNodeAsync`), organic growth (`GrowthLoopAsync`), and node churn/departure (`ChurnLoopAsync`/`RemoveNode`). |
| `MiningScheduler.cs` | Round-robin turn scheduling across whatever `IMiner`s currently exist — solo or pooled, reshuffled whenever a new block appears. |
| `TransactionGenerator.cs` | Synthetic transaction traffic: picks a real sender/recipient pair from live balances each round and submits a transaction. |
| `PersistenceLoop.cs` | Per-node persistence: resumes a node's chain from its `blockchain.db` at startup, then periodically syncs it back for the rest of the run. |
| `Blockchain.cs` | The blockchain data model: `Transaction`, `Block`, `ConsensusRules` (a block's own declared proof-of-work/economics ruleset), `ProofOfWork`, `Economics`, `Ledger`, and `Blockchain` itself (validation and fork-choice logic). Also defines `BlockchainStore` — SQLite persistence for one node's local chain (`blockchain.db`). |
| `NetworkServer.cs` | The single shared HTTP listener; routes each request by node id to that node's handler. |
| `Node.cs` | Per-node request handling: `/<node-id>/chain`, `/<node-id>/tx`, `/<node-id>/receiveBlock`, `/<node-id>/receiveChain`, etc., including relaying an accepted block/chain on to this node's own other peers. Also defines `NodeRole`, `NodeIdentityRegistry` (process-wide table binding node Ids to the public keys they sign blocks with), and `NodeMetadata`/`NodeMetadataStore` (a node's persisted config — role, hash power, economic weight, consensus rules, signing key — and its `metadata.json` load/save/apply logic). |
| `Miner.cs` | `SoloMiner` — nonce search, block assembly, broadcast, and a node's signing identity. Also defines `PoolMiner` (a named group of `SoloMiner`s mining as one combined turn, with proportional reward splitting) and `IMiner` (the common interface the round-robin scheduler rotates over). |
| `Watcher.cs` | `ChainWatcher` — periodic cross-network convergence/validity auditing. Also defines `WatcherStore` — SQLite persistence for the watcher's events and audits (`watcher.db`). |
| `Scenario.cs` | Scenario file format and loader; also computes each run's `ScenarioResults/` result directory. |
| `ElasticTaskPool.cs` | `ElasticTaskPool` — a bounded, load-scaling async worker pool; backs `NetworkServer`'s request handling. |

## What this is not

- Mining difficulty is tiny compared to real Bitcoin (scenario-configurable
  via `InitialDifficultyShift`, see [Scenarios](#scenarios)), and
  deliberately can't be matched to it — mining only gets a bounded number of
  attempts per turn (a node's `HashPower`), not the unbounded,
  massively-parallel search a real miner performs, so real difficulty would
  make a block practically unmineable here. This demonstrates the
  mechanism, not a real proof-of-work barrier — see the retargeting note in
  `ProofOfWork.ComputeExpectedTargetHex`'s comment for what happens when this
  is combined with the (default, real) retarget cadence.
- All nodes run in a single process on `localhost`; there's no real network,
  transport security, or peer discovery.
- Node identity (`NodeIdentityRegistry`) is bootstrapped in-memory, standing
  in for whatever a real network would use (a genesis validator list, an
  on-chain registration transaction, a PKI).
- **"Consensus" here really means self-consistency, not network-wide
  agreement.** Every block carries its own builder's rules (retarget
  cadence, halving schedule, max supply, ...) — see `ConsensusRules` in
  `Blockchain.cs` and [Scenarios](#scenarios)' `NodeRules`/`RulesName` — and
  `ValidateChain` checks a block purely against what IT declares for
  itself. That means a node genuinely can build valid-by-its-own-rules
  blocks under an arbitrary policy (a tiny halving interval, an enormous
  max supply, ...) and the rest of the network will accept them, since
  there's no single value everyone is independently checking a claim
  against — the check is "did this block follow the rules it says it
  followed," not "did this block follow *the* rules." Real Bitcoin has
  exactly one, protocol-wide ruleset for precisely this reason: modeling
  what happens when that assumption is relaxed (rule-divergent miners,
  competing economic policies within one gossip network) is the point here,
  not an oversight.
- A node's outbound peers are chosen once, at creation, and never rotate or
  get evicted for the rest of the run — real Bitcoin periodically refreshes
  connections. There's also no cap on inbound connections (real Bitcoin
  defaults to ~125 total); a high-`EconomicWeight` node can accumulate an
  unbounded number of peers.
