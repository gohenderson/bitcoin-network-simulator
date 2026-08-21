# Bitcoin Network Simulator

A single-process simulation of a Bitcoin-style peer-to-peer network. Each
"node" is an independent async worker with its own view of the chain and
mempool — mining real proof-of-work against a public, deterministically-derived
target, gossiping blocks and full chains to peers, and resolving forks by
longest-valid-chain, just like real nodes do. There is no central
coordinator: every rule (mining target, coinbase reward, account balances,
who's allowed to claim they built a block) is independently recomputed by
each node from public chain history, not trusted from anyone's claim.

It's meant to demonstrate the *mechanism* of a proof-of-work network — forks,
reorgs, difficulty retargeting, coin issuance, balance/double-spend
enforcement, mining pools, mining economics, and a handful of deliberately
malicious node behaviors — not to be a secure or production-grade
implementation. See [What this is not](docs/mechanics.md#what-this-is-not).

## What it simulates

- **Proof-of-work mining** against a public, retargeting difficulty target — see [mechanics](docs/mechanics.md).
- **Forks & reorgs**, resolved by longest-valid-chain.
- **Peer gossip topology** where economically-weighted nodes become structural hubs.
- **Peer discouragement** of nodes that send provably invalid data.
- **Coin issuance**: halving schedule, hard supply cap, independently verified per block.
- **Balance & double-spend enforcement**, derived purely from chain history.
- **Mining pools**, with proportional reward splitting.
- **Signed blocks**, so rewards can't be redirected under someone else's name.
- **Malicious node roles** (`Equivocator`, `Impersonator`, `Corruptor`, `Withholder`) — see [Node roles](docs/mechanics.md#node-roles).
- **Scenario-driven runs**: multi-phase timelines, organic growth/churn, per-group consensus-rule forks, `ValueSeeking` profit-driven mining, pool adoption by realization odds, mining costs/reinvestment, currency debasement — see [Scenarios](docs/scenarios.md).
- **Persistence & resume**, a convergence watcher, and a live web dashboard — see [Persistence, watching, and project layout](docs/operations.md).

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
no argument is given.) See [Scenarios](docs/scenarios.md) for the full
scenario file format and the list of included scenarios.

While it's running, query any node over HTTP by its id, e.g.:

```
curl http://localhost:5000/000-alpha/chain
curl http://localhost:5000/000-alpha/balances
```

Or open `http://localhost:5000/dashboard/` in a browser for a live view of
the network.

## HTTP API

Every node in the network shares one real HTTP listener on port `5000`
(`NetworkServer.cs`) — a node is addressed by id as the first path segment,
e.g. `/000-alpha/chain` reaches node `000-alpha`'s `/chain` endpoint. An
unknown node id gets a 404 before the request ever reaches a node.

| Endpoint | Method | Description |
|---|---|---|
| `/<node-id>/chain` | GET | That node's full local chain. |
| `/<node-id>/balances` | GET | Every account's balance per coin/asset, computed from that node's chain. |
| `/<node-id>/mempool` | GET | Transactions that node has accepted but not yet mined. |
| `/<node-id>/tx` | POST | Submit a transaction (`{"From", "To", "Amount", "Asset"}`) to that node's mempool, which relays it on to peers on the same lineage. |
| `/<node-id>/receiveTx` | POST | Peer-to-peer: offer a transaction another node has already admitted to its own mempool. |
| `/<node-id>/receiveBlock` | POST | Peer-to-peer: offer a single new block to append to that node's tip. |
| `/<node-id>/receiveChain` | POST | Peer-to-peer: offer a full candidate chain; adopted if longer and valid. |
| `/<node-id>/peersFor/<lineage>` | GET | This node's own peer ids for `<lineage>` — its real live peers if that's its current lineage, otherwise whatever it's gossiped for a lineage it once switched away from. |
| `/<node-id>/spendOnLineage` | POST | Spend this node's own balance on a named lineage (`{"Lineage", "To", "Amount"}`), even one it no longer shares consensus with — forwarded to a peer remembered from that lineage if needed. |
| `/dashboard/` | GET | Live web dashboard — see [Persistence, watching, and project layout](docs/operations.md). |

## Documentation

- [How it works](docs/mechanics.md) — proof-of-work, mining, forks, peer topology, node roles, and what this simulator deliberately doesn't model.
- [Scenarios](docs/scenarios.md) — the scenario file format, every field, and the included scenarios.
- [Persistence, watching, and project layout](docs/operations.md) — run/resume, the convergence watcher, the web dashboard, and a file-by-file map of the codebase.
