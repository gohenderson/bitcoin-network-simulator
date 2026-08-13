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
    // Owns node creation (AddNodeAsync) and organic growth (GrowthLoopAsync).
    // Program.cs is the only caller; MiningScheduler and TransactionGenerator
    // only ever read through the snapshot accessors below.
    // ------------------------------------------------------------------
    public sealed class NodeNetwork
    {
        public const int DefaultMaxNodes = 100;
        public const int DefaultGrowthIntervalMs = 8000; // roughly double the network every 8 s — see GrowthLoopAsync

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

        // Default assignment for a brand new node with no metadata.json yet:
        // every 8th node cycles through one of each malicious type, the rest are
        // honest. Only used the first time a given node id is created — see
        // NodeMetadataStore.LoadOrCreateAsync, which persists this so it (or a
        // hand edit on top of it) sticks across restarts.
        private static NodeRole AssignRole(int index) => (index % 8) switch
        {
            4 => NodeRole.Equivocator,
            5 => NodeRole.Impersonator,
            6 => NodeRole.Corruptor,
            7 => NodeRole.Withholder,
            _ => NodeRole.Honest
        };

        // Default mining participation for a brand new node: every 3rd node is
        // wallet-only (fully validates, gossips, sends/receives transactions,
        // but never gets a mining turn), so a fresh run shows a mix without any
        // manual edits. Same override rules as AssignRole — see
        // NodeMetadataStore.LoadOrCreateAsync and the "Mining participation"
        // note in README.md.
        private static bool AssignCanMine(int index) => index % 3 != 2;

        private readonly string _runRootDir;
        private readonly int _port;
        private readonly Random _rng = new();

        // All mutable registry state below is guarded by this one lock —
        // nodes call the getters at broadcast time so newly joined peers are
        // automatically included without any wiring.
        private readonly object _lock = new();
        private readonly List<string> _allNodeIds = new();
        private readonly List<Node> _allNodes = new();
        private readonly Dictionary<string, Node> _nodesById = new();
        private readonly List<Task> _persistTasks = new();
        private readonly List<BlockchainStore> _blockchainStores = new();
        private readonly List<IMiner> _allMiners = new();
        private readonly Dictionary<string, PoolMiner> _poolMinersByName = new();

        public NodeNetwork(string runRootDir, int port)
        {
            _runRootDir = runRootDir;
            _port = port;
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

        public async Task AddNodeAsync(ChainWatcher watcher, CancellationToken token)
        {
            int index;
            lock (_lock) { index = _allNodeIds.Count; }

            var id = NodeNameFor(index);

            Directory.CreateDirectory(NodeDirFor(id));
            var metadata = await NodeMetadataStore.LoadOrCreateAsync(_runRootDir, id, AssignRole(index), AssignCanMine(index));

            lock (_lock)
            {
                _allNodeIds.Add(id);
            }
            watcher.AddNode(id);

            // Composition root: Chain and Mempool are constructed once here and
            // shared between Node (which serves them over HTTP) and SoloMiner
            // (which reads/mutates them while mining) — see the comments atop
            // Node.cs and Miner.cs for why SoloMiner takes these directly
            // instead of holding a reference back to the Node it mines for.
            var chain = new Blockchain();
            var mempool = new ConcurrentQueue<Transaction>();
            var signingKey = NodeMetadataStore.ImportSigningKey(metadata.SigningKey!);
            var soloMiner = new SoloMiner(id, _port, metadata.NodeRole, metadata.HashPower, chain, mempool, GetAllNodeIds, watcher, signingKey);
            var node = new Node(id, chain, mempool, watcher);
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

            Console.WriteLine($"[network] node #{index} ({id}, {metadata.NodeRole}, hashPower={metadata.HashPower}, canMine={metadata.CanMine}, pool={metadata.Pool ?? "(solo)"}) joined at /{id}/ — total: {index + 1}");
        }

        // Roughly exponential growth, not linear: each tick, as many new nodes
        // join as already exist — a network effect where the bigger it already
        // is, the faster it grows, rather than a fixed trickle of one at a
        // time — capped so the total never exceeds maxNodes. New nodes are
        // added one at a time (sequential awaits) so each gets a clean,
        // atomically-assigned index/port. maxNodes/growthIntervalMs default to
        // DefaultMaxNodes/DefaultGrowthIntervalMs but can be overridden by a
        // scenario's MaxNodes/GrowthIntervalSeconds — see "Scenarios" in
        // README.md.
        public async Task GrowthLoopAsync(ChainWatcher watcher, CancellationToken token, int maxNodes, int growthIntervalMs)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(growthIntervalMs, token); }
                catch (OperationCanceledException) { break; }

                int count;
                lock (_lock) { count = _allNodes.Count; }
                if (count >= maxNodes) break;

                var toAdd = Math.Min(count, maxNodes - count);
                for (int i = 0; i < toAdd; i++)
                    await AddNodeAsync(watcher, token);
            }
        }
    }
}
