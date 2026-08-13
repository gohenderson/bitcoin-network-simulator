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

- **Proof-of-work.** Every block header carries a public 256-bit `Target`.
  Any peer can verify a block's hash independently by recomputing the
  expected target from prior block timestamps (`ProofOfWork.ComputeExpectedTargetHex`)
  and checking the hash satisfies it. Difficulty retargets every 10 blocks
  to a 3-second-per-block goal, clamped to a 4x swing per retarget.
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
- **Coin issuance.** Each block's winning miner earns a coinbase reward,
  starting at 50 coins and halving every 210 blocks (toy-scaled down from
  Bitcoin's 210,000), with the total ever minted hard-capped at 21,000,000.
  Every node recomputes the expected reward for any block independently and
  rejects a mismatch.
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
dotnet run -- Scenarios/mining-pool-fairness.json
```

(Path is relative to the current directory. `dotnet run` also picks up a
`scenario.json` file next to the executable automatically, if present, when
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

A scenario file declares a run's starting population, growth behavior, and
duration up front, instead of hand-editing `metadata.json` files after the
fact. See [`Scenario.cs`](Scenario.cs) for the full format. Example:

```json
{
  "Description": "15 nodes, fixed: 10 plain solo miners plus a 5-member pool.",
  "DurationSeconds": 900,
  "AutoGrowth": false,
  "NodeGroups": [
    { "Count": 10, "Role": "Honest", "HashPower": 1, "CanMine": true },
    { "Count": 5, "Role": "Honest", "HashPower": 50, "CanMine": true, "Pool": "cooperative" }
  ]
}
```

- `NodeGroups` — starting nodes, applied in order, as `{Count, Role, HashPower, CanMine, Pool}` groups.
- `AutoGrowth` (default `true`) — whether the network keeps growing organically on top of `NodeGroups`.
- `GrowthIntervalSeconds` / `MaxNodes` — override organic growth's pace/cap.
- `DurationSeconds` — automatically stop after this many seconds (Enter still works too, to stop early).

Included scenarios, in [`Scenarios/`](Scenarios/):

| File | Demonstrates |
|---|---|
| `quick-demo.json` | A fast sanity check — a handful of modest miners, short duration. |
| `hash-power-disparity.json` | Nodes with very different simulated hash power competing for blocks. |
| `mining-pool-fairness.json` | A shared pool competing against solo miners, and proportional reward splitting. |
| `wallet-only-network.json` | Mining-disabled, wallet-only nodes participating normally otherwise. |
| `malicious-roles-showcase.json` | Each malicious node role in action (see below) and how honest nodes catch it. |
| `large-scale-organic-growth.json` | A larger network growing over time. |

## Node roles

Most nodes are `Honest`. The others each deliberately violate one trust
assumption, to demonstrate that the network catches it:

| Role | Behavior | Caught by |
|---|---|---|
| `Equivocator` | Mines two separate valid blocks at the same height to fork the chain. | Real proof-of-work makes this genuinely costly — a deliberate fork, not a free action. |
| `Impersonator` | Claims another node's identity (`BuiltBy`) to redirect a reward. | Can only sign with its own key, which never verifies against the name it's framing. |
| `Corruptor` | Tampers with a block after finding a valid nonce. | The recomputed hash no longer matches the block's contents, and a tampered hash essentially never still satisfies the target. |
| `Withholder` | Only tells some peers about a new block. | Peers catch up via the next round's full-chain gossip. |

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
`CanMine`, or `Pool` value survives a restart) and is safe to hand-edit —
except `SigningKey`, which must never change once a node has mined blocks, or
its historical blocks can no longer be verified.

Each node's `blockchain.db` (`BlockchainStore`, in `Blockchain.cs`) holds its
local chain across two tables:

| Table | Contents |
|---|---|
| `blocks` | One row per block, keyed by height (`idx`): timestamp, previous/own hash, builder, signature, target, nonce. |
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
| `Program.cs` | Entry point / composition root: reads the scenario, builds a `NodeNetwork`, and starts the mining scheduler, transaction generator, growth loop, watcher, and persistence loops as async tasks. |
| `NodeNetwork.cs` | The live network: the node/miner registry, node naming and default role/mining-participation policy, node creation (`AddNodeAsync`), and organic growth (`GrowthLoopAsync`). |
| `MiningScheduler.cs` | Round-robin turn scheduling across whatever `IMiner`s currently exist — solo or pooled, reshuffled whenever a new block appears. |
| `TransactionGenerator.cs` | Synthetic transaction traffic: picks a real sender/recipient pair from live balances each round and submits a transaction. |
| `PersistenceLoop.cs` | Per-node persistence: resumes a node's chain from its `blockchain.db` at startup, then periodically syncs it back for the rest of the run. |
| `Blockchain.cs` | The blockchain data model: `Transaction`, `Block`, `ProofOfWork`, `Economics`, `Ledger`, and `Blockchain` itself (validation and fork-choice logic). Also defines `BlockchainStore` — SQLite persistence for one node's local chain (`blockchain.db`). |
| `NetworkServer.cs` | The single shared HTTP listener; routes each request by node id to that node's handler. |
| `Node.cs` | Per-node request handling: `/<node-id>/chain`, `/<node-id>/tx`, `/<node-id>/receiveBlock`, `/<node-id>/receiveChain`, etc. Also defines `NodeRole`, `NodeIdentityRegistry` (process-wide table binding node Ids to the public keys they sign blocks with), and `NodeMetadata`/`NodeMetadataStore` (a node's persisted config — role, hash power, signing key — and its `metadata.json` load/save/apply logic). |
| `Miner.cs` | `SoloMiner` — nonce search, block assembly, broadcast, and a node's signing identity. Also defines `PoolMiner` (a named group of `SoloMiner`s mining as one combined turn, with proportional reward splitting) and `IMiner` (the common interface the round-robin scheduler rotates over). |
| `Watcher.cs` | `ChainWatcher` — periodic cross-network convergence/validity auditing. Also defines `WatcherStore` — SQLite persistence for the watcher's events and audits (`watcher.db`). |
| `Scenario.cs` | Scenario file format and loader; also computes each run's `ScenarioResults/` result directory. |
| `ElasticTaskPool.cs` | `ElasticTaskPool` — a bounded, load-scaling async worker pool; backs `NetworkServer`'s request handling. |

## What this is not

- Mining difficulty is tiny compared to real Bitcoin (tunable via
  `ProofOfWork.InitialDifficultyShift`) — this demonstrates the mechanism,
  not a real proof-of-work barrier.
- All nodes run in a single process on `localhost`; there's no real network,
  transport security, or peer discovery.
- Node identity (`NodeIdentityRegistry`) is bootstrapped in-memory, standing
  in for whatever a real network would use (a genesis validator list, an
  on-chain registration transaction, a PKI).
- With the toy-scaled halving schedule (halving every 210 blocks at the same
  50-coin initial reward), the reward series naturally converges to 21,000
  coins, not the enforced 21,000,000-coin cap — the cap is real but doesn't
  end up binding with these particular numbers.
