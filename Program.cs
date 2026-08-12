// BitcoinNetworkSimulator: a single-process simulation of a Bitcoin-style
// P2P network — real proof-of-work, gossip/reorg, coin issuance, balance
// enforcement, mining pools, signed blocks, and a handful of deliberately
// malicious node roles. See README.md for how the whole system works and
// how to run it; the mechanism-specific comments that used to live here now
// live next to the code they explain (ProofOfWork, Economics, Ledger,
// Blockchain.ValidateChain, and the MINING POOLS / BUILTBY SIGNING notes in
// Miner.cs).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Elastic worker pool: a bounded set of async worker loops pulling from a
    // shared queue. Starts with `minWorkers` running. If the backlog grows past
    // `scaleUpQueueThreshold`, it spins up another worker (up to `maxWorkers`).
    // Idle workers beyond the minimum retire themselves after a timeout, so the
    // pool grows under load and shrinks back down at rest.
    // ------------------------------------------------------------------

    public class ElasticTaskPool
    {
        private readonly string _ownerId;
        private readonly int _minWorkers;
        private readonly int _maxWorkers;
        private readonly int _scaleUpQueueThreshold;
        private readonly TimeSpan _idleRetireAfter;

        private readonly ConcurrentQueue<Func<Task>> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly object _scaleLock = new();
        private int _currentWorkers = 0;

        public ElasticTaskPool(string ownerId, int minWorkers = 2, int maxWorkers = 32,
            int scaleUpQueueThreshold = 4, TimeSpan? idleRetireAfter = null)
        {
            _ownerId = ownerId;
            _minWorkers = minWorkers;
            _maxWorkers = maxWorkers;
            _scaleUpQueueThreshold = scaleUpQueueThreshold;
            _idleRetireAfter = idleRetireAfter ?? TimeSpan.FromSeconds(10);

            for (int i = 0; i < _minWorkers; i++)
                SpawnWorker(isCoreWorker: true);
        }

        public void Enqueue(Func<Task> work)
        {
            _queue.Enqueue(work);
            _signal.Release();

            lock (_scaleLock)
            {
                if (_queue.Count > _scaleUpQueueThreshold && _currentWorkers < _maxWorkers)
                    SpawnWorker(isCoreWorker: false);
            }
        }

        private void SpawnWorker(bool isCoreWorker)
        {
            lock (_scaleLock)
            {
                if (_currentWorkers >= _maxWorkers) return;
                _currentWorkers++;
                Console.WriteLine($"[{_ownerId}] worker pool scaled up to {_currentWorkers} (queue depth {_queue.Count})");
            }
            _ = Task.Run(() => WorkerLoop(isCoreWorker));
        }

        private async Task WorkerLoop(bool isCoreWorker)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    bool signaled;
                    try
                    {
                        signaled = await _signal.WaitAsync(_idleRetireAfter, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!signaled)
                    {
                        if (!isCoreWorker)
                            break;
                        continue;
                    }

                    if (_queue.TryDequeue(out var work))
                    {
                        try { await work(); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{_ownerId}] worker error: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                lock (_scaleLock)
                {
                    _currentWorkers--;
                }
            }
        }

        public void Stop() => _cts.Cancel();
    }

    // Persisted, hand-editable per-node configuration — see "Persistence &
    // resume" in README.md. Lives at nodes/<node-id>/metadata.json, alongside
    // that node's blockchain.db. NodeRole is serialized as its string name
    // (not the underlying int) specifically so it's easy to read and edit by
    // hand — see Program's metadata JSON options.
    public class NodeMetadata
    {
        public string Id { get; set; } = "";
        public NodeRole NodeRole { get; set; } = NodeRole.Honest;
        public int HashPower { get; set; } = 1;
        // Whether this node ever gets a mining turn. A node with CanMine false
        // still does everything else a full node does — serves /chain, /tx,
        // /balances, receives and validates blocks and chains from peers, holds
        // a mempool — it just never builds a block itself, i.e. a wallet-only /
        // relay-only participant. See the "Mining participation" note in README.md.
        public bool CanMine { get; set; } = true;
        // Null/empty = mines solo (default). Otherwise, the name of a mining
        // pool this node subscribes to — its HashPower is combined with every
        // other current member's into the pool's single shared turn instead of
        // getting its own. Only honored for NodeRole.Honest nodes; a malicious
        // role's Pool value, if set, is ignored and it always mines solo. See
        // the "Mining pools" note in README.md.
        public string? Pool { get; set; } = null;
        // Base64-encoded DER (ECDsa.ExportECPrivateKey) signing identity key
        // — see the "Signed blocks" note in README.md. Unlike every other
        // field here, this one should never be hand-edited or deleted once a
        // node has mined blocks: doing so orphans every historical block it
        // signed, since the key registered for its Id on the next run would
        // no longer be the one that actually signed that history.
        public string? SigningKey { get; set; } = null;
    }

    // ------------------------------------------------------------------
    // Program: spins up N nodes (each an async listener + a continuous mining
    // loop, both backed by their own resources), a transaction generator, and
    // per-node persistence — all as async Tasks. There is no mining coordinator
    // anymore: with a public, deterministically-derived target, nothing needs
    // one.
    // ------------------------------------------------------------------

    public static class Program
    {
        // Every node in the network is reachable through this one shared
        // port — see NetworkServer.cs — addressed by node id in the URL
        // path (e.g. http://localhost:5000/000-alpha/chain) rather than one
        // real OS port per node.
        private const int Port = 5000;
        private const int MaxNodes = 100;
        private const int GrowthIntervalMs = 8000; // roughly double the network every 8 s — see NodeGrowthLoopAsync
        private static readonly Random Rng = new();

        // Root directory for this run's node folders and watcher.db
        // — ScenarioResults/<timestamp>-<scenario name, or "no-scenario">/,
        // computed once at startup by DetermineRunRootDir before anything
        // else happens, so every run's artifacts land in their own
        // timestamped, reviewable folder instead of always overwriting the
        // same nodes/ next to the executable. See SCENARIO EXECUTION at the
        // top of the file.
        private static string RunRootDir = AppContext.BaseDirectory;

        private static string SanitizeForFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        // Also copies the exact scenario file that was executed into the new
        // result folder (unmodified, same filename) when one was used, so
        // the folder is a self-contained record of both what happened and
        // exactly what configuration produced it — no need to go find
        // Scenarios/whatever.json separately, which may have since been
        // edited or deleted.
        private static string DetermineRunRootDir(string scenarioPath, Scenario? scenario)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            var label = scenario != null
                ? SanitizeForFileName(Path.GetFileNameWithoutExtension(scenarioPath))
                : "no-scenario";
            var dir = Path.Combine(AppContext.BaseDirectory, "ScenarioResults", $"{timestamp}-{label}");
            Directory.CreateDirectory(dir);

            if (scenario != null)
            {
                try
                {
                    File.Copy(scenarioPath, Path.Combine(dir, Path.GetFileName(scenarioPath)), overwrite: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[scenario] failed to copy {scenarioPath} into {dir}: {ex.Message}");
                }
            }

            return dir;
        }

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
        private static string NodeNameFor(int index) =>
            $"{index:D3}-{GreekNames[index % GreekNames.Length]}";

        // Default assignment for a brand new node with no metadata.json yet:
        // every 8th node cycles through one of each malicious type, the rest are
        // honest. Only used the first time a given node id is created — see
        // LoadOrCreateMetadataAsync, which persists this so it (or a hand
        // edit on top of it) sticks across restarts.
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
        // LoadOrCreateMetadataAsync and the "Mining participation" note in
        // README.md.
        private static bool AssignCanMine(int index) => index % 3 != 2;

        // Shared, lock-protected registry — nodes call the getters at broadcast time
        // so newly joined peers are automatically included without any wiring.
        private static readonly object NetworkLock = new();
        private static readonly List<string> AllNodeIds = new();
        private static readonly List<Node> AllNodes = new();
        private static readonly Dictionary<string, Node> NodesById = new();
        private static readonly List<Task> PersistTasks = new();
        private static readonly List<BlockchainStore> BlockchainStores = new();

        // Every currently-mining participant — one SoloMiner per non-pooled
        // CanMine node, one PoolMiner per distinct pool name — is what the
        // round-robin scheduler actually rotates over (see
        // RoundRobinMiningLoopAsync). Which bucket a node's SoloMiner lands in
        // is decided once, in AddNodeAsync, at creation time; the scheduler
        // itself never has to ask. PoolMinersByName exists purely so
        // AddNodeAsync can find and grow an existing pool's PoolMiner when a
        // later-joining node subscribes to it, rather than creating a second,
        // competing one. Both are only ever written from AddNodeAsync's
        // single sequential call chain, guarded by NetworkLock alongside
        // AllNodes/AllNodeIds/NodesById since the scheduler reads AllMiners
        // concurrently with node growth.
        private static readonly List<IMiner> AllMiners = new();
        private static readonly Dictionary<string, PoolMiner> PoolMinersByName = new();

        private static List<string> GetAllNodeIds() { lock (NetworkLock) { return new List<string>(AllNodeIds); } }

        // Backs NetworkServer's request dispatch — resolves a URL path's
        // node-id segment to the live Node that should handle it, or null
        // for an unknown id (404).
        private static Node? ResolveNode(string id) { lock (NetworkLock) { return NodesById.TryGetValue(id, out var node) ? node : null; } }

        private static string NodeDirFor(string nodeId) =>
            Path.Combine(RunRootDir, "nodes", nodeId);

        private static string BlockchainDbPathFor(string nodeId) =>
            Path.Combine(NodeDirFor(nodeId), "blockchain.db");

        private static string MetadataPathFor(string nodeId) =>
            Path.Combine(NodeDirFor(nodeId), "metadata.json");

        // NodeRole serialized by name ("Honest", "Corruptor", ...) rather than
        // its underlying int, since metadata.json is meant to be hand-readable
        // and hand-editable.
        private static readonly JsonSerializerOptions MetadataJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // Base64-encoded DER <-> ECDsa conversions shared by every place that
        // reads or writes NodeMetadata.SigningKey.
        private static string ExportSigningKey(ECDsa key) => Convert.ToBase64String(key.ExportECPrivateKey());

        private static ECDsa ImportSigningKey(string base64)
        {
            var key = ECDsa.Create();
            key.ImportECPrivateKey(Convert.FromBase64String(base64), out _);
            return key;
        }

        // Loads a node's persisted metadata.json if one already exists (from a
        // previous run, or hand-edited by a user to bump HashPower or change
        // NodeRole) so it survives a restart unchanged. Only a brand new node —
        // no metadata.json yet — gets fresh defaults, written out immediately so
        // they're there to edit or resume from next time. SigningKey is handled
        // specially either way: a loaded metadata.json missing one (e.g. saved
        // before this field existed) gets a freshly generated key filled in and
        // re-saved immediately, exactly as if it were brand new — see the
        // "Signed blocks" note in README.md for why that key, once established,
        // must never change again.
        private static async Task<NodeMetadata> LoadOrCreateMetadataAsync(string id, int index)
        {
            var path = MetadataPathFor(id);
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    var loaded = JsonSerializer.Deserialize<NodeMetadata>(json, MetadataJsonOptions);
                    if (loaded != null)
                    {
                        Console.WriteLine($"[{id}] loaded saved metadata (role={loaded.NodeRole}, hashPower={loaded.HashPower}, canMine={loaded.CanMine}, pool={loaded.Pool ?? "(solo)"})");
                        if (string.IsNullOrEmpty(loaded.SigningKey))
                        {
                            loaded.SigningKey = ExportSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));
                            await SaveMetadataAsync(loaded);
                        }
                        return loaded;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{id}] failed to read saved metadata: {ex.Message}; assigning defaults");
                }
            }

            var metadata = new NodeMetadata
            {
                Id = id,
                NodeRole = AssignRole(index),
                HashPower = 1,
                CanMine = AssignCanMine(index),
                SigningKey = ExportSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256))
            };
            await SaveMetadataAsync(metadata);
            return metadata;
        }

        private static async Task SaveMetadataAsync(NodeMetadata metadata)
        {
            var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
            await File.WriteAllTextAsync(MetadataPathFor(metadata.Id), json);
        }

        // Writes (or updates) nodes/<id>/metadata.json for every position
        // `scenario`'s NodeGroups define, BEFORE any node is created or any
        // metadata is loaded for real — this is what makes the scenario
        // authoritative for behavior (NodeRole, HashPower, CanMine, Pool)
        // every time it's applied, the same way hand-editing metadata.json
        // already is. If a position already has metadata on disk (from a
        // previous run, scenario or otherwise), its existing SigningKey is
        // preserved rather than regenerated — see "Persistence & resume" and
        // the "Signed blocks" note in README.md for why — so re-running the
        // same scenario keeps building on the same node identities and chain
        // history instead of resetting to genesis every time. Only a
        // genuinely new position gets a freshly generated key. See "Scenarios"
        // in README.md.
        private static async Task ApplyScenarioAsync(Scenario scenario)
        {
            var index = 0;
            foreach (var group in scenario.NodeGroups)
            {
                for (var i = 0; i < group.Count; i++, index++)
                {
                    var id = NodeNameFor(index);
                    Directory.CreateDirectory(NodeDirFor(id));

                    NodeMetadata? existing = null;
                    var path = MetadataPathFor(id);
                    if (File.Exists(path))
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(path);
                            existing = JsonSerializer.Deserialize<NodeMetadata>(json, MetadataJsonOptions);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[scenario] [{id}] failed to read existing metadata: {ex.Message}; treating as new");
                        }
                    }

                    var metadata = existing ?? new NodeMetadata { Id = id };
                    metadata.Id = id;
                    metadata.NodeRole = group.Role;
                    metadata.HashPower = group.HashPower;
                    metadata.CanMine = group.CanMine;
                    metadata.Pool = group.Pool;
                    if (string.IsNullOrEmpty(metadata.SigningKey))
                        metadata.SigningKey = ExportSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));

                    await SaveMetadataAsync(metadata);
                }
            }

            var durationNote = scenario.DurationSeconds is int d ? $", running for {d}s" : ", no automatic stop";
            var growthNote = scenario.AutoGrowth ? ", auto-growth still enabled on top" : ", auto-growth disabled";
            Console.WriteLine($"[scenario] applied {index} node(s) across {scenario.NodeGroups.Count} group(s){durationNote}{growthNote}");
        }

        // Registers every already-persisted node's public key (from a
        // previous run) BEFORE any node starts joining or resuming its chain
        // this run. This closes an ordering gap that would otherwise exist:
        // nodes are (re)created one at a time as the network grows, but a
        // resumed chain can reference BuiltBy names for nodes that haven't
        // been (re)created yet THIS run — without this preload, validating
        // node 0's saved chain would fail on any historical block built by
        // node 5, since node 5's key wouldn't be registered until node 5
        // itself joins, seconds or minutes later. Scanning every persisted
        // nodes/<id>/metadata.json up front, independent of join order, is
        // this simulation's stand-in for however a real network would
        // bootstrap a shared, trusted view of "who's who" (see
        // NodeIdentityRegistry).
        private static async Task PreloadKnownSigningKeysAsync()
        {
            var nodesDir = Path.Combine(RunRootDir, "nodes");
            if (!Directory.Exists(nodesDir)) return;

            foreach (var dir in Directory.GetDirectories(nodesDir))
            {
                var id = Path.GetFileName(dir);
                var path = Path.Combine(dir, "metadata.json");
                if (!File.Exists(path)) continue;

                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    var saved = JsonSerializer.Deserialize<NodeMetadata>(json, MetadataJsonOptions);
                    if (saved == null || string.IsNullOrEmpty(saved.SigningKey)) continue;

                    using var ecdsa = ImportSigningKey(saved.SigningKey);
                    NodeIdentityRegistry.Register(id, ecdsa.ExportSubjectPublicKeyInfo());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{id}] failed to preload saved metadata for its signing key: {ex.Message}; skipping");
                }
            }
        }

        public static async Task Main(string[] args)
        {
            Console.WriteLine("=== BitcoinNetworkSimulator ===");

            // A scenario file governs this run's starting node population,
            // growth behavior, and duration — see "Scenarios" in README.md.
            // `dotnet run -- path/to/scenario.json` picks a
            // specific file; otherwise scenario.json next to the executable
            // is used if present. No file at all means the normal
            // single-node, indefinite-runtime default, unchanged from before
            // this feature existed.
            var scenarioPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "scenario.json");
            var scenario = await ScenarioLoader.LoadAsync(scenarioPath);

            // Every run's node folders and watcher.db land under
            // their own timestamped ScenarioResults/ subfolder from this
            // point on — see "Scenarios" in README.md.
            RunRootDir = DetermineRunRootDir(scenarioPath, scenario);
            Console.WriteLine($"Results: {RunRootDir}\n");

            var effectiveMaxNodes = scenario?.MaxNodes ?? MaxNodes;
            var effectiveGrowthIntervalMs = scenario?.GrowthIntervalSeconds is int gis ? gis * 1000 : GrowthIntervalMs;
            var autoGrowthEnabled = scenario?.AutoGrowth ?? true;

            if (scenario != null)
            {
                if (!string.IsNullOrWhiteSpace(scenario.Description))
                    Console.WriteLine($"Scenario: {scenario.Description}");
                await ApplyScenarioAsync(scenario);
            }
            else
            {
                Console.WriteLine($"Dynamic network: starts at 1 node, roughly doubles every {effectiveGrowthIntervalMs / 1000} s (cap: {effectiveMaxNodes}).");
            }
            Console.WriteLine("Mining is round-robin across active nodes — no per-node background threads.");
            Console.WriteLine("Real proof-of-work: a public, deterministically-derived target.\n");

            var cts = new CancellationTokenSource();
            using var watcherStore = new WatcherStore(Path.Combine(RunRootDir, "watcher.db"), Port, scenario != null ? scenarioPath : null, scenario?.Description);
            var watcher = new ChainWatcher(Port, new List<string>(), watcherStore);

            // One shared listener for the whole network — see
            // NetworkServer.cs — dispatching every request by the node id in
            // its URL path (ResolveNode looks it up in NodesById) rather
            // than each node owning its own OS-level port.
            var server = new NetworkServer(Port, ResolveNode);
            server.Start();

            await PreloadKnownSigningKeysAsync();

            var initialNodeCount = scenario?.NodeGroups.Count > 0 ? scenario.NodeGroups.Sum(g => g.Count) : 1;
            for (var i = 0; i < initialNodeCount; i++)
                await AddNodeAsync(watcher, cts.Token);

            var miningTask = Task.Factory.StartNew(
                async () => await RoundRobinMiningLoopAsync(cts.Token),
                cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            var txTask = TransactionGeneratorLoopAsync(cts.Token);
            var growthTask = autoGrowthEnabled
                ? NodeGrowthLoopAsync(watcher, cts.Token, effectiveMaxNodes, effectiveGrowthIntervalMs)
                : Task.CompletedTask;
            var watcherTask = watcher.RunAsync(cts.Token);

            Console.WriteLine($"All nodes share http://localhost:{Port}/ — address one by id in the path." +
                (autoGrowthEnabled ? " Network grows automatically." : " Auto-growth disabled — network stays fixed."));
            Console.WriteLine($"Try: curl http://localhost:{Port}/{NodeNameFor(0)}/chain");
            Console.WriteLine($"Or:  curl http://localhost:{Port}/{NodeNameFor(0)}/balances");
            Console.WriteLine("Watcher: inspect watcher.db (SQLite) for convergence/recovery history.");

            if (scenario?.DurationSeconds is int durationSeconds && durationSeconds > 0)
            {
                Console.WriteLine($"Scenario duration: {durationSeconds}s (or press Enter to stop early).\n");
                var enterTask = Task.Run(() => Console.ReadLine());
                var winner = await Task.WhenAny(enterTask, Task.Delay(TimeSpan.FromSeconds(durationSeconds)));
                if (winner != enterTask)
                    Console.WriteLine("\nScenario duration elapsed — stopping.");
            }
            else
            {
                Console.WriteLine("Press Enter to stop.\n");
                Console.ReadLine();
            }

            cts.Cancel();
            server.Stop();

            try
            {
                List<Task> persistSnapshot;
                lock (NetworkLock) { persistSnapshot = new List<Task>(PersistTasks); }
                await Task.WhenAll(
                    new[] { miningTask, txTask, growthTask, watcherTask }
                    .Concat(persistSnapshot));
            }
            catch (OperationCanceledException) { }

            List<BlockchainStore> storesSnapshot;
            lock (NetworkLock) { storesSnapshot = new List<BlockchainStore>(BlockchainStores); }
            foreach (var store in storesSnapshot) store.Dispose();

            Console.WriteLine("Stopped.");
        }

        private static async Task AddNodeAsync(ChainWatcher watcher, CancellationToken token)
        {
            int index;
            lock (NetworkLock) { index = AllNodeIds.Count; }

            var id = NodeNameFor(index);

            Directory.CreateDirectory(NodeDirFor(id));
            var metadata = await LoadOrCreateMetadataAsync(id, index);

            lock (NetworkLock)
            {
                AllNodeIds.Add(id);
            }
            watcher.AddNode(id);

            // Composition root: Chain and Mempool are constructed once here and
            // shared between Node (which serves them over HTTP) and SoloMiner
            // (which reads/mutates them while mining) — see the comments atop
            // Node.cs and Miner.cs for why SoloMiner takes these directly
            // instead of holding a reference back to the Node it mines for.
            var chain = new Blockchain();
            var mempool = new ConcurrentQueue<Transaction>();
            var signingKey = ImportSigningKey(metadata.SigningKey!);
            var soloMiner = new SoloMiner(id, Port, metadata.NodeRole, metadata.HashPower, chain, mempool, GetAllNodeIds, watcher, signingKey);
            var node = new Node(id, chain, mempool, watcher);
            var blockchainStore = new BlockchainStore(BlockchainDbPathFor(id));
            ResumeNodeFromDisk(node, blockchainStore);

            lock (NetworkLock)
            {
                AllNodes.Add(node);
                NodesById[id] = node;
                BlockchainStores.Add(blockchainStore);
                PersistTasks.Add(PersistenceLoopAsync(node, blockchainStore, token));

                // Deciding solo vs. pooled — and, for pools, finding or
                // creating that pool's PoolMiner — happens exactly once, right
                // here at creation time. Nothing downstream (the scheduler)
                // ever has to re-derive it. Matches RoundRobinMiningLoopAsync's
                // grouping rules from before this became IMiner-based:
                // wallet-only nodes contribute no miner at all; malicious
                // roles always mine solo even if a Pool value is set.
                if (metadata.CanMine)
                {
                    if (metadata.NodeRole == NodeRole.Honest && !string.IsNullOrEmpty(metadata.Pool))
                    {
                        if (PoolMinersByName.TryGetValue(metadata.Pool, out var pool))
                            pool.AddMember(soloMiner);
                        else
                        {
                            var newPool = new PoolMiner(metadata.Pool, new[] { soloMiner }, Rng);
                            PoolMinersByName[metadata.Pool] = newPool;
                            AllMiners.Add(newPool);
                        }
                    }
                    else
                    {
                        AllMiners.Add(soloMiner);
                    }
                }
            }

            Console.WriteLine($"[network] node #{index} ({id}, {metadata.NodeRole}, hashPower={metadata.HashPower}, canMine={metadata.CanMine}, pool={metadata.Pool ?? "(solo)"}) joined at /{id}/ — total: {index + 1}");
        }

        // A turn that finds nothing (the common case now that MineBlock is
        // bounded by HashPower — see SIMULATED HASH POWER at the top of the
        // file) returns almost instantly, so without a pause here this loop
        // would spin a CPU core at ~100% doing essentially nothing. This delay
        // paces turns to something an operator can actually watch, and bounds
        // how often Chain.Snapshot() (an O(chain length) copy) gets taken.
        private const int MiningTurnDelayMs = 25;

        // Persistent random sort key per mining participant (IMiner.Label — a
        // node's Id for a SoloMiner, a pool's name for a PoolMiner), used by
        // RoundRobinMiningLoopAsync to order turns. Cleared — so everyone
        // draws a fresh key — every time a new block appears; a participant
        // not yet in here (including one that just joined mid-height) draws
        // its key the first time it's looked up, landing it at a random
        // position rather than always at the end. Only ever touched from that
        // loop's single sequential iteration, so it needs no locking.
        private static readonly Dictionary<string, double> MiningOrderKeys = new();

        private static double OrderKeyFor(string participantLabel)
        {
            if (!MiningOrderKeys.TryGetValue(participantLabel, out var key))
            {
                key = Rng.NextDouble();
                MiningOrderKeys[participantLabel] = key;
            }
            return key;
        }

        // Rotates across mining "turns" — see the "Mining participation" and
        // "Mining pools" notes in README.md. Deliberately knows nothing about
        // solo vs. pooled mining, roles, or hash power: every entry in
        // AllMiners is just an IMiner, and whether it's a SoloMiner or a
        // PoolMiner (and, for a pool, who's currently in it) was already
        // decided back in AddNodeAsync. The roster is re-derived fresh every
        // iteration (so a node that just joined is included immediately), but
        // its ORDER is not just insertion order — it's sorted by
        // MiningOrderKeys, which only gets reshuffled (cleared) when a new
        // block appears (detected by watching AllNodes[0]'s tip hash change).
        // Otherwise whichever miner happened to be created earliest would
        // always go first in every round, giving it first crack at every
        // height.
        private static async Task RoundRobinMiningLoopAsync(CancellationToken token)
        {
            int index = 0;
            string? lastTipHash = null;

            while (!token.IsCancellationRequested)
            {
                Node? tipReference;
                List<IMiner> miners;
                lock (NetworkLock)
                {
                    tipReference = AllNodes.Count > 0 ? AllNodes[0] : null;
                    miners = new List<IMiner>(AllMiners);
                }

                if (tipReference == null)
                {
                    try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                    continue;
                }

                var currentTipHash = tipReference.Chain.Latest.Hash;
                if (currentTipHash != lastTipHash)
                {
                    MiningOrderKeys.Clear();
                    lastTipHash = currentTipHash;
                    index = 0;
                }

                var ordered = miners.OrderBy(m => OrderKeyFor(m.Label)).ToList();
                if (ordered.Count == 0)
                {
                    try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                index %= ordered.Count;

                try { await ordered[index].MineOneRoundAsync(token); }
                catch (OperationCanceledException) { break; }

                index++;
                try { await Task.Delay(MiningTurnDelayMs, token); } catch (OperationCanceledException) { break; }
            }
        }

        // Roughly exponential growth, not linear: each tick, as many new nodes
        // join as already exist — a network effect where the bigger it already
        // is, the faster it grows, rather than a fixed trickle of one at a
        // time — capped so the total never exceeds maxNodes. New nodes are
        // still added one at a time (sequential awaits, same as before) so
        // each keeps getting a clean, atomically-assigned index/port; only how
        // MANY join per tick has changed. maxNodes/growthIntervalMs default to
        // MaxNodes/GrowthIntervalMs but can be overridden by a scenario's
        // MaxNodes/GrowthIntervalSeconds — see SCENARIO EXECUTION at the top
        // of the file.
        private static async Task NodeGrowthLoopAsync(ChainWatcher watcher, CancellationToken token, int maxNodes, int growthIntervalMs)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(growthIntervalMs, token); }
                catch (OperationCanceledException) { break; }

                int count;
                lock (NetworkLock) { count = AllNodes.Count; }
                if (count >= maxNodes) break;

                var toAdd = Math.Min(count, maxNodes - count);
                for (int i = 0; i < toAdd; i++)
                    await AddNodeAsync(watcher, token);
            }
        }

        private static void ResumeNodeFromDisk(Node node, BlockchainStore store)
        {
            try
            {
                var candidate = store.LoadAll();
                if (candidate == null || candidate.Count == 0)
                {
                    Console.WriteLine($"[{node.Id}] no saved chain found (blockchain.db); starting from genesis");
                    return;
                }

                var (loaded, reason) = node.Chain.TryLoadFrom(candidate);
                Console.WriteLine(loaded
                    ? $"[{node.Id}] {reason}"
                    : $"[{node.Id}] ignored saved chain ({reason}); starting from genesis");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{node.Id}] failed to read saved chain: {ex.Message}; starting from genesis");
            }
        }

        private static async Task<bool> DelayOrCancelled(int milliseconds, CancellationToken token)
        {
            try { await Task.Delay(milliseconds, token); return true; }
            catch (OperationCanceledException) { return false; }
        }

        // Sends from real node IDs (alpha, beta, ...) rather than made-up user
        // names, because only node IDs ever actually receive coins (the coinbase
        // reward is paid to whoever built the block) — with balance enforcement
        // now in place, a fictional account that never earns anything could
        // never legally send anything either. Balances are recomputed from a
        // live /chain snapshot each round (never trusted from a cache), and a
        // sender never gets asked to send more than they currently have.
        private static async Task TransactionGeneratorLoopAsync(CancellationToken token)
        {
            using var http = new HttpClient();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var nodeIds = GetAllNodeIds();
                    if (nodeIds.Count == 0) { if (!await DelayOrCancelled(500, token)) break; continue; }

                    var queryId = nodeIds[Rng.Next(nodeIds.Count)];
                    var chainJson = await http.GetStringAsync($"http://localhost:{Port}/{queryId}/chain", token);
                    var chain = JsonSerializer.Deserialize<List<Block>>(chainJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<Block>();
                    var balances = Ledger.ComputeBalances(chain);

                    var spenders = nodeIds.Where(n => balances.GetValueOrDefault(n) > 0m).ToList();
                    if (spenders.Count == 0) { if (!await DelayOrCancelled(1500, token)) break; continue; }

                    var from = spenders[Rng.Next(spenders.Count)];
                    string to;
                    do { to = nodeIds[Rng.Next(nodeIds.Count)]; } while (to == from && nodeIds.Count > 1);

                    var amount = Math.Min(balances[from], (decimal)Rng.Next(1, 100));
                    var tx = new Transaction { From = from, To = to, Amount = amount };

                    var targetId = nodeIds[Rng.Next(nodeIds.Count)];
                    var json = JsonSerializer.Serialize(tx);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await http.PostAsync($"http://localhost:{Port}/{targetId}/tx", content, token);
                }
                catch (OperationCanceledException) { break; }
                catch { }

                if (!await DelayOrCancelled(1500, token)) break;
            }
        }

        private static async Task PersistenceLoopAsync(Node node, BlockchainStore store, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = node.Chain.Snapshot();
                    store.Sync(snapshot);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[persistence:{node.Id}] failed to sync blockchain.db: {ex.Message}");
                }

                if (!await DelayOrCancelled(3000, token)) break;
            }
        }

    }
}
