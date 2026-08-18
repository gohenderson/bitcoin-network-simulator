# How it works

Full detail behind the summary in the main [README](../README.md). See also
[Scenarios](scenarios.md) for how these mechanisms are configured per run,
and [What this is not](#what-this-is-not) below for the simplifications this
simulator deliberately makes.

- **Proof-of-work.** Every block header carries a public 256-bit `Target`.
  Any peer can verify a block's hash independently by recomputing the
  expected target from prior block timestamps, using **its own**
  currently-active `ConsensusRules` for that height — not whatever the
  block itself claims (`ProofOfWork.ComputeExpectedTargetHex`; see [What
  this is not](#what-this-is-not) and [Scenarios](scenarios.md)'
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
  topology" fields in [Scenarios](scenarios.md).
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
  run](operations.md#watching-a-run)).
- **Coin issuance.** Each block's winning miner earns a coinbase reward,
  starting (by default) at 50 coins and halving every 210,000 blocks, with
  the total ever minted hard-capped at 21,000,000 — real Bitcoin's own
  numbers, and at these defaults the halving schedule's asymptotic supply
  actually converges to the cap, not just in theory. Every peer recomputes
  the expected reward for any block independently, using **its own**
  currently-active rules for that height (see [Scenarios](scenarios.md)'
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
- **Node roles.** Nodes are normally assigned a mix of behaviors (see [Node
  roles](#node-roles) below), overridable per node via `metadata.json` or a
  scenario file.

## Node roles

Most nodes are `Honest`. The others each deliberately violate one trust
assumption, to demonstrate that the network catches it:

| Role | Behavior | Caught by |
|---|---|---|
| `Equivocator` | Mines two separate valid blocks at the same height to fork the chain. | Real proof-of-work makes this genuinely costly — a deliberate fork, not a free action. |
| `Impersonator` | Claims another node's identity (`BuiltBy`) to redirect a reward. | Can only sign with its own key, which never verifies against the name it's framing. |
| `Corruptor` | Tampers with a block after finding a valid nonce. | The recomputed hash no longer matches the block's contents, and a tampered hash essentially never still satisfies the target. |
| `Withholder` | Only tells some peers about a new block. | The peers it does tell may relay it onward to the ones it excluded; any peer still behind catches up via the next round's full-chain gossip regardless. |

## What this is not

- Mining difficulty is tiny compared to real Bitcoin (scenario-configurable
  via `InitialDifficultyShift`, see [Scenarios](scenarios.md)), and
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
  [Scenarios](scenarios.md)' `RuleSchedule`/`RulesName`. When a node validates
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
  `RuleSchedule` — see [Scenarios](scenarios.md)' `ValueSeeking` note — but
  still produces the same deterministic answer per height on every node
  that shares its candidate set, so it forks (or stays in consensus) by
  exactly the same rule.
- A node's outbound peers are chosen once, at creation, and never rotate or
  get evicted for the rest of the run — real Bitcoin periodically refreshes
  connections. There's also no cap on inbound connections (real Bitcoin
  defaults to ~125 total); a high-`EconomicWeight` node can accumulate an
  unbounded number of peers.
