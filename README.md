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
  expected target from prior block timestamps, using **its own**
  currently-active `ConsensusRules` for that height — not whatever the
  block itself claims (`ProofOfWork.ComputeExpectedTargetHex`; see [What
  this is not](#what-this-is-not) and [Scenarios](#scenarios)'
  `RuleSchedule`) — then checking the hash satisfies it. Difficulty
  retargets every 2016 blocks to a 10-minute-per-block goal, clamped to a
  4x swing per retarget, by default — real Bitcoin's own numbers. The
  starting difficulty itself is *not* real Bitcoin's — see [What this is
  not](#what-this-is-not).
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
- **Peer discouragement.** Real Bitcoin nodes never compare consensus rules
  up front — there's no such field in the handshake — they discover a
  disagreement lazily, the first time a peer actually sends something that
  fails their own validation. This simulator models the same thing: every
  block/chain a node receives is tagged with the sending peer's id
  (`Node.SenderIdHeaderName`), and a rejection that reflects a genuine
  consensus-rule violation in the data itself — not just normal network
  timing like a stale height or a chain that isn't longer yet —
  (`Blockchain.TryAppend`/`TryReplaceWithLongerChain`'s `AttributableToSender`)
  makes the receiving node immediately drop that peer from its own outbound
  set (`NodeNetwork.DiscouragePeer`) and refuse any further requests it
  sends. This is one-directional, same as a real node simply refusing a
  discouraged peer's connection attempts without that peer necessarily
  knowing why — it doesn't ripple out to anyone else's peer graph. A
  discouragement is recorded as a `peer-discouraged` event (see [Watching a
  run](#watching-a-run)).
- **Coin issuance.** Each block's winning miner earns a coinbase reward,
  starting (by default) at 50 coins and halving every 210,000 blocks, with
  the total ever minted hard-capped at 21,000,000 — real Bitcoin's own
  numbers, and at these defaults the halving schedule's asymptotic supply
  actually converges to the cap, not just in theory. Every peer recomputes
  the expected reward for any block independently, using **its own**
  currently-active rules for that height (see [Scenarios](#scenarios)'
  `RuleSchedule`) — not a value the block itself claims — and rejects a
  mismatch.
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

A scenario file is a YAML mapping with a top-level **`Phases`** list, an
optional top-level **`NodeRules`** list, and an optional top-level
**`DefaultRuleSchedule`** list. `Phases` is the run's timeline,
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
to by name — via `RulesName` (one ruleset for a group's whole life) or
`RuleSchedule` (a timeline of named rulesets switching in at specific
heights) — instead of every group that happens to share one repeating the
same block. Omitted, a group defaults to real Bitcoin's own numbers for its
whole life (see [Per-NodeGroup fields](#scenarios) below). **Every node
validates an incoming block against ITS OWN currently-active rules for that
height, not whatever the block claims** (see [What this is
not](#what-this-is-not)) — so groups pointed at the same named rules, or
whose schedules switch at the same height, stay in consensus with each
other; groups whose schedules disagree at a height genuinely diverge, a
simulated fork:

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

A `NodeGroups` entry can also switch rules partway through its life, via
`RuleSchedule` instead of `RulesName` — the multi-era case:

```yaml
NodeRules:
  - Name: real-bitcoin
    HalvingIntervalBlocks: 210000
  - Name: bitcoin-cash
    HalvingIntervalBlocks: 210000
    MaxSupply: 21000000
    RetargetIntervalBlocks: 1  # simplified difficulty-adjustment-per-block

Phases:
  - Description: >-
      This group mines under real-bitcoin rules through height 5, then switches to
      bitcoin-cash rules from height 6 on. Point another group at the SAME schedule
      (same NodeRules, same FromHeight) to keep it in consensus with this one;
      a group that doesn't switch — or switches to something else — forks off instead.
    DurationSeconds: 300
    AutoGrowth: false
    NodeGroups:
      - Count: 5
        Role: Honest
        HashPower: 10
        RuleSchedule:
          - { FromHeight: 0, RulesName: real-bitcoin }
          - { FromHeight: 6, RulesName: bitcoin-cash }
```

See [`Scenarios/consensus-rule-switch.yaml`](Scenarios/consensus-rule-switch.yaml)
for this worked as a full, runnable scenario (two groups, one switches and
one doesn't, producing a real fork), and
[`Scenarios/mining-pool-fairness.yaml`](Scenarios/mining-pool-fairness.yaml)
for the synchronized case (both groups switch together and stay in
consensus).

**Caution for `AutoGrowth: true` scenarios:** a node organic growth adds was
never authored by any `NodeGroups` entry, so its rules come from
`DefaultRuleSchedule` below — real Bitcoin's own numbers, for its whole
life, if `DefaultRuleSchedule` is omitted (see [Per-NodeGroup
fields](#scenarios) below). If a `NodeGroups` entry's own schedule later
switches to something else, it forks away from every organically-grown node
(or every share of them not itself claimed by a matching
`DefaultRuleSchedule` entry) the moment it switches — almost certainly not
what you want in a growth demo. Give a `RuleSchedule` that switches only to
scenarios with `AutoGrowth: false` (a fixed, fully `NodeGroups`-authored
population), unless the fork itself is the point.

`DefaultRuleSchedule` controls which rules organically-grown nodes follow —
every node organic growth creates, plus the initial dynamic-start node, but
never a `NodeGroups`-authored node (those always use their own
`RulesName`/`RuleSchedule`). It's a list of **tranches** — `{ FromHeight,
RuleSchedules }` — each active for nodes created at or after `FromHeight`,
replacing whichever earlier tranche was active outright (not blended with
it) once the network reaches that height. A tranche's `RuleSchedules` is
the *full* distribution new nodes are drawn from at that stage: an array of
`{ Percent, RulesName }` options, where `Percent` (0-100, not a 0-1
fraction) is the chance a new node gets that option's named ruleset as its
own, single, lifelong `ConsensusRules`. A tranche's `Percent`s are summed
in list order against a 100-point pool; whatever's left unclaimed falls
back to a node's hardcoded `ConsensusRules` defaults. For example:

```yaml
DefaultRuleSchedule:
  - FromHeight: 0
    RuleSchedules:
      - Percent: 50
        RulesName: bitcoin-cash
```

— half of all organically-grown nodes get `bitcoin-cash`'s rules for their
whole life, the other half fall back to hardcoded defaults. A real network
distribution that itself shifts as the network matures:

```yaml
DefaultRuleSchedule:
  - FromHeight: 0
    RuleSchedules:
      - Percent: 80
        RulesName: real-bitcoin
      - Percent: 20
        RulesName: bitcoin-cash
  - FromHeight: 5000
    RuleSchedules:
      - Percent: 50
        RulesName: real-bitcoin
      - Percent: 50
        RulesName: bitcoin-cash
```

— up to height 5000, new nodes split 80/20 between `real-bitcoin` and
`bitcoin-cash`; the second (higher-`FromHeight`) tranche then REPLACES that
distribution outright for anything created from height 5000 on, evening
the split to 50/50 — nodes already created under the first tranche keep
whatever they were assigned, they aren't retroactively reassigned.
`real-bitcoin` and `bitcoin-cash` nodes reject each other's blocks outright
(a real, simulated fork) the moment their paths cross — see the "Peer
discouragement" note above for what that does to their connection.

`ValueSeeking` is a `NodeGroups` field that replaces a fixed
`RulesName`/`RuleSchedule` with a live *computation*: instead of an author
scripting which ruleset a group follows, each node in the group picks
whichever of its `ValueSeekingCandidates` currently pays the most —
`NominalBlockReward(height, rules) x Price(rules, height)` — recomputed
fresh every time it mines. `Price` comes from each `NodeRules` entry's own
`PriceSchedule` (the same `{ FromHeight, value }` shape as everything else
here — see [`NodeRules`](#scenarios) below), a $-reference value over
height, letting a scenario script a market event (a crash, a pump, a
delisting) the same way it scripts a rule change. Because `PriceSchedule`
and every candidate's `ConsensusRules` are public, scenario-authored facts
— not private per-node randomness — every `ValueSeeking` node
independently recomputes the identical answer at a given height, with no
coordination needed, the same "recompute it yourself, don't trust a claim"
property `ProofOfWork` and `Economics` already rely on elsewhere. For
example:

```yaml
NodeRules:
  - Name: real-bitcoin
    InitialBlockReward: 50
    PriceSchedule:
      - { FromHeight: 0, Price: 100 }
      - { FromHeight: 8, Price: 5 }
  - Name: bitcoin-cash
    InitialBlockReward: 10
    PriceSchedule:
      - { FromHeight: 0, Price: 20 }
      - { FromHeight: 8, Price: 200 }

NodeGroups:
  - Count: 4
    ValueSeeking: true
    ValueSeekingCandidates: [real-bitcoin, bitcoin-cash]
```

— before height 8, `real-bitcoin` pays more (50 x 100 = 5000 vs. 10 x 20 =
200), so every node in the group mines `real-bitcoin` blocks; at height 8
the prices cross (50 x 5 = 250 vs. 10 x 200 = 2000) and every node
independently switches to `bitcoin-cash` from that point on. `ValueSeeking`
considers only the *explicit* names in `ValueSeekingCandidates`, never
every priced `NodeRules` entry implicitly, and takes precedence over
`RulesName`/`RuleSchedule` outright (logged warning) if both are set on
the same group. Mining nodes only — a node with no mining turn has no
"which ruleset am I building under" decision to make — and NodeGroups-authored
only for now, not (yet) available to organically-grown nodes via
`DefaultRuleSchedule`. See `Scenarios/value-seeking-competition.yaml` for
this exact example running end to end.

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
- `GrowthMaliciousFraction` / `GrowthWalletOnlyFraction` — override the role/mining-participation mix for auto-created nodes (the initial dynamic-start node, plus every node organic growth adds — not `NodeGroups`, which set `Role`/`CanMine` explicitly per group). Defaults `0.5` and `1/3`, matching the simulator's original fixed cycling. Organically-grown nodes' rules come from the top-level `DefaultRuleSchedule` (see [Scenarios](#scenarios) above) — real Bitcoin's own defaults for any share it doesn't claim.
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
- `RulesName` — shorthand for a single-entry `RuleSchedule` (`{ FromHeight: 0, RulesName }`): this group's consensus/economics ruleset for its whole life, by name from the scenario file's top-level `NodeRules` list (below). Omitted (or a name not defined in `NodeRules`, logged as a warning) means every field below defaults. Ignored (with a warning) if `RuleSchedule` is also set.
- `RuleSchedule` — this group's full timeline of which named ruleset (from `NodeRules`) is active at which block height, as a list of `{ FromHeight, RulesName }` entries — e.g. `real-bitcoin` from height 0, switching to a different named ruleset from height 6 on. Takes precedence over `RulesName` if both are set.
- `ValueSeeking` (default `false`) — see the "ValueSeeking" note above. Dynamically picks this group's ruleset each height by live profitability instead of a fixed `RulesName`/`RuleSchedule`. Takes precedence over both (with a warning) if set alongside either and `ValueSeekingCandidates` resolves to at least one valid entry.
- `ValueSeekingCandidates` — the explicit `NodeRules` names this group compares when `ValueSeeking` is `true`. A name not defined in `NodeRules` is a scenario-authoring mistake (logged as a warning, skipped).

`NodeRules` — a top-level list of named rulesets, each `{ Name, ...the 8
fields below, plus an optional PriceSchedule }`. **Every node validates an incoming block against ITS OWN
`RuleSchedule` for that block's height, never against whatever the block
itself claims** (`Block.Rules` is recorded for logging/introspection only —
see [What this is not](#what-this-is-not)). So two `NodeGroups` stay in
consensus with each other exactly when their resolved schedules agree at a
given height (same named ruleset, switching at the same `FromHeight`);
otherwise they diverge, same as a real hard fork. All default to real
Bitcoin's own numbers except `InitialDifficultyShift`, which deliberately
can't be:

- `Name` — how a `NodeGroups` entry's `RulesName` refers to this entry. Keep unique — a duplicate `Name` is a scenario-authoring mistake (the last one silently wins).
- `RetargetIntervalBlocks` / `TargetSecondsPerBlock` — how often (in blocks) difficulty retargets, and how long a block "should" take on average. Default `2016` / `600` (10 minutes).
- `MinAdjustmentFactor` / `MaxAdjustmentFactor` — clamp on how much a single retarget can swing the target. Default `0.25` / `4.0` (already real Bitcoin's own clamp).
- `InitialDifficultyShift` — starting difficulty (higher = harder). Default `8` — see [What this is not](#what-this-is-not) for why this one stays simulation-scaled.
- `InitialBlockReward` / `HalvingIntervalBlocks` — coinbase reward for the first block, and how often (in blocks) it halves. Default `50` / `210000`.
- `MaxSupply` — hard cap on total coins ever minted. Default `21000000` — with the default `InitialBlockReward`/`HalvingIntervalBlocks` pair, the halving series actually converges to this asymptotically, so the cap binds for real, not just in theory.
- `PriceSchedule` — this ruleset's $-reference value over height, as a list of `{ FromHeight, Price }` entries — see the "ValueSeeking" note above. Omitted means worth $0 at every height (never picked over any priced alternative by a `ValueSeeking` node).

Included scenarios, in [`Scenarios/`](Scenarios/):

| File | Demonstrates |
|---|---|
| `quick-demo.yaml` | A fast sanity check — a handful of modest miners, short duration. |
| `hash-power-disparity.yaml` | Nodes with very different simulated hash power competing for blocks. |
| `mining-pool-fairness.yaml` | A shared pool competing against solo miners, proportional reward splitting, and a synchronized `RuleSchedule` switch partway through (both groups switch together, stay in consensus). |
| `wallet-only-network.yaml` | Mining-disabled, wallet-only nodes participating normally otherwise. |
| `malicious-roles-showcase.yaml` | Each malicious node role in action (see below) and how honest nodes catch it. |
| `large-scale-organic-growth.yaml` | A larger network growing over time. |
| `economic-hub-topology.yaml` | A few high-`EconomicWeight` hub nodes among many ordinary ones, with a small `OutboundPeerCount` so the hubs' disproportionate connectivity — and multi-hop relay — is visible. |
| `consensus-rule-switch.yaml` | Two groups start under the same rules; one switches to a different named `RuleSchedule` entry partway through and the other doesn't — a real, simulated hard fork. |
| `value-seeking-competition.yaml` | `ValueSeeking` nodes compare two rulesets' live profitability (`NominalBlockReward x PriceSchedule`) and switch sides the moment a scripted price crossover makes the other one pay more — a fixed control group stays put and forks away from them. |

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
`CanMine`, `Pool`, or `RuleSchedule` value survives a restart) and is safe to
hand-edit — a changed `RuleSchedule` only ever affects what this node
builds AND validates from the moment it's loaded on, never existing
history: each already-mined block keeps whatever it recorded in its own
`Rules` at the time (informational only — see [What this is
not](#what-this-is-not) — persisted right alongside it, see the `blocks`
table below), and `TryLoadFrom`'s resume-time validation checks the
resumed chain against the *freshly-loaded* `RuleSchedule`, so a resumed
chain that was built under the old schedule but no longer matches the
new one fails to load — except `SigningKey`, which must never change once
a node has mined blocks, or its historical blocks can no longer be
verified.

Each node's `blockchain.db` (`BlockchainStore`, in `Blockchain.cs`) holds its
local chain across two tables:

| Table | Contents |
|---|---|
| `blocks` | One row per block, keyed by height (`idx`): timestamp, previous/own hash, builder, signature, target, nonce, and that block's own recorded (informational-only) `Rules` (retarget cadence, halving schedule, max supply, ...) — see [Scenarios](#scenarios) and [What this is not](#what-this-is-not). |
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
| `Blockchain.cs` | The blockchain data model: `Transaction`, `Block`, `ConsensusRules` (one proof-of-work/economics ruleset), `RuleSchedule` (a node's own timeline of which `ConsensusRules` is active at which height — what `ValidateChain` actually checks incoming blocks against), `ProofOfWork`, `Economics`, `Ledger`, and `Blockchain` itself (validation and fork-choice logic). Also defines `BlockchainStore` — SQLite persistence for one node's local chain (`blockchain.db`). |
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
- **Each node owns its own consensus-rules timeline, and a real fork is
  possible if timelines disagree.** Every node has a `RuleSchedule` — which
  `ConsensusRules` (retarget cadence, halving schedule, max supply, ...) is
  active at which block height — see `RuleSchedule` in `Blockchain.cs` and
  [Scenarios](#scenarios)' `RuleSchedule`/`RulesName`. When a node validates
  an incoming block, it looks up **its own** schedule for that block's
  height and checks the block's target/reward against that — never against
  whatever the block itself claims (`Block.Rules` is recorded for
  informational/logging purposes only; peers don't trust it). So consensus
  here means what it means in real Bitcoin: nodes whose schedules agree at
  a given height independently arrive at the same expected target/reward
  and stay on one chain; nodes whose schedules disagree at a height (one
  switches rulesets and the other doesn't, or they switch to different
  ones) independently arrive at different expected values and reject each
  other's blocks from that point on — a real, simulated hard fork. That
  divergence, not a single node getting to declare its own arbitrary rules
  and have everyone accept them, is what a `RuleSchedule` is for modeling.
  `ValueSeeking` is a dynamically-computed alternative to an author-scripted
  `RuleSchedule` — see [Scenarios](#scenarios)' `ValueSeeking` note — but
  still produces the same deterministic answer per height on every node
  that shares its candidate set, so it forks (or stays in consensus) by
  exactly the same rule.
- A node's outbound peers are chosen once, at creation, and never rotate or
  get evicted for the rest of the run — real Bitcoin periodically refreshes
  connections. There's also no cap on inbound connections (real Bitcoin
  defaults to ~125 total); a high-`EconomicWeight` node can accumulate an
  unbounded number of peers.
