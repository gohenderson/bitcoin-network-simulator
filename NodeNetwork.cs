using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // The live network: every node currently running (registry, keyed both
    // by insertion order and by id), plus the mining-participant roster —
    // one SoloMiner per non-pooled CanMine node, one PoolMiner per distinct
    // pool name — derived from each node's metadata once, at creation time,
    // in AddNodeAsync. Nothing downstream (MiningScheduler) ever has to
    // re-derive solo-vs-pooled; it only ever sees the finished IMiner list.
    //
    // Also owns the peer graph — see the "Peer topology" note in README.md.
    // Real Bitcoin nodes don't form a full mesh: each keeps a small, fixed
    // number of outbound connections, and well-run, publicly-reachable
    // nodes end up with far more inbound connections than an ordinary one
    // simply because more peers independently choose them. AddNodeAsync
    // models that at creation time: a new node draws _outboundPeerCount
    // peers via weighted random sampling (weight = EconomicWeight) from
    // whoever already exists, and the resulting edges are bidirectional.
    // SoloMiner and Node both gossip/relay only to a node's own peer set,
    // not the whole network — a node with disproportionately high
    // EconomicWeight ends up disproportionately connected, i.e. a
    // structural hub, without any special protocol role.
    //
    // Owns node creation (AddNodeAsync) and organic growth (GrowthLoopAsync).
    // Program.cs is the only caller; MiningScheduler and TransactionGenerator
    // only ever read through the snapshot accessors below.
    // ------------------------------------------------------------------
    public sealed class NodeNetwork
    {
        public const int DefaultMaxNodes = 100;
        public const int DefaultGrowthIntervalMs = 8000; // roughly double the network every 8 s — see GrowthLoopAsync
        public const double DefaultGrowthRate = 2.0; // doubles the network each tick — see GrowthLoopAsync
        public const int DefaultGrowthJitterMs = 0; // no jitter — every tick lands exactly GrowthIntervalMs apart
        public const int DefaultGrowthMinSeedNodes = 0; // no floor — growth-rate scaling applies from the first tick
        public const int DefaultOutboundPeerCount = 8; // matches real Bitcoin's default outbound connection count
        public const double DefaultMaliciousFraction = 0.5; // matches the pre-existing index%8 behavior — see AssignRole
        public const double DefaultWalletOnlyFraction = 1.0 / 3.0; // matches the pre-existing index%3 behavior — see AssignCanMine
        public const int DefaultChurnIntervalMs = 8000;
        public const double DefaultChurnRate = 0.0; // disabled — no scenario opts into churn by default
        public const int DefaultChurnMinNodes = 1; // never churn the network down to nothing

        private static readonly string[] GreekNames =
        {
            "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta",
            "iota", "kappa", "lambda", "mu", "nu", "xi", "omicron", "pi",
            "rho", "sigma", "tau", "upsilon", "phi", "chi", "psi", "omega"
        };

        // Every node gets its zero-padded join index as a prefix (e.g.
        // "000-alpha", "024-alpha") so names stay unique once the Greek
        // alphabet wraps around (index 24 reuses "alpha", etc.) instead of
        // falling back to a plain "node-N" once it runs out.
        public static string NodeNameFor(int index) =>
            $"{index:D3}-{GreekNames[index % GreekNames.Length]}";

        private static readonly NodeRole[] MaliciousRoles =
        {
            NodeRole.Equivocator, NodeRole.Impersonator, NodeRole.Corruptor, NodeRole.Withholder
        };

        // Default assignment for a brand new node with no metadata.json yet:
        // a maliciousFraction share of nodes cycle through one of each
        // malicious type in turn, the rest are honest. Scenario-configurable
        // via GrowthMaliciousFraction (default 0.5, reproducing the original
        // hardcoded index%8 behavior exactly: cycleLen = 4/0.5 = 8, first 4
        // of every 8 honest, last 4 one of each malicious type). Only used
        // the first time a given node id is created — see
        // NodeMetadataStore.LoadOrCreateAsync, which persists this so it (or a
        // hand edit on top of it) sticks across restarts.
        private static NodeRole AssignRole(int index, double maliciousFraction)
        {
            if (maliciousFraction <= 0) return NodeRole.Honest;

            var cycleLen = Math.Max(MaliciousRoles.Length, (int)Math.Round(MaliciousRoles.Length / Math.Min(maliciousFraction, 1.0)));
            var honestSlots = cycleLen - MaliciousRoles.Length;
            var pos = index % cycleLen;
            return pos < honestSlots ? NodeRole.Honest : MaliciousRoles[pos - honestSlots];
        }

        // Default mining participation for a brand new node: a
        // walletOnlyFraction share of nodes are wallet-only (fully validates,
        // gossips, sends/receives transactions, but never gets a mining
        // turn), so a fresh run shows a mix without any manual edits.
        // Scenario-configurable via GrowthWalletOnlyFraction (default 1/3,
        // reproducing the original hardcoded index%3 behavior exactly:
        // cycleLen = round(1 / (1/3)) = 3, every 3rd node wallet-only). Same
        // override rules as AssignRole — see NodeMetadataStore.LoadOrCreateAsync
        // and the "Mining participation" note in README.md.
        private static bool AssignCanMine(int index, double walletOnlyFraction)
        {
            if (walletOnlyFraction <= 0) return true;

            var cycleLen = Math.Max(1, (int)Math.Round(1.0 / Math.Min(walletOnlyFraction, 1.0)));
            return index % cycleLen != cycleLen - 1;
        }

        private readonly string _runRootDir;
        private readonly int _port;
        // Node-creation defaults for whichever phase is currently active —
        // see SetNodeCreationSettings. Not readonly: a multi-phase scenario
        // (see "Scenarios" in README.md) can change these between phases,
        // e.g. modeling outbound connectivity or the malicious/wallet-only
        // mix shifting over a network's simulated history. Guarded by
        // _lock, same as the rest of this class's mutable state.
        private int _outboundPeerCount;
        private double _maliciousFraction;
        private double _walletOnlyFraction;
        // What RuleSchedule a brand-new organically-grown node gets — see
        // ScenarioFile.DefaultRuleSchedule's own comment for the
        // tranche/option semantics and PickDefaultRuleSchedule below for the
        // implementation. Whole-run, not phase-mutable like the three
        // fields above (ScenarioFile.DefaultRuleSchedule lives at the file
        // root, not per-phase), so this one IS readonly.
        private readonly List<ResolvedDefaultRuleScheduleEntry> _defaultRuleSchedule;
        private readonly Random _rng = new();
        private readonly Random _peerSelectionRng = new(); // dedicated so peer selection (only ever called from AddNodeAsync's sequential flow) never shares a Random with concurrently-running mining code
        private readonly Random _growthTimingRng = new(); // dedicated so growth-tick jitter never shares a Random with concurrently-running mining/peer-selection code
        private readonly Random _churnRng = new(); // dedicated so churn's node selection never shares a Random with concurrently-running mining/peer-selection/growth-timing code
        private readonly Random _defaultRuleScheduleRng = new(); // dedicated so a new organic node's rules pick never shares a Random with concurrently-running mining/peer-selection/growth-timing/churn code

        // All mutable registry state below is guarded by this one lock —
        // nodes call the getters at broadcast time so newly joined peers are
        // automatically included without any wiring.
        private readonly object _lock = new();
        private int _nextJoinIndex = 0; // ever-increasing — see AddNodeAsync's comment on why churn makes _allNodeIds.Count unsafe to reuse for this
        private readonly List<string> _allNodeIds = new();
        private readonly List<Node> _allNodes = new();
        private readonly Dictionary<string, Node> _nodesById = new();
        private readonly List<Task> _persistTasks = new();
        private readonly List<BlockchainStore> _blockchainStores = new();
        private readonly List<IMiner> _allMiners = new();
        private readonly Dictionary<string, PoolMiner> _poolMinersByName = new();

        // Peer-graph state — see the "Peer topology" note in README.md.
        // _peerIdsByNodeId holds bidirectional edges: when node A picks node
        // B as one of its outbound peers, both A's and B's sets gain each
        // other, mirroring a real, once-open TCP connection relaying in both
        // directions. _economicWeightByNodeId is a node's own weight,
        // recorded at creation so later-joining nodes can weight their own
        // outbound picks against it.
        private readonly Dictionary<string, HashSet<string>> _peerIdsByNodeId = new();
        private readonly Dictionary<string, int> _economicWeightByNodeId = new();

        public NodeNetwork(string runRootDir, int port, int outboundPeerCount, double maliciousFraction, double walletOnlyFraction, List<ResolvedDefaultRuleScheduleEntry> defaultRuleSchedule)
        {
            _runRootDir = runRootDir;
            _port = port;
            _outboundPeerCount = outboundPeerCount;
            _maliciousFraction = maliciousFraction;
            _walletOnlyFraction = walletOnlyFraction;
            _defaultRuleSchedule = defaultRuleSchedule;
        }

        // Called by Program at each phase transition (see "Scenarios" in
        // README.md) to change what AddNodeAsync uses for nodes it creates
        // from here on — organically-grown nodes and phase 0's single-node
        // default start via _maliciousFraction/_walletOnlyFraction, every
        // node's peer count via _outboundPeerCount. NodeGroups-authored
        // nodes are unaffected (they always use their own Role/CanMine).
        public void SetNodeCreationSettings(int outboundPeerCount, double maliciousFraction, double walletOnlyFraction)
        {
            lock (_lock)
            {
                _outboundPeerCount = outboundPeerCount;
                _maliciousFraction = maliciousFraction;
                _walletOnlyFraction = walletOnlyFraction;
            }
        }

        public List<string> GetAllNodeIds() { lock (_lock) { return new List<string>(_allNodeIds); } }

        // Backs NetworkServer's request dispatch — resolves a URL path's
        // node-id segment to the live Node that should handle it, or null
        // for an unknown id (404).
        public Node? ResolveNode(string id) { lock (_lock) { return _nodesById.TryGetValue(id, out var node) ? node : null; } }

        // The tip hash MiningScheduler watches to detect a new block (and
        // reshuffle mining turn order) — null once the network has no nodes
        // yet, which only happens before the very first AddNodeAsync call.
        public string? CurrentTipHash() { lock (_lock) { return _allNodes.Count > 0 ? _allNodes[0].Chain.Latest.Hash : null; } }

        public List<IMiner> SnapshotMiners() { lock (_lock) { return new List<IMiner>(_allMiners); } }

        // This node's current outbound-and-inbound peer set — see the "Peer
        // topology" note in README.md. Handed to both SoloMiner (as its
        // gossip broadcast target) and Node (as its relay target) at
        // creation time; both always see the same peer set for a given node
        // since it's the one underlying registry entry.
        private List<string> PeerIdsFor(string nodeId) { lock (_lock) { return _peerIdsByNodeId.TryGetValue(nodeId, out var set) ? new List<string>(set) : new List<string>(); } }

        public List<Task> SnapshotPersistTasks() { lock (_lock) { return new List<Task>(_persistTasks); } }

        public List<BlockchainStore> SnapshotBlockchainStores() { lock (_lock) { return new List<BlockchainStore>(_blockchainStores); } }

        // "Where does this node's stuff live" and its metadata.json
        // load/save/apply logic live in Node.cs's NodeMetadataStore, next to
        // NodeMetadata itself — this just forwards RunRootDir so callers here
        // don't need to know that.
        private string NodeDirFor(string nodeId) =>
            NodeMetadataStore.NodeDirFor(_runRootDir, nodeId);

        private string BlockchainDbPathFor(string nodeId) =>
            Path.Combine(NodeDirFor(nodeId), "blockchain.db");

        public async Task AddNodeAsync(ChainWatcher watcher, CancellationToken token, ScenarioNodeGroup? group = null)
        {
            int index;
            List<string> existingIds;
            Dictionary<string, int> existingWeights;
            int outboundPeerCount;
            double maliciousFraction;
            double walletOnlyFraction;
            int currentHeight;
            lock (_lock)
            {
                // _nextJoinIndex, not _allNodeIds.Count: once churn can remove
                // nodes, the live count shrinks, and reusing it here would
                // hand a departed node's index — and therefore its id, disk
                // directory, signing key, and blockchain.db — to a supposedly
                // brand-new node, including two concurrent BlockchainStore
                // instances writing the same file (the departed node's
                // still-running PersistenceLoop, plus the new one). A
                // dedicated counter that only ever increments guarantees
                // every id is used exactly once for the lifetime of the run.
                index = _nextJoinIndex++;
                existingIds = new List<string>(_allNodeIds);
                existingWeights = new Dictionary<string, int>(_economicWeightByNodeId);
                // Snapshotting these under the same lock as the settings
                // themselves' writer (SetNodeCreationSettings) means a phase
                // transition landing mid-call always resolves to one phase's
                // settings or the other, never a torn mix of both.
                outboundPeerCount = _outboundPeerCount;
                maliciousFraction = _maliciousFraction;
                walletOnlyFraction = _walletOnlyFraction;
                // A representative chain height for DefaultRuleSchedule's
                // FromHeight gating below — see PickDefaultRuleScheduleEntry.
                // _allNodes[0] is an arbitrary-but-consistent stand-in (same
                // pattern as CurrentTipHash()); individual nodes' own tips
                // can differ slightly during an active fork, but this only
                // needs to be "close enough" to decide which DefaultRuleSchedule
                // entries have activated yet, not exact.
                currentHeight = _allNodes.Count > 0 ? _allNodes[0].Chain.Latest.Index : 0;
            }

            var id = NodeNameFor(index);

            Directory.CreateDirectory(NodeDirFor(id));
            var metadata = group != null
                ? await NodeMetadataStore.LoadOrCreateFromGroupAsync(_runRootDir, id, group)
                : await NodeMetadataStore.LoadOrCreateAsync(_runRootDir, id, AssignRole(index, maliciousFraction), AssignCanMine(index, walletOnlyFraction), PickDefaultRuleSchedule(currentHeight));

            // Picked from every node that exists so far (this node's own id
            // isn't in existingIds yet) — see the "Peer topology" note in
            // README.md. A node born with no peers yet (e.g. the very first
            // one) simply starts isolated and bootstraps connectivity as
            // later-joining nodes independently pick it.
            var outboundPeers = ChooseWeightedPeers(existingIds, existingWeights, outboundPeerCount, _peerSelectionRng);

            lock (_lock)
            {
                _allNodeIds.Add(id);
                _economicWeightByNodeId[id] = metadata.EconomicWeight;
                if (!_peerIdsByNodeId.TryGetValue(id, out var mySet))
                    _peerIdsByNodeId[id] = mySet = new HashSet<string>();

                foreach (var peerId in outboundPeers)
                {
                    mySet.Add(peerId);
                    if (!_peerIdsByNodeId.TryGetValue(peerId, out var peerSet))
                        _peerIdsByNodeId[peerId] = peerSet = new HashSet<string>();
                    peerSet.Add(id);
                }
            }
            watcher.AddNode(id);

            // Composition root: Chain and Mempool are constructed once here and
            // shared between Node (which serves them over HTTP) and SoloMiner
            // (which reads/mutates them while mining) — see the comments atop
            // Node.cs and Miner.cs for why SoloMiner takes these directly
            // instead of holding a reference back to the Node it mines for.
            // ruleSchedule is likewise shared between the two: Blockchain
            // uses it to validate incoming blocks against what THIS node
            // currently expects at a given height, and SoloMiner uses the
            // exact same lookup to decide what to build under — see
            // RuleSchedule's own comment in Blockchain.cs.
            // ValueSeeking mode: metadata.ValueSeekingCandidates non-empty means this
            // node dynamically picks its ruleset by live profitability each height
            // instead of following a fixed timeline — see RuleSchedule's
            // value-seeking constructor in Blockchain.cs. Same shared-instance wiring
            // either way: Blockchain (validation) and SoloMiner (building) both get
            // this one RuleSchedule object.
            var ruleSchedule = metadata.ValueSeekingCandidates.Count > 0
                ? new RuleSchedule(metadata.ValueSeekingCandidates, metadata.HashPower)
                : new RuleSchedule(metadata.RuleSchedule);
            var chain = new Blockchain(ruleSchedule);
            var mempool = new ConcurrentQueue<Transaction>();
            var signingKey = NodeMetadataStore.ImportSigningKey(metadata.SigningKey!);
            Func<List<string>> getPeerIds = () => PeerIdsFor(id);
            Action<string> discouragePeer = peerId => DiscouragePeer(id, peerId);
            // Lets a SoloMiner remove ITSELF from the network on insolvency — reuses
            // the exact same RemoveNode churn already used by ChurnLoopAsync, just
            // triggered by this node's own economics instead of the random churn
            // tick. Safe to call mid-turn: RemoveNode is idempotent (no-ops if the
            // node is already gone) and only mutates NodeNetwork's own in-memory
            // registries under its own lock — it doesn't affect the scheduler's
            // already-in-flight iteration, which will simply stop seeing this miner
            // on its next fresh SnapshotMiners() call.
            Action requestForcedChurn = () => RemoveNode(id, watcher);
            var soloMiner = new SoloMiner(id, _port, metadata.NodeRole, metadata.HashPower, metadata.CostPerAttempt, metadata.CostOfLiving, metadata.StartingCapital, requestForcedChurn, ruleSchedule, chain, mempool, getPeerIds, watcher, signingKey);
            var node = new Node(id, chain, mempool, watcher, _port, getPeerIds, discouragePeer);
            var blockchainStore = new BlockchainStore(BlockchainDbPathFor(id));
            PersistenceLoop.ResumeFromDisk(node, blockchainStore);

            lock (_lock)
            {
                _allNodes.Add(node);
                _nodesById[id] = node;
                _blockchainStores.Add(blockchainStore);
                _persistTasks.Add(PersistenceLoop.RunAsync(node, blockchainStore, token));

                // Deciding solo vs. pooled — and, for pools, finding or
                // creating that pool's PoolMiner — happens exactly once, right
                // here at creation time. Nothing downstream (MiningScheduler)
                // ever has to re-derive it. Wallet-only nodes contribute no
                // miner at all; malicious roles always mine solo even if a
                // Pool value is set.
                if (metadata.CanMine)
                {
                    if (metadata.NodeRole == NodeRole.Honest && !string.IsNullOrEmpty(metadata.Pool))
                    {
                        if (_poolMinersByName.TryGetValue(metadata.Pool, out var pool))
                            pool.AddMember(soloMiner);
                        else
                        {
                            var newPool = new PoolMiner(metadata.Pool, new[] { soloMiner }, _rng);
                            _poolMinersByName[metadata.Pool] = newPool;
                            _allMiners.Add(newPool);
                        }
                    }
                    else
                    {
                        _allMiners.Add(soloMiner);
                    }
                }
            }

            var peerCountSoFar = PeerIdsFor(id).Count; // outbound picks now, plus any earlier node that already picked this one back — grows further as later nodes join
            Console.WriteLine($"[network] node #{index} ({id}, {metadata.NodeRole}, hashPower={metadata.HashPower}, canMine={metadata.CanMine}, pool={metadata.Pool ?? "(solo)"}, economicWeight={metadata.EconomicWeight}) joined at /{id}/ — {outboundPeers.Count} outbound peer(s), {peerCountSoFar} peer(s) total so far — total: {index + 1}");
        }

        // Weighted random sampling WITHOUT replacement: draws up to `count`
        // distinct ids from `candidateIds`, each draw weighted by that
        // candidate's entry in `weights` (defaulting to 1 if somehow
        // missing). Same weighted-draw shape as PoolMiner.WeightedRandomMember,
        // just repeated with the chosen candidate removed from the pool each
        // time so a node never picks the same peer twice. Returns fewer than
        // `count` ids once candidateIds is exhausted — expected for
        // early-joining nodes when few others exist yet.
        private static List<string> ChooseWeightedPeers(List<string> candidateIds, Dictionary<string, int> weights, int count, Random rng)
        {
            var pool = new List<string>(candidateIds);
            var chosen = new List<string>();
            var take = Math.Min(count, pool.Count);

            for (var i = 0; i < take; i++)
            {
                var totalWeight = pool.Sum(c => weights.GetValueOrDefault(c, 1));
                var roll = rng.Next(totalWeight);
                var cumulative = 0;
                var picked = pool[^1];
                foreach (var candidate in pool)
                {
                    cumulative += weights.GetValueOrDefault(candidate, 1);
                    if (roll < cumulative) { picked = candidate; break; }
                }
                chosen.Add(picked);
                pool.Remove(picked);
            }

            return chosen;
        }

        // Picks, for a brand-new organically-grown node, the single-entry
        // RuleSchedule it should get (or an empty list for hardcoded
        // defaults — the same thing an empty schedule already means, see
        // RuleSchedule.RulesForHeight in Blockchain.cs) — see
        // ScenarioFile.DefaultRuleSchedule's own comment in Scenario.cs for
        // the tranche/option semantics this implements. `height` is the
        // representative chain height AddNodeAsync snapshotted, gating
        // which tranche has activated yet.
        private List<RuleScheduleEntry> PickDefaultRuleSchedule(int height)
        {
            // The latest-activated tranche wins outright — it isn't merged
            // with any earlier tranche. Ties (two tranches sharing a
            // FromHeight) resolve to the last-declared one, same as a
            // duplicate NodeRules Name elsewhere.
            var tranche = _defaultRuleSchedule
                .Where(e => e.FromHeight <= height)
                .OrderBy(e => e.FromHeight)
                .LastOrDefault();
            if (tranche == null || tranche.RuleSchedules.Count == 0) return new List<RuleScheduleEntry>();

            var roll = _defaultRuleScheduleRng.NextDouble() * 100.0;
            var cumulative = 0.0;
            foreach (var option in tranche.RuleSchedules)
            {
                cumulative += option.Percent;
                if (roll < cumulative)
                    return new List<RuleScheduleEntry> { new RuleScheduleEntry { FromHeight = 0, Rules = option.Rules } };
            }
            return new List<RuleScheduleEntry>(); // unclaimed remainder — hardcoded defaults
        }

        // Exponential growth, not linear: each tick, the network grows by
        // growthRate applied to however many nodes already exist — e.g.
        // growthRate 2.0 (the default) adds as many new nodes as already
        // exist, a network effect where the bigger it already is, the faster
        // it grows, rather than a fixed trickle of one at a time — capped so
        // the total never exceeds maxNodes. Ceiling'd so a fractional rate
        // still makes forward progress on a small network instead of
        // rounding down to zero added nodes forever. New nodes are added one
        // at a time (sequential awaits) so each gets a clean,
        // atomically-assigned index/port.
        //
        // Below growthMinSeedNodes, growth-rate scaling is skipped entirely
        // in favor of a flat one-node-per-tick top-up, so a scenario that
        // wants (say) a guaranteed 20-node base before compounding kicks in
        // doesn't have to fight a doubling curve that's still tiny at first.
        //
        // Each tick's delay is growthIntervalMs +/- a random draw up to
        // growthJitterMs (clamped so the delay itself never goes negative),
        // so ticks don't land on a perfectly regular schedule.
        //
        // maxNodes/growthIntervalMs/growthRate/growthJitterMs/growthMinSeedNodes
        // default to NodeNetwork's Default* constants but can be overridden
        // by a scenario's MaxNodes/GrowthIntervalSeconds/GrowthRate/
        // GrowthJitterSeconds/GrowthMinSeedNodes — see "Scenarios" in
        // README.md.
        public async Task GrowthLoopAsync(ChainWatcher watcher, CancellationToken token, int maxNodes, int growthIntervalMs, double growthRate, int growthJitterMs, int growthMinSeedNodes)
        {
            while (!token.IsCancellationRequested)
            {
                var jitter = growthJitterMs > 0 ? _growthTimingRng.Next(-growthJitterMs, growthJitterMs + 1) : 0;
                var delayMs = Math.Max(0, growthIntervalMs + jitter);
                try { await Task.Delay(delayMs, token); }
                catch (OperationCanceledException) { break; }

                int count;
                lock (_lock) { count = _allNodes.Count; }
                if (count >= maxNodes) break;

                var toAdd = count < growthMinSeedNodes
                    ? Math.Min(growthMinSeedNodes - count, maxNodes - count)
                    : Math.Min((int)Math.Ceiling(count * Math.Max(0.0, growthRate - 1.0)), maxNodes - count);
                for (int i = 0; i < toAdd; i++)
                    await AddNodeAsync(watcher, token);
            }
        }

        // Node churn: each tick, removes a churnRate share of the current
        // node count (floored, so a low rate on a small network simply skips
        // removal that tick rather than aggressively shrinking it), never
        // dropping below churnMinNodes. Candidates are picked uniformly at
        // random from every live node — solo, wallet-only, and pool members
        // alike are all safe to remove (see RemoveNode); there's no need to
        // special-case a pool's last member since RemoveNode tears the pool
        // down cleanly when that happens. churnIntervalMs/churnRate/
        // churnMinNodes default to NodeNetwork's Default* constants but can
        // be overridden by a scenario's ChurnIntervalSeconds/ChurnRate/
        // ChurnMinNodes — see "Scenarios" in README.md.
        public async Task ChurnLoopAsync(ChainWatcher watcher, CancellationToken token, int churnIntervalMs, double churnRate, int churnMinNodes)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(churnIntervalMs, token); }
                catch (OperationCanceledException) { break; }

                List<string> toRemove;
                lock (_lock)
                {
                    var count = _allNodeIds.Count;
                    var removable = Math.Max(0, count - churnMinNodes);
                    var n = Math.Min(removable, (int)Math.Floor(count * churnRate));

                    toRemove = new List<string>();
                    if (n > 0)
                    {
                        var pool = new List<string>(_allNodeIds);
                        for (var i = 0; i < n; i++)
                        {
                            var idx = _churnRng.Next(pool.Count);
                            toRemove.Add(pool[idx]);
                            pool.RemoveAt(idx);
                        }
                    }
                }

                foreach (var id in toRemove)
                    RemoveNode(id, watcher);
            }
        }

        // The inverse of AddNodeAsync — see the "Node churn" note above.
        // Synchronous: every structure it touches is in-memory (registry,
        // peer graph, miner roster); nothing here needs to await. Two things
        // deliberately keep running unsignaled after removal, since neither
        // touches the registry, peer graph, or miner roster, and both are
        // harmless (arguably desirable — they preserve the departed node's
        // final state) to leave going until the whole run's CancellationToken
        // fires: its PersistenceLoop task (still in _persistTasks) and its
        // BlockchainStore (still in _blockchainStores).
        public void RemoveNode(string nodeId, ChainWatcher watcher)
        {
            lock (_lock)
            {
                if (!_nodesById.Remove(nodeId)) return;

                _allNodes.RemoveAll(n => n.Id == nodeId);
                _allNodeIds.Remove(nodeId);
                _economicWeightByNodeId.Remove(nodeId);

                // Bidirectional — see the "Peer topology" note in README.md:
                // drop this node's own edge set, and this node's id out of
                // every peer that had picked it too.
                if (_peerIdsByNodeId.TryGetValue(nodeId, out var mySet))
                {
                    foreach (var peerId in mySet)
                        if (_peerIdsByNodeId.TryGetValue(peerId, out var peerSet))
                            peerSet.Remove(nodeId);
                }
                _peerIdsByNodeId.Remove(nodeId);

                _allMiners.RemoveAll(m => m is SoloMiner sm && sm.Id == nodeId);
                foreach (var (poolName, pool) in _poolMinersByName.ToList())
                {
                    if (pool.RemoveMemberIfPresent(nodeId) && pool.MemberCount == 0)
                    {
                        _poolMinersByName.Remove(poolName);
                        _allMiners.Remove(pool);
                    }
                }
            }
            watcher.RemoveNode(nodeId);
            Console.WriteLine($"[network] node {nodeId} left (churn)");
        }

        // Peer discouragement (see the "Peer discouragement" note in
        // README.md): unlike RemoveNode, this is deliberately one-directional
        // — it only drops peerId out of nodeId's own edge set, mirroring a
        // real node that stops dialing/relaying to a peer it has discouraged
        // without that peer necessarily knowing it's been dropped. The peer's
        // own edge set (and therefore its future outbound attempts toward
        // nodeId) is untouched; Node's receive handlers refuse those directly.
        public void DiscouragePeer(string nodeId, string peerId)
        {
            lock (_lock)
            {
                if (_peerIdsByNodeId.TryGetValue(nodeId, out var mySet))
                    mySet.Remove(peerId);
            }
        }
    }
}
