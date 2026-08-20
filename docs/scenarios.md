# Scenarios

Full field-by-field reference for scenario files. See the main
[README](../README.md) for how to run one, and [How it works](mechanics.md)
for the mechanisms these fields configure.

A scenario file is a YAML mapping with a top-level **`Phases`** list, an
optional top-level **`NodeRules`** list, an optional top-level
**`DefaultRuleSchedule`** list, and an optional top-level
**`DebasementRatePerBlock`** number (see "Debasement" below). `Phases` is the run's timeline,
applied in order: phase 0's settings and `NodeGroups` take effect
immediately; each later phase's settings and `NodeGroups` take over once
the previous phase's `DurationSeconds` elapses — so a single run can model
a network changing over time (e.g. a slow-growth early era, then a
pool-dominated high-growth one, then a mature era with churn) instead of
being fixed for its whole duration. A field a phase leaves out inherits
whatever the previous phase had in effect (or the built-in default, for
phase 0) — a phase only needs to state what's actually changing. YAML
comments (`#`) are fair game for narrating *why* a phase is shaped the way
it is. See [`Scenario.cs`](../Scenario.cs) for the full field-by-field format.
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
whole life (see [Per-`NodeRules` fields](#per-noderules-fields) below). **Every node
validates an incoming block against ITS OWN currently-active rules for that
height, not whatever the block claims** (see [What this is
not](mechanics.md#what-this-is-not)) — so groups pointed at the same named rules, or
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

See [`Scenarios/consensus-rule-switch.yaml`](../Scenarios/consensus-rule-switch.yaml)
for this worked as a full, runnable scenario (two groups, one switches and
one doesn't, producing a real fork), and
[`Scenarios/mining-pool-fairness.yaml`](../Scenarios/mining-pool-fairness.yaml)
for the synchronized case (both groups switch together and stay in
consensus).

**Caution for `AutoGrowth: true` scenarios:** a node organic growth adds was
never authored by any `NodeGroups` entry, so its rules come from
`DefaultRuleSchedule` below — real Bitcoin's own numbers, for its whole
life, if `DefaultRuleSchedule` is omitted (see [Per-`NodeRules` fields](#per-noderules-fields)
below). If a `NodeGroups` entry's own schedule later
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
(a real, simulated fork) the moment their paths cross — see [Peer
discouragement](mechanics.md) for what that does to their connection.

`ValueSeeking` is a `NodeGroups` field that replaces a fixed
`RulesName`/`RuleSchedule` with a live *computation*: instead of an author
scripting which ruleset a group follows, each node in the group picks
whichever of its `ValueSeekingCandidates` has the highest EXPECTED value —
`ProofOfWork.WinProbability(HashPower, rules.InitialDifficultyShift) x
NominalBlockReward(height, rules) x Price(rules, height)` — recomputed
fresh every time it mines. `Price` comes from each `NodeRules` entry's own
`PriceSchedule` (the same `{ FromHeight, value }` shape as everything else
here — see [Per-`NodeRules` fields](#per-noderules-fields) below), a $-reference value over
height, letting a scenario script a market event (a crash, a pump, a
delisting) the same way it scripts a rule change. Weighing by win
probability means a candidate that pays more nominally isn't automatically
the best pick if it's also much harder to actually mine (see
`InitialDifficultyShift` under [Per-`NodeRules` fields](#per-noderules-fields) below) — and
because that probability depends on *this node's own* `HashPower`, two
`ValueSeeking` nodes with different hash power can rationally reach
*opposite* conclusions from the exact same public data (see
`Scenarios/mining-difficulty-tradeoff.yaml`). Determinism still holds *per
node*: `PriceSchedule` and every candidate's `ConsensusRules` are public,
scenario-authored facts — not private per-node randomness — so any two
`ValueSeeking` nodes that share both a candidate set and a `HashPower`
always independently recompute the identical answer at a given height, the
same "recompute it yourself, don't trust a claim" property `ProofOfWork`
and `Economics` already rely on elsewhere. For example (both rulesets here
share the same, unstated — so default — `InitialDifficultyShift`, which
cancels out of the comparison identically for both, leaving the raw
`reward x price` numbers below untouched by win probability):

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

**Mining costs.** `ValueSeeking`'s profitability comparison otherwise
assumes mining is free — real mining costs electricity and hardware.
`CostPerAttempt` (a per-`NodeGroups` field, default `0`) puts a $ price on
each nonce a node tries. Because the same hardware costs the same to run
regardless of which candidate ruleset it's pointed at, this cost is
identical across every candidate — so it never changes *which* one is
most profitable, only *whether* mining is worth doing at all this turn:
each turn, if the best candidate's expected value doesn't clear
`CostPerAttempt x HashPower`, the node sits out entirely (`going idle` in
the console) rather than mining at a guaranteed loss, and resumes
(`resuming mining`) the moment a candidate clears the bar again —
modeling real hash power abandoning an unprofitable market instead of
chasing a doomed reward. See `Scenarios/value-seeking-competition.yaml`'s
third price stage, where both rulesets' expected value collapses under
its `CostPerAttempt: 2` / `HashPower: 20` group (threshold `40`) and the
group goes idle.

**Cost of living.** `CostPerAttempt` is *variable* — a node dodges it
entirely by going idle. Real operations also carry *fixed* overhead (rent,
staff, hosting) owed every period whether or not any work happens that
period. `CostOfLiving` (a per-`NodeGroups` field, default `0`) models
that: a $ bill owed every turn *regardless of outcome* — mining, idle, or
otherwise — so idling no longer dodges it. It's compared against this
node's actual on-chain balance's current $ value (coins held, from
`Ledger.ComputeBalances`, times its currently-active candidate's price) —
not settled via an actual on-chain transaction, since there's no natural
recipient and letting a node "spend" what it doesn't have would
contradict the insufficient-balance rule that holds everywhere else in
this codebase. Concretely: `CostOfLiving` accrues turn over turn into a
running total, and the moment that total exceeds this node's net worth
plus `StartingCapital` (a $ runway a node starts with, so a brand-new node
with zero balance and zero income isn't judged bankrupt on turn one), the
node is **insolvent** and forced out of the network — the same churn
`ChurnLoopAsync` already uses, just triggered by this node's own economics
instead of a random tick. Only meaningful for a `ValueSeeking` group,
which is the only kind with `PriceSchedule` data to value its own balance
against. See `Scenarios/mining-bankruptcy.yaml`, where a node whose
`CostOfLiving` structurally exceeds even its best-case mining income goes
bankrupt within its first few turns, watched over by a comfortably solvent
one mining the same ruleset.

**Reinvestment.** Every mechanism above lets a node survive or fail, but
none of them let it *grow* — `HashPower` has otherwise been fixed for a
node's whole life. `HashPowerCost` (a per-`NodeGroups` field, default `0`)
closes that loop: once a node's own *earned profit* — net worth beyond
whatever's already committed to accrued `CostOfLiving` or past purchases —
covers `HashPowerCost`, it buys `+1 HashPower`, the same real dynamic of
mining operations reinvesting winnings into more hardware instead of just
banking them. Deliberately never draws on `StartingCapital`, which stays a
protected solvency buffer rather than investable capital — a node still
living off its starting cushion (hasn't yet out-earned its own bills)
correctly doesn't reinvest money it hasn't actually made. At most one
purchase per turn, so growth is gradual and observable rather than an
instant lump sum. Because `HashPower` feeds directly into
`ProofOfWork.WinProbability`, reinvestment is a genuine compounding loop —
more hash power wins more often, which affords more hash power — capped by
`MaxHashPower` (default `0`, uncapped) so a long-running, consistently
profitable node doesn't grow its own per-turn hash computation without
bound. Only meaningful for a `ValueSeeking` group, same restriction as
`CostPerAttempt`/`CostOfLiving`. See `Scenarios/mining-reinvestment.yaml`,
where a reinvesting node's win probability climbs from 7.5% to 17.8% over
the run while a fixed-`HashPower` control node's stays flat.

**Pool adoption.** Every mechanic above (`ValueSeeking`, `CostOfLiving`,
reinvestment) maximizes *expected value*. Pool membership doesn't — it
can't: a proportional share of a bigger pie is never bigger than the pie
kept whole, so pooling never raises expected coins over solo mining. What
pooling actually buys is **realization** — turning an effectively-infinite
wait for a first payout into a small, predictable, frequent one, by
combining hash power so the *group* wins often and every member takes a
slice of every group win. A `PoolCandidates` list (a per-`NodeGroups`
field, default empty — disables reconsideration entirely) makes a node
re-evaluate, once per turn, whether to stay put or move, using a threshold
rule instead of an EV comparison: if its own solo win probability
(`ProofOfWork.WinProbability(HashPower, shift)`) is at or above
`PoolAdoptionThreshold` (default `0.5`), it stays solo (or leaves any pool
it's in) — EV wins, and EV always favors solo. Below the threshold,
realization dominates instead: it joins whichever option — its current
pool, or a named candidate — maximizes the *group's* win probability,
dilution be damned, since even a small share of a group that actually wins
beats a share of one that almost never does. This is why real mining
farms solo-mine (or run their own pool) while hobbyists flock to large
public pools: a farm's own odds already clear the bar, a hobbyist's don't.
Needs no `Price`/`PriceSchedule` data (unlike `ValueSeeking`, `CostOfLiving`,
and reinvestment) since it only compares win probabilities, not $ value, so
it works for a fixed-`RulesName` group exactly as well as a `ValueSeeking`
one. Reconsideration happens once per full round-robin sweep — the same
cadence every other candidate/height lookup in this codebase gets — and a
tie (e.g. a candidate pool with no other members yet, mathematically
identical to staying solo) never triggers a move, so a node never
spontaneously founds a pointless pool of one. See
`Scenarios/mining-pool-adoption.yaml`, where a high-`HashPower` node's own
odds clear the threshold and it mines solo the whole run, while several
low-`HashPower` nodes' odds don't and they each join a shared pool as soon
as they get a turn.

**Debasement.** Every $ figure above — `PriceSchedule`'s prices,
`CostPerAttempt`, `CostOfLiving`, `HashPowerCost` — is authored as a
real, today's-dollars amount. A top-level (not per-`NodeGroup`, not
per-`NodeRules`) `DebasementRatePerBlock` (default `0`, disabling this
entirely) nominally inflates all of them by the same compounding factor,
`(1 + rate) ^ height`, computed once by `RuleSchedule.DebasementFactorAt`
and applied wherever a $ figure is read: `PriceSchedule` lookups, and each
of the three cost checks. It's file-wide rather than a per-node knob like
`CostOfLiving` itself, because every node's $ comparisons — `ValueSeeking`
choosing between candidates, a cost check against `BestValueAt` — only
make sense if the whole scenario shares one currency; letting two nodes
debase at different rates would be modeling two different currencies, not
one inflating one. `StartingCapital` is deliberately the one $ figure left
alone: it's a cash *stock* a node is already holding, not a recurring
price re-quoted every block, and letting its real value erode under
debasement (rather than topping up its nominal number to match) is the
actual point — the same real-world reason holding cash is a bad hedge
against inflation.

A non-obvious consequence: debasement compounds by chain HEIGHT, not
wall-clock time or turns spent waiting, and a win's coins get revalued at
whatever the CURRENT (highest-so-far) debased price is the instant they're
counted — so a node that wins even occasionally is remarkably resistant to
debasement bankrupting it (its net worth's growth outpaces its accrued
bill's). The real danger is a node that goes a long stretch WITHOUT
winning while the height it shares with everyone else — including much
bigger, frequently-winning peers — races ahead regardless, debasing its
bills long before it has anything to show for them. See
`Scenarios/mining-debasement.yaml`: a tiny `HashPower: 1` node with a
`CostOfLiving` that would need ~66 turns to exhaust its `StartingCapital`
at `DebasementRatePerBlock: 0` instead goes bankrupt within its first few
turns — having never won a single block — purely because a `HashPower: 200`
peer sharing the same chain pushes height, and therefore debasement, up
fast. The big peer, with no `CostOfLiving` to erode, mines on unaffected.

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

**Editor autocomplete.** [`Scenarios/scenario.schema.json`](../Scenarios/scenario.schema.json)
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

## Per-phase fields

Fields inside `Phases`:

- `NodeGroups` — nodes to add when this phase begins, applied in order (see [Per-`NodeGroup` fields](#per-nodegroup-fields) below for each group's own fields), added on top of whatever already exists from earlier phases. Empty/omitted on phase 0 specifically falls back to the normal single-node default start; empty/omitted on any later phase just means no explicit nodes that phase.
- `AutoGrowth` (default `true`) — whether the network keeps growing organically on top of `NodeGroups`.
- `GrowthIntervalSeconds` / `GrowthRate` / `MaxNodes` — override organic growth's pace/rate/cap. `GrowthRate` is a multiplier on the current node count applied each tick (default `2.0`, doubling; `1.5` adds 50% per tick).
- `GrowthJitterSeconds` — random +/- range applied to `GrowthIntervalSeconds` each tick, so growth doesn't land on a perfectly regular schedule (default `0`, no jitter).
- `GrowthMinSeedNodes` — floor the network tops up to, one node per tick, before `GrowthRate` scaling takes over (default `0`, no floor — rate scaling applies from the first tick).
- `GrowthMaliciousFraction` / `GrowthWalletOnlyFraction` — override the role/mining-participation mix for auto-created nodes (the initial dynamic-start node, plus every node organic growth adds — not `NodeGroups`, which set `Role`/`CanMine` explicitly per group). Defaults `0.5` and `1/3`, matching the simulator's original fixed cycling. Organically-grown nodes' rules come from the top-level `DefaultRuleSchedule` (see above) — real Bitcoin's own defaults for any share it doesn't claim.
- `ChurnIntervalSeconds` / `ChurnRate` / `ChurnMinNodes` — nodes leaving the live network, growth's counterpart. `ChurnRate` is the fraction of the current node count removed each tick (default `0`, disabled); `ChurnMinNodes` is a floor churn won't shrink below (default `1`). Independent of `AutoGrowth` — can run growth and churn together, or churn alone on a fixed population.
- `OutboundPeerCount` — override how many outbound peers each node picks (default 8). See [Peer topology](mechanics.md) and `EconomicWeight` below.
- `DurationSeconds` — how long this phase lasts before the next one takes over (Enter still works too, to stop the whole run early). On the *last* phase, this instead means how long the whole run lasts before automatically shutting down; omitted there means no automatic stop. Omitted on any earlier phase means that phase — and therefore the run — never advances past it, so every non-last phase should set this.

## Per-`NodeGroup` fields

- `Count` (default `1`) — how many identically-configured nodes this group creates.
- `Role` (default `Honest`) — see [Node roles](mechanics.md#node-roles).
- `HashPower` (default `1`) — simulated hash power; see [Mining](mechanics.md).
- `CostPerAttempt` (default `0`, mining is free) — see the "Mining costs" note above. `$` cost per nonce tried; only consulted for a `ValueSeeking` group, which sits idle instead of mining any turn its best candidate doesn't clear `CostPerAttempt x HashPower`.
- `CostOfLiving` (default `0`, no living cost) — see the "Cost of living" note above. `$` fixed cost owed every turn regardless of outcome; only consulted for a `ValueSeeking` group, which is forced out of the network (churned) once accrued cost exceeds its on-chain net worth plus `StartingCapital`.
- `StartingCapital` (default `0`) — see the "Cost of living" note above. `$` runway a node starts with, on top of its on-chain balance's market value, before `CostOfLiving` can push it into insolvency.
- `HashPowerCost` (default `0`, reinvestment disabled) — see the "Reinvestment" note above. `$` cost to buy `+1 HashPower` from a `ValueSeeking` node's own earned profit.
- `MaxHashPower` (default `0`, uncapped) — see the "Reinvestment" note above. Upper bound `HashPowerCost`-driven reinvestment can grow a node's `HashPower` to.
- `CanMine` (default `true`) — see [Mining participation](mechanics.md); `false` makes this group wallet-only.
- `Pool` (default none — mines solo) — see [Mining pools](mechanics.md).
- `PoolCandidates` (default empty, reconsideration disabled) — see the "Pool adoption" note above. Names of pools this group reconsiders joining every turn; a name nobody has joined yet is valid, just empty until someone does.
- `PoolAdoptionThreshold` (default `0.5`) — see the "Pool adoption" note above. Own solo win-probability cutoff below which this group optimizes for realization (join whichever option maximizes the group's win probability) instead of expected value.
- `EconomicWeight` (default `1`) — see [Peer topology](mechanics.md).
- `RulesName` — shorthand for a single-entry `RuleSchedule` (`{ FromHeight: 0, RulesName }`): this group's consensus/economics ruleset for its whole life, by name from the scenario file's top-level `NodeRules` list (below). Omitted (or a name not defined in `NodeRules`, logged as a warning) means every field below defaults. Ignored (with a warning) if `RuleSchedule` is also set.
- `RuleSchedule` — this group's full timeline of which named ruleset (from `NodeRules`) is active at which block height, as a list of `{ FromHeight, RulesName }` entries — e.g. `real-bitcoin` from height 0, switching to a different named ruleset from height 6 on. Takes precedence over `RulesName` if both are set.
- `ValueSeeking` (default `false`) — see the "ValueSeeking" note above. Dynamically picks this group's ruleset each height by live profitability instead of a fixed `RulesName`/`RuleSchedule`. Takes precedence over both (with a warning) if set alongside either and `ValueSeekingCandidates` resolves to at least one valid entry.
- `ValueSeekingCandidates` — the explicit `NodeRules` names this group compares when `ValueSeeking` is `true`. A name not defined in `NodeRules` is a scenario-authoring mistake (logged as a warning, skipped).

## Per-`NodeRules` fields

`NodeRules` — a top-level list of named rulesets, each `{ Name, ...the 8
fields below, plus an optional PriceSchedule }`. **Every node validates an incoming block against ITS OWN
`RuleSchedule` for that block's height, never against whatever the block
itself claims** (`Block.Rules` is recorded for logging/introspection only —
see [What this is not](mechanics.md#what-this-is-not)). So two `NodeGroups` stay in
consensus with each other exactly when their resolved schedules agree at a
given height (same named ruleset, switching at the same `FromHeight`);
otherwise they diverge, same as a real hard fork. All default to real
Bitcoin's own numbers except `InitialDifficultyShift`, which deliberately
can't be:

- `Name` — how a `NodeGroups` entry's `RulesName` refers to this entry. Keep unique — a duplicate `Name` is a scenario-authoring mistake (the last one silently wins).
- `RetargetIntervalBlocks` / `TargetSecondsPerBlock` — how often (in blocks) difficulty retargets, and how long a block "should" take on average. Default `2016` / `600` (10 minutes).
- `MinAdjustmentFactor` / `MaxAdjustmentFactor` — clamp on how much a single retarget can swing the target. Default `0.25` / `4.0` (already real Bitcoin's own clamp).
- `InitialDifficultyShift` — starting difficulty (higher = harder). Default `8` — see [What this is not](mechanics.md#what-this-is-not) for why this one stays simulation-scaled. Note: every chain's actual per-block target inherits its genesis block's — which is always this same default `8`, not whatever a custom ruleset declares here — until the first real retarget (`RetargetIntervalBlocks`, default `2016`, blocks in); a non-default value here still shapes `ValueSeeking`'s profitability *ranking* between candidates (see "ValueSeeking" above) even though it won't change actual early-game mining odds until that point.
- `InitialBlockReward` / `HalvingIntervalBlocks` — coinbase reward for the first block, and how often (in blocks) it halves. Default `50` / `210000`.
- `MaxSupply` — hard cap on total coins ever minted. Default `21000000` — with the default `InitialBlockReward`/`HalvingIntervalBlocks` pair, the halving series actually converges to this asymptotically, so the cap binds for real, not just in theory.
- `PriceSchedule` — this ruleset's $-reference value over height, as a list of `{ FromHeight, Price }` entries — see the "ValueSeeking" note above. Omitted means worth $0 at every height (never picked over any priced alternative by a `ValueSeeking` node).

## Included scenarios

All in [`Scenarios/`](../Scenarios/):

| File | Demonstrates |
|---|---|
| `quick-demo.yaml` | A fast sanity check — a handful of modest miners, short duration. |
| `hash-power-disparity.yaml` | Nodes with very different simulated hash power competing for blocks. |
| `mining-pool-fairness.yaml` | A shared pool competing against solo miners, proportional reward splitting, and a synchronized `RuleSchedule` switch partway through (both groups switch together, stay in consensus). |
| `wallet-only-network.yaml` | Mining-disabled, wallet-only nodes participating normally otherwise. |
| `malicious-roles-showcase.yaml` | Each malicious node role in action (see [Node roles](mechanics.md#node-roles)) and how honest nodes catch it. |
| `large-scale-organic-growth.yaml` | A larger network growing over time. |
| `economic-hub-topology.yaml` | A few high-`EconomicWeight` hub nodes among many ordinary ones, with a small `OutboundPeerCount` so the hubs' disproportionate connectivity — and multi-hop relay — is visible. |
| `consensus-rule-switch.yaml` | Two groups start under the same rules; one switches to a different named `RuleSchedule` entry partway through and the other doesn't — a real, simulated hard fork. |
| `value-seeking-competition.yaml` | `ValueSeeking` nodes compare two rulesets' live profitability and switch sides the moment a scripted price crossover makes the other one pay more, then go idle once a price crash drops both below `CostPerAttempt`. |
| `mining-difficulty-tradeoff.yaml` | Two `ValueSeeking` groups with very different `HashPower` see the same prices/rewards but reach opposite conclusions, because expected value also weighs each candidate's `InitialDifficultyShift`. |
| `mining-bankruptcy.yaml` | Two `ValueSeeking` nodes mine the same ruleset with very different `CostOfLiving` — one survives indefinitely, the other is forced out of the network once accrued cost exceeds its net worth. |
| `mining-reinvestment.yaml` | Two identical `ValueSeeking` nodes mine the same ruleset, but one reinvests earned profit into `+HashPower`, compounding its win probability over the run. |
| `mining-pool-adoption.yaml` | A high-`HashPower` node's own odds clear `PoolAdoptionThreshold` and it mines solo; low-`HashPower` nodes join a shared pool instead, optimizing for realization over expected value. |
| `mining-debasement.yaml` | A tiny node's `CostOfLiving` would take ~66 turns to exhaust its `StartingCapital` undebased, but a big peer pushing chain height (and debasement) bankrupts it within its first few turns. |
| `bitcoin-history.yaml` | An 11-phase dramatization of Bitcoin's real history, genesis to "today" — the 2010 value-overflow incident, the 2013 v0.7/v0.8 chain split, GHash.io's brush with a hash-power majority, Mt. Gox's collapse, the block-size wars, the Bitcoin Cash and Bitcoin SV hard forks, and the shift to industrial pooled mining — with 100 nodes standing in for the whole network at every point in time. |
