using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    /// <summary>
    /// The live network: every node currently running, plus the mining-participant roster —
    /// one <see cref="SoloMiner"/> per non-pooled mining-capable node, one
    /// <see cref="PoolMiner"/> per distinct pool name — derived from each node's metadata once,
    /// at creation time, in <see cref="AddNodeAsync"/>. Also owns the peer graph: a new node
    /// draws a fixed number of outbound peers via weighted random sampling (weight =
    /// economic weight) from whoever already exists, and the resulting edges are
    /// bidirectional, so a node with disproportionately high economic weight ends up as a
    /// structural hub. Owns node creation and organic growth/churn.
    /// </summary>
    public sealed class NodeNetwork
    {
        public const int DefaultMaxNodes = 100;
        public const int DefaultGrowthIntervalMs = 8000;
        public const double DefaultGrowthRate = 2.0;
        public const int DefaultGrowthJitterMs = 0;
        public const int DefaultGrowthMinSeedNodes = 0;
        public const int DefaultOutboundPeerCount = 8;
        public const double DefaultMaliciousFraction = 0.5;
        public const double DefaultWalletOnlyFraction = 1.0 / 3.0;
        public const int DefaultChurnIntervalMs = 8000;
        public const double DefaultChurnRate = 0.0;
        public const int DefaultChurnMinNodes = 1;

        private static readonly string[] GreekNames =
        {
            "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta",
            "iota", "kappa", "lambda", "mu", "nu", "xi", "omicron", "pi",
            "rho", "sigma", "tau", "upsilon", "phi", "chi", "psi", "omega"
        };

        /// <summary>
        /// Every node gets its zero-padded join index as a prefix (e.g. "000-alpha",
        /// "024-alpha") so names stay unique once the Greek alphabet wraps around.
        /// </summary>
        public static string NodeNameFor(int index) =>
            $"{index:D3}-{GreekNames[index % GreekNames.Length]}";

        private static readonly NodeRole[] MaliciousRoles =
        {
            NodeRole.Equivocator, NodeRole.Impersonator, NodeRole.Corruptor, NodeRole.Withholder
        };

        /// <summary>
        /// Default role assignment for a brand new node with no <c>metadata.json</c> yet: a
        /// <paramref name="maliciousFraction"/> share of nodes cycle through one of each
        /// malicious type in turn, the rest are honest.
        /// </summary>
        private static NodeRole PickDefaultRole(int index, double maliciousFraction)
        {
            if (maliciousFraction <= 0) return NodeRole.Honest;

            var cycleLen = Math.Max(MaliciousRoles.Length, (int)Math.Round(MaliciousRoles.Length / Math.Min(maliciousFraction, 1.0)));
            var honestSlots = cycleLen - MaliciousRoles.Length;
            var pos = index % cycleLen;
            return pos < honestSlots ? NodeRole.Honest : MaliciousRoles[pos - honestSlots];
        }

        /// <summary>
        /// Default mining participation for a brand new node: a
        /// <paramref name="walletOnlyFraction"/> share of nodes are wallet-only (fully
        /// validates, gossips, sends/receives transactions, but never gets a mining turn).
        /// </summary>
        private static bool PickDefaultCanMine(int index, double walletOnlyFraction)
        {
            if (walletOnlyFraction <= 0) return true;

            var cycleLen = Math.Max(1, (int)Math.Round(1.0 / Math.Min(walletOnlyFraction, 1.0)));
            return index % cycleLen != cycleLen - 1;
        }

        private readonly string _runRootDir;
        private int _outboundPeerCount;
        private double _maliciousFraction;
        private double _walletOnlyFraction;
        private readonly List<ResolvedDefaultRuleScheduleEntry> _defaultRuleSchedule;
        private readonly decimal _debasementRatePerBlock;
        private readonly Random _rng = new();
        private readonly Random _peerSelectionRng = new();
        private readonly Random _growthTimingRng = new();
        private readonly Random _churnRng = new();
        private readonly Random _defaultRuleScheduleRng = new();

        private readonly object _lock = new();
        private int _nextJoinIndex = 0;
        private readonly List<string> _allNodeIds = new();
        private readonly List<Node> _allNodes = new();
        private readonly Dictionary<string, Node> _nodesById = new();
        private readonly List<Task> _persistTasks = new();
        private readonly List<BlockchainStore> _blockchainStores = new();
        private readonly List<IMiner> _allMiners = new();
        private readonly Dictionary<string, PoolMiner> _poolMinersByName = new();

        private readonly Dictionary<string, HashSet<string>> _peerIdsByNodeId = new();
        private readonly Dictionary<string, int> _economicWeightByNodeId = new();

        private readonly Dictionary<string, NodeRole> _roleByNodeId = new();
        private readonly Dictionary<string, bool> _canMineByNodeId = new();

        public NodeNetwork(string runRootDir, int outboundPeerCount, double maliciousFraction, double walletOnlyFraction, List<ResolvedDefaultRuleScheduleEntry> defaultRuleSchedule, decimal debasementRatePerBlock)
        {
            _runRootDir = runRootDir;
            _outboundPeerCount = outboundPeerCount;
            _maliciousFraction = maliciousFraction;
            _walletOnlyFraction = walletOnlyFraction;
            _defaultRuleSchedule = defaultRuleSchedule;
            _debasementRatePerBlock = debasementRatePerBlock;
        }

        /// <summary>
        /// Called at each scenario phase transition to change what <see cref="AddNodeAsync"/>
        /// uses for nodes it creates from here on. NodeGroups-authored nodes are unaffected —
        /// they always use their own role/mining settings.
        /// </summary>
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

        /// <summary>Resolves a URL path's node-id segment to the live <see cref="Node"/> that should handle it, or null for an unknown id.</summary>
        public Node? ResolveNode(string id) { lock (_lock) { return _nodesById.TryGetValue(id, out var node) ? node : null; } }

        /// <summary>
        /// Reaches any node in this network without a real HTTP round trip — the same
        /// (method, route, senderId, body) shape a real HTTP request would carry, and the
        /// same (statusCode, body) shape its response would carry.
        /// </summary>
        public delegate Task<(int StatusCode, string Body)> InternalDispatchFunc(string targetNodeId, string method, string route, string? senderId, string? body);

        public async Task<(int StatusCode, string Body)> DispatchInternalAsync(string targetNodeId, string method, string route, string? senderId, string? body)
        {
            var node = ResolveNode(targetNodeId);
            if (node == null)
                return (404, $"{{\"error\":\"unknown node id '{targetNodeId}'\"}}");
            return await node.HandleAsync(method, route, senderId, body);
        }

        /// <summary>The tip hash <see cref="MiningScheduler"/> watches to detect a new block, or null before the first node has joined.</summary>
        public string? CurrentTipHash() { lock (_lock) { return _allNodes.Count > 0 ? _allNodes[0].Chain.Latest.Hash : null; } }

        public List<IMiner> SnapshotMiners() { lock (_lock) { return new List<IMiner>(_allMiners); } }

        /// <summary>Point-in-time view of every live node's participation/influence stats, assembled fresh on demand for the dashboard.</summary>
        public NetworkSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                var hashPowerByMinerId = new Dictionary<string, int>();
                var poolByMemberId = new Dictionary<string, string>();
                var pools = new List<PoolSummary>();

                foreach (var miner in _allMiners)
                {
                    if (miner is PoolMiner pool)
                    {
                        pools.Add(new PoolSummary { Name = pool.Label, MemberCount = pool.MemberCount, TotalHashPower = pool.TotalHashPower });
                        foreach (var member in pool.Members)
                        {
                            hashPowerByMinerId[member.Id] = member.HashPower;
                            poolByMemberId[member.Id] = pool.Label;
                        }
                    }
                    else if (miner is SoloMiner solo)
                    {
                        hashPowerByMinerId[solo.Id] = solo.HashPower;
                    }
                }

                var nodes = _allNodeIds.Select(id => new NodeSummary
                {
                    Id = id,
                    Role = _roleByNodeId.GetValueOrDefault(id, NodeRole.Honest),
                    CanMine = _canMineByNodeId.GetValueOrDefault(id, false),
                    HashPower = hashPowerByMinerId.GetValueOrDefault(id, 0),
                    Pool = poolByMemberId.GetValueOrDefault(id),
                    EconomicWeight = _economicWeightByNodeId.GetValueOrDefault(id, 1),
                    PeerCount = _peerIdsByNodeId.TryGetValue(id, out var peers) ? peers.Count : 0
                }).ToList();

                return new NetworkSnapshot { Nodes = nodes, Pools = pools };
            }
        }

        private List<string> PeerIdsFor(string nodeId) { lock (_lock) { return _peerIdsByNodeId.TryGetValue(nodeId, out var set) ? new List<string>(set) : new List<string>(); } }

        public List<Task> SnapshotPersistTasks() { lock (_lock) { return new List<Task>(_persistTasks); } }

        public List<BlockchainStore> SnapshotBlockchainStores() { lock (_lock) { return new List<BlockchainStore>(_blockchainStores); } }

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
                index = _nextJoinIndex++;
                existingIds = new List<string>(_allNodeIds);
                existingWeights = new Dictionary<string, int>(_economicWeightByNodeId);
                outboundPeerCount = _outboundPeerCount;
                maliciousFraction = _maliciousFraction;
                walletOnlyFraction = _walletOnlyFraction;
                currentHeight = _allNodes.Count > 0 ? _allNodes[0].Chain.Latest.Index : 0;
            }

            var id = NodeNameFor(index);

            Directory.CreateDirectory(NodeDirFor(id));
            var metadata = group != null
                ? await NodeMetadataStore.LoadOrCreateFromGroupAsync(_runRootDir, id, group)
                : await NodeMetadataStore.LoadOrCreateAsync(_runRootDir, id, PickDefaultRole(index, maliciousFraction), PickDefaultCanMine(index, walletOnlyFraction), PickDefaultRuleSchedule(currentHeight));

            var outboundPeers = ChooseWeightedPeers(existingIds, existingWeights, outboundPeerCount, _peerSelectionRng);

            lock (_lock)
            {
                _allNodeIds.Add(id);
                _economicWeightByNodeId[id] = metadata.EconomicWeight;
                _roleByNodeId[id] = metadata.NodeRole;
                _canMineByNodeId[id] = metadata.CanMine;
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

            var ruleSchedule = metadata.ValueSeekingCandidates.Count > 0
                ? new RuleSchedule(metadata.ValueSeekingCandidates, metadata.HashPower, _debasementRatePerBlock)
                : new RuleSchedule(metadata.RuleSchedule, _debasementRatePerBlock);
            var chain = new Blockchain(ruleSchedule);
            var mempool = new ConcurrentQueue<Transaction>();
            var signingKey = NodeMetadataStore.ImportSigningKey(metadata.SigningKey!);
            Func<List<string>> getPeerIds = () => PeerIdsFor(id);
            Action<string> discouragePeer = peerId => DiscouragePeer(id, peerId);
            Action requestForcedChurn = () => RemoveNode(id, watcher);
            Action<string?> requestPoolSwitch = newPool => SwitchPoolMembership(id, newPool);
            Func<string, int> getPoolHashPower = poolName => GetPoolHashPower(poolName);
            var soloMiner = new SoloMiner(id, DispatchInternalAsync, metadata.NodeRole, metadata.HashPower, metadata.CostPerAttempt, metadata.CostOfLiving, metadata.StartingCapital, requestForcedChurn, metadata.HashPowerCost, metadata.MaxHashPower, metadata.PoolCandidates, metadata.PoolAdoptionThreshold, metadata.Pool, getPoolHashPower, requestPoolSwitch, ruleSchedule, chain, mempool, getPeerIds, watcher, signingKey);
            var node = new Node(id, chain, mempool, watcher, DispatchInternalAsync, getPeerIds, discouragePeer);
            var blockchainStore = new BlockchainStore(BlockchainDbPathFor(id));
            PersistenceLoop.ResumeFromDisk(node, blockchainStore);

            lock (_lock)
            {
                _allNodes.Add(node);
                _nodesById[id] = node;
                _blockchainStores.Add(blockchainStore);
                _persistTasks.Add(PersistenceLoop.RunAsync(node, blockchainStore, token));

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

            var peerCountSoFar = PeerIdsFor(id).Count;
            Console.WriteLine($"[network] node #{index} ({id}, {metadata.NodeRole}, hashPower={metadata.HashPower}, canMine={metadata.CanMine}, pool={metadata.Pool ?? "(solo)"}, economicWeight={metadata.EconomicWeight}) joined at /{id}/ — {outboundPeers.Count} outbound peer(s), {peerCountSoFar} peer(s) total so far — total: {index + 1}");
        }

        /// <summary>
        /// Weighted random sampling without replacement: draws up to <paramref name="count"/>
        /// distinct ids from <paramref name="candidateIds"/>, each draw weighted by that
        /// candidate's entry in <paramref name="weights"/> (defaulting to 1 if missing).
        /// Returns fewer than <paramref name="count"/> ids once candidateIds is exhausted.
        /// </summary>
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

        /// <summary>
        /// Picks, for a brand-new organically-grown node, the single-entry
        /// <see cref="RuleSchedule"/> it should get (or an empty list for hardcoded
        /// defaults). The latest-activated tranche wins outright — it isn't merged with any
        /// earlier tranche; a tie resolves to the last-declared one.
        /// </summary>
        private List<RuleScheduleEntry> PickDefaultRuleSchedule(int height)
        {
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
                    return new List<RuleScheduleEntry> { new RuleScheduleEntry { FromHeight = 0, Rules = option.Rules, Name = option.Name } };
            }
            return new List<RuleScheduleEntry>();
        }

        /// <summary>
        /// Exponential, not linear, growth: each tick, the network grows by
        /// <paramref name="growthRate"/> applied to however many nodes already exist, capped
        /// so the total never exceeds <paramref name="maxNodes"/>. Below
        /// <paramref name="growthMinSeedNodes"/>, growth-rate scaling is skipped in favor of a
        /// flat one-node-per-tick top-up. Each tick's delay is
        /// <paramref name="growthIntervalMs"/> +/- a random draw up to
        /// <paramref name="growthJitterMs"/>.
        /// </summary>
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

        /// <summary>
        /// Each tick, removes a <paramref name="churnRate"/> share of the current node count
        /// (floored), never dropping below <paramref name="churnMinNodes"/>. Candidates are
        /// picked uniformly at random from every live node.
        /// </summary>
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

        /// <summary>A named pool's current combined HashPower, or 0 if it doesn't exist yet.</summary>
        public int GetPoolHashPower(string poolName)
        {
            lock (_lock)
            {
                return _poolMinersByName.TryGetValue(poolName, out var pool) ? pool.TotalHashPower : 0;
            }
        }

        /// <summary>
        /// Moves <paramref name="nodeId"/> between the solo miner roster and a pool's member
        /// list. Looks the <see cref="SoloMiner"/> object up wherever it currently lives
        /// rather than trusting a caller-supplied reference, so it's correct even if the node
        /// churned out between deciding to switch and this call running.
        /// </summary>
        public void SwitchPoolMembership(string nodeId, string? newPool)
        {
            lock (_lock)
            {
                SoloMiner? soloMiner = _allMiners.OfType<SoloMiner>().FirstOrDefault(m => m.Id == nodeId);
                if (soloMiner != null)
                {
                    _allMiners.Remove(soloMiner);
                }
                else
                {
                    foreach (var (poolName, pool) in _poolMinersByName.ToList())
                    {
                        if (pool.TryRemoveMember(nodeId, out var removed))
                        {
                            soloMiner = removed;
                            if (pool.MemberCount == 0)
                            {
                                _poolMinersByName.Remove(poolName);
                                _allMiners.Remove(pool);
                            }
                            break;
                        }
                    }
                }
                if (soloMiner == null) return;

                if (string.IsNullOrEmpty(newPool))
                {
                    _allMiners.Add(soloMiner);
                }
                else if (_poolMinersByName.TryGetValue(newPool, out var target))
                {
                    target.AddMember(soloMiner);
                }
                else
                {
                    var created = new PoolMiner(newPool, new[] { soloMiner }, _rng);
                    _poolMinersByName[newPool] = created;
                    _allMiners.Add(created);
                }
            }
        }

        /// <summary>
        /// The inverse of <see cref="AddNodeAsync"/>. The departed node's persistence task and
        /// <see cref="BlockchainStore"/> deliberately keep running, preserving its final state
        /// on disk until the whole run's cancellation token fires.
        /// </summary>
        public void RemoveNode(string nodeId, ChainWatcher watcher)
        {
            lock (_lock)
            {
                if (!_nodesById.Remove(nodeId)) return;

                _allNodes.RemoveAll(n => n.Id == nodeId);
                _allNodeIds.Remove(nodeId);
                _economicWeightByNodeId.Remove(nodeId);
                _roleByNodeId.Remove(nodeId);
                _canMineByNodeId.Remove(nodeId);

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

        /// <summary>
        /// Unlike <see cref="RemoveNode"/>, this is deliberately one-directional: it only
        /// drops <paramref name="peerId"/> out of <paramref name="nodeId"/>'s own edge set.
        /// The peer's own edge set is untouched; <see cref="Node"/>'s receive handlers refuse
        /// requests from a discouraged peer directly.
        /// </summary>
        public void DiscouragePeer(string nodeId, string peerId)
        {
            lock (_lock)
            {
                if (_peerIdsByNodeId.TryGetValue(nodeId, out var mySet))
                    mySet.Remove(peerId);
            }
        }
    }

    /// <summary><see cref="NodeNetwork.GetSnapshot"/>'s per-node return shape.</summary>
    public sealed class NodeSummary
    {
        public string Id { get; init; } = "";
        public NodeRole Role { get; init; }
        public bool CanMine { get; init; }
        public int HashPower { get; init; }
        public string? Pool { get; init; }
        public int EconomicWeight { get; init; }
        /// <summary>Total peers, inbound + outbound — a node's structural influence: how much of the network's block/chain relay flows through it.</summary>
        public int PeerCount { get; init; }
    }

    public sealed class PoolSummary
    {
        public string Name { get; init; } = "";
        public int MemberCount { get; init; }
        public int TotalHashPower { get; init; }
    }

    public sealed class NetworkSnapshot
    {
        public List<NodeSummary> Nodes { get; init; } = new();
        public List<PoolSummary> Pools { get; init; } = new();
    }
}
