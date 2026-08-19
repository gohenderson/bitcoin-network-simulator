using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace BitcoinNetworkSimulator
{
    public enum NetworkState
    {
        Healthy,
        Recovering,
        InvalidState
    }

    public sealed class NodeAudit
    {
        public string NodeId { get; init; } = "";
        public int Height { get; init; }
        public string TipHash { get; init; } = "";
        public bool StructurallyValid { get; init; }
        public string Reason { get; init; } = "";
    }

    public sealed class ReorganizationEvent
    {
        public string Timestamp { get; init; } = "";
        public string NodeId { get; init; } = "";
        public string Reason { get; init; } = "";
    }

    /// <summary>One currently-live branch: every structurally-valid node sharing the same tip hash.</summary>
    public sealed class TipGroup
    {
        public string TipHash { get; init; } = "";
        public int Height { get; init; }
        public List<string> NodeIds { get; init; } = new();
    }

    public sealed class WatcherSnapshot
    {
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public NetworkState State { get; init; }
        public bool ChainsConverged { get; init; }
        public bool AllChainsValid { get; init; }
        public bool NetworkIsMakingProgress { get; init; }
        public int BlocksObserved { get; init; }
        public int BlocksObservedSincePreviousAudit { get; init; }
        public bool InvalidStateWhileProducingBlocks { get; init; }
        public int MinHeight { get; init; }
        public int MaxHeight { get; init; }
        public string CommonTipHash { get; init; } = "";
        public List<NodeAudit> Nodes { get; init; } = new();
        /// <summary>Every currently-live branch among structurally-valid nodes, most-populated first.</summary>
        public List<TipGroup> Tips { get; init; } = new();
        public int ReorganizationsObserved { get; init; }
        /// <summary>
        /// Every audited node's full chain from genesis, deduplicated by hash, in height
        /// order — the dashboard's chain graph collapses long unforked runs client-side, so
        /// the whole tree stays visible without needing to truncate history here.
        /// </summary>
        public List<ChainGraphBlock> ChainGraph { get; init; } = new();
        public string Explanation { get; init; } = "";
    }

    /// <summary>One block within the recent chain-graph window, and which currently-audited nodes still carry it.</summary>
    public sealed class ChainGraphBlock
    {
        public string Hash { get; init; } = "";
        public string PreviousHash { get; init; } = "";
        public int Height { get; init; }
        public string BuiltBy { get; init; } = "";
        public List<string> NodeIds { get; init; } = new();
    }

    /// <summary>
    /// Tracks build/accept/reject/reorg events across the whole simulated network and
    /// periodically audits every node's <c>/chain</c> endpoint to report on convergence, not
    /// just what any single node believes.
    /// </summary>
    public sealed class ChainWatcher
    {
        private readonly NodeNetwork.InternalDispatchFunc _dispatch;
        private List<string> _nodeIds;
        private readonly object _lock = new();
        private readonly WatcherStore _store;
        private WatcherSnapshot? _lastSnapshot;
        private int _blocksObserved;
        private int _reorganizationsObserved;
        private int _rejectedBlocksObserved;

        public ChainWatcher(NodeNetwork.InternalDispatchFunc dispatch, List<string> nodeIds, WatcherStore store)
        {
            _dispatch = dispatch;
            _nodeIds = new List<string>(nodeIds);
            _store = store;
        }

        /// <summary>Most recent convergence audit, or null until <see cref="AuditAsync"/> has run at least once.</summary>
        public WatcherSnapshot? LastSnapshot { get { lock (_lock) return _lastSnapshot; } }

        public void AddNode(string nodeId)
        {
            lock (_lock)
            {
                _nodeIds.Add(nodeId);
            }
        }

        /// <summary>
        /// Stops <see cref="AuditAsync"/> from polling a departed node's <c>/chain</c>
        /// endpoint — otherwise every future audit would find it 404ing and permanently mark
        /// it structurally invalid.
        /// </summary>
        public void RemoveNode(string nodeId)
        {
            lock (_lock)
            {
                _nodeIds.Remove(nodeId);
            }
        }

        public void ObserveBuild(string nodeId, Block block, NodeRole role)
        {
            lock (_lock)
            {
                _blocksObserved++;
            }
            _store.InsertEvent(DateTime.UtcNow, "block-built", nodeId,
                height: block.Index, tipHash: block.Hash,
                role: role.ToString(), builtBy: block.BuiltBy,
                nonce: block.Nonce, txCount: block.Transactions.Count);
        }

        public void ObserveAccepted(string nodeId, Block block)
        {
            _store.InsertEvent(DateTime.UtcNow, "block-accepted", nodeId,
                height: block.Index, tipHash: block.Hash, builtBy: block.BuiltBy);
        }

        public void ObserveRejected(string nodeId, Block block, string reason)
        {
            lock (_lock)
            {
                _rejectedBlocksObserved++;
            }
            _store.InsertEvent(DateTime.UtcNow, "block-rejected", nodeId,
                height: block.Index, tipHash: block.Hash, reason: reason);
        }

        public void ObserveReorganization(string nodeId, string reason)
        {
            lock (_lock)
            {
                _reorganizationsObserved++;
            }
            _store.InsertEvent(DateTime.UtcNow, "reorganization", nodeId, reason: reason);
        }

        /// <summary>
        /// Records that <paramref name="nodeId"/> dropped <paramref name="peerId"/> from its
        /// own peer set after <paramref name="peerId"/> sent it something that failed
        /// <paramref name="nodeId"/>'s own validation for reasons attributable to it.
        /// </summary>
        public void ObserveDiscouraged(string nodeId, string peerId, string reason)
        {
            _store.InsertEvent(DateTime.UtcNow, "peer-discouraged", nodeId, reason: $"discouraged peer {peerId}: {reason}");
        }

        public async Task<WatcherSnapshot> AuditAsync(bool emitTransitions = true)
        {
            List<string> nodeIds;
            lock (_lock) { nodeIds = new List<string>(_nodeIds); }

            var audits = new List<NodeAudit>();
            var chainsByNode = new Dictionary<string, List<Block>>();

            foreach (var nodeId in nodeIds)
            {
                try
                {
                    var (statusCode, body) = await _dispatch(nodeId, "GET", "/chain", null, null);
                    if (statusCode < 200 || statusCode >= 300)
                    {
                        audits.Add(new NodeAudit { NodeId = nodeId, StructurallyValid = false, Reason = $"/chain HTTP {statusCode}" });
                        continue;
                    }

                    var chain = JsonSerializer.Deserialize<List<Block>>(body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (chain == null)
                    {
                        audits.Add(new NodeAudit { NodeId = nodeId, StructurallyValid = false, Reason = "chain endpoint returned null" });
                        continue;
                    }

                    var validation = Blockchain.ValidateSnapshot(chain);
                    audits.Add(new NodeAudit
                    {
                        NodeId = nodeId,
                        Height = Math.Max(0, chain.Count - 1),
                        TipHash = chain.Count == 0 ? "" : chain[^1].Hash,
                        StructurallyValid = validation.Ok,
                        Reason = validation.Reason
                    });

                    chainsByNode[nodeId] = chain;
                }
                catch (Exception ex)
                {
                    audits.Add(new NodeAudit { NodeId = nodeId, StructurallyValid = false, Reason = $"watcher could not inspect node: {ex.Message}" });
                }
            }

            var allValid = audits.Count == nodeIds.Count && audits.All(a => a.StructurallyValid);
            var minHeight = audits.Count == 0 ? 0 : audits.Min(a => a.Height);
            var maxHeight = audits.Count == 0 ? 0 : audits.Max(a => a.Height);

            var graphBlocksByHash = new Dictionary<string, (Block Block, HashSet<string> NodeIds)>();
            foreach (var (nodeId, chain) in chainsByNode)
            {
                foreach (var block in chain)
                {
                    if (graphBlocksByHash.TryGetValue(block.Hash, out var entry))
                        entry.NodeIds.Add(nodeId);
                    else
                        graphBlocksByHash[block.Hash] = (block, new HashSet<string> { nodeId });
                }
            }

            var chainGraph = graphBlocksByHash.Values
                .Select(e => new ChainGraphBlock
                {
                    Hash = e.Block.Hash,
                    PreviousHash = e.Block.PreviousHash,
                    Height = e.Block.Index,
                    BuiltBy = e.Block.BuiltBy,
                    NodeIds = e.NodeIds.ToList()
                })
                .OrderBy(b => b.Height)
                .ToList();
            var distinctTips = audits.Where(a => a.StructurallyValid).Select(a => a.TipHash).Distinct().ToList();
            var converged = allValid && distinctTips.Count == 1;
            var tips = audits.Where(a => a.StructurallyValid)
                .GroupBy(a => a.TipHash)
                .Select(g => new TipGroup { TipHash = g.Key, Height = g.First().Height, NodeIds = g.Select(a => a.NodeId).ToList() })
                .OrderByDescending(g => g.NodeIds.Count)
                .ThenByDescending(g => g.Height)
                .ToList();
            int observedBlocks;
            int reorganizationsObserved;
            WatcherSnapshot? previousSnapshot;
            lock (_lock)
            {
                observedBlocks = _blocksObserved;
                reorganizationsObserved = _reorganizationsObserved;
                previousSnapshot = _lastSnapshot;
            }
            var blocksSincePreviousAudit = previousSnapshot == null
                ? observedBlocks
                : Math.Max(0, observedBlocks - previousSnapshot.BlocksObserved);
            var progress = previousSnapshot != null && maxHeight > previousSnapshot.MaxHeight;

            NetworkState state;
            string explanation;
            if (!allValid)
            {
                state = NetworkState.InvalidState;
                explanation = "At least one node is invalid or unreachable. The network may still be producing blocks, so apparent progress is not evidence of correctness.";
            }
            else if (!converged)
            {
                state = NetworkState.Recovering;
                explanation = $"All observed chains are structurally valid but divergent: heights {minHeight}-{maxHeight}, {distinctTips.Count} valid tip(s).";
            }
            else
            {
                state = NetworkState.Healthy;
                explanation = "All nodes have structurally valid chains and the same tip; the network has converged.";
            }

            var snapshot = new WatcherSnapshot
            {
                Timestamp = DateTime.UtcNow,
                State = state,
                ChainsConverged = converged,
                AllChainsValid = allValid,
                NetworkIsMakingProgress = progress,
                BlocksObserved = observedBlocks,
                BlocksObservedSincePreviousAudit = blocksSincePreviousAudit,
                InvalidStateWhileProducingBlocks = state == NetworkState.InvalidState && blocksSincePreviousAudit > 0,
                MinHeight = minHeight,
                MaxHeight = maxHeight,
                CommonTipHash = converged ? audits[0].TipHash : "",
                Nodes = audits,
                Tips = tips,
                ReorganizationsObserved = reorganizationsObserved,
                ChainGraph = chainGraph,
                Explanation = explanation
            };

            _store.InsertAudit(snapshot);

            if (snapshot.InvalidStateWhileProducingBlocks)
            {
                Console.WriteLine($"[watcher] !!! INVALID STATE WHILE CHAIN CONTINUES BUILDING: {blocksSincePreviousAudit} block build(s) observed since last audit !!!");
            }

            if (emitTransitions)
            {
                WatcherSnapshot? previous;
                lock (_lock) previous = _lastSnapshot;
                if (previous == null || previous.State != snapshot.State)
                {
                    var label = snapshot.State switch
                    {
                        NetworkState.Healthy => "RECOVERED",
                        NetworkState.Recovering => "DIVERGENCE",
                        NetworkState.InvalidState => "INVALID-STATE",
                        _ => "STATE"
                    };
                    Console.WriteLine($"\n[watcher] *** {label} *** {snapshot.Explanation}");
                    _store.InsertEvent(DateTime.UtcNow, $"network-{snapshot.State.ToString().ToLowerInvariant()}",
                        reason: snapshot.Explanation);
                }
            }

            lock (_lock) _lastSnapshot = snapshot;
            return snapshot;
        }

        public async Task RunAsync(CancellationToken token, int intervalMs = 2000)
        {
            await AuditAsync();
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(intervalMs, token); }
                catch (OperationCanceledException) { break; }
                await AuditAsync();
            }
        }
    }

    /// <summary>
    /// SQLite-backed persistence for <see cref="ChainWatcher"/>. One <c>watcher.db</c> lives
    /// in each run's result folder, with tables for run info, the append-only event log, and
    /// periodic convergence audits (plus each audited node's per-audit result). A single
    /// connection is reused for the run's lifetime, with all access serialized through a lock,
    /// since <see cref="ChainWatcher"/>'s observe/audit methods can be called concurrently
    /// from many nodes' request handlers plus the audit loop.
    /// </summary>
    public sealed class WatcherStore : IDisposable
    {
        private readonly object _lock = new();
        private readonly SqliteConnection _connection;

        public WatcherStore(string dbPath, int port, string? scenarioPath, string? scenarioDescription)
        {
            _connection = new SqliteConnection($"Data Source={dbPath}");
            _connection.Open();

            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }

            using (var schema = _connection.CreateCommand())
            {
                schema.CommandText = @"
                    CREATE TABLE IF NOT EXISTS run_info (
                        id INTEGER PRIMARY KEY CHECK (id = 1),
                        started_at TEXT NOT NULL,
                        port INTEGER NOT NULL,
                        scenario_path TEXT,
                        scenario_description TEXT
                    );

                    CREATE TABLE IF NOT EXISTS events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp TEXT NOT NULL,
                        event_type TEXT NOT NULL,
                        node_id TEXT,
                        height INTEGER,
                        tip_hash TEXT,
                        role TEXT,
                        built_by TEXT,
                        nonce INTEGER,
                        tx_count INTEGER,
                        reason TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_events_timestamp ON events(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_events_type ON events(event_type);
                    CREATE INDEX IF NOT EXISTS idx_events_node ON events(node_id);

                    CREATE TABLE IF NOT EXISTS audits (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp TEXT NOT NULL,
                        state TEXT NOT NULL,
                        chains_converged INTEGER NOT NULL,
                        all_chains_valid INTEGER NOT NULL,
                        network_making_progress INTEGER NOT NULL,
                        blocks_observed INTEGER NOT NULL,
                        blocks_observed_since_previous_audit INTEGER NOT NULL,
                        invalid_state_while_producing_blocks INTEGER NOT NULL,
                        min_height INTEGER NOT NULL,
                        max_height INTEGER NOT NULL,
                        common_tip_hash TEXT,
                        explanation TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_audits_timestamp ON audits(timestamp);

                    CREATE TABLE IF NOT EXISTS audit_nodes (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        audit_id INTEGER NOT NULL REFERENCES audits(id),
                        node_id TEXT NOT NULL,
                        height INTEGER NOT NULL,
                        tip_hash TEXT,
                        structurally_valid INTEGER NOT NULL,
                        reason TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_audit_nodes_audit ON audit_nodes(audit_id);
                    CREATE INDEX IF NOT EXISTS idx_audit_nodes_node ON audit_nodes(node_id);
                ";
                schema.ExecuteNonQuery();
            }

            using var runInfo = _connection.CreateCommand();
            runInfo.CommandText = @"
                INSERT INTO run_info (id, started_at, port, scenario_path, scenario_description)
                VALUES (1, $startedAt, $port, $scenarioPath, $scenarioDescription)
                ON CONFLICT(id) DO UPDATE SET
                    started_at = excluded.started_at,
                    port = excluded.port,
                    scenario_path = excluded.scenario_path,
                    scenario_description = excluded.scenario_description;
            ";
            runInfo.Parameters.AddWithValue("$startedAt", DateTime.UtcNow.ToString("O"));
            runInfo.Parameters.AddWithValue("$port", port);
            runInfo.Parameters.AddWithValue("$scenarioPath", (object?)scenarioPath ?? DBNull.Value);
            runInfo.Parameters.AddWithValue("$scenarioDescription", (object?)scenarioDescription ?? DBNull.Value);
            runInfo.ExecuteNonQuery();
        }

        public void InsertEvent(
            DateTime timestamp,
            string eventType,
            string? nodeId = null,
            int? height = null,
            string? tipHash = null,
            string? role = null,
            string? builtBy = null,
            long? nonce = null,
            int? txCount = null,
            string? reason = null)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO events (timestamp, event_type, node_id, height, tip_hash, role, built_by, nonce, tx_count, reason)
                    VALUES ($timestamp, $eventType, $nodeId, $height, $tipHash, $role, $builtBy, $nonce, $txCount, $reason);
                ";
                cmd.Parameters.AddWithValue("$timestamp", timestamp.ToString("O"));
                cmd.Parameters.AddWithValue("$eventType", eventType);
                cmd.Parameters.AddWithValue("$nodeId", (object?)nodeId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$height", (object?)height ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tipHash", (object?)tipHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$builtBy", (object?)builtBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$nonce", (object?)nonce ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$txCount", (object?)txCount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertAudit(WatcherSnapshot snapshot)
        {
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();

                long auditId;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO audits (
                            timestamp, state, chains_converged, all_chains_valid, network_making_progress,
                            blocks_observed, blocks_observed_since_previous_audit, invalid_state_while_producing_blocks,
                            min_height, max_height, common_tip_hash, explanation
                        ) VALUES (
                            $timestamp, $state, $chainsConverged, $allChainsValid, $networkMakingProgress,
                            $blocksObserved, $blocksObservedSincePreviousAudit, $invalidStateWhileProducingBlocks,
                            $minHeight, $maxHeight, $commonTipHash, $explanation
                        );
                        SELECT last_insert_rowid();
                    ";
                    cmd.Parameters.AddWithValue("$timestamp", snapshot.Timestamp.ToString("O"));
                    cmd.Parameters.AddWithValue("$state", snapshot.State.ToString());
                    cmd.Parameters.AddWithValue("$chainsConverged", snapshot.ChainsConverged ? 1 : 0);
                    cmd.Parameters.AddWithValue("$allChainsValid", snapshot.AllChainsValid ? 1 : 0);
                    cmd.Parameters.AddWithValue("$networkMakingProgress", snapshot.NetworkIsMakingProgress ? 1 : 0);
                    cmd.Parameters.AddWithValue("$blocksObserved", snapshot.BlocksObserved);
                    cmd.Parameters.AddWithValue("$blocksObservedSincePreviousAudit", snapshot.BlocksObservedSincePreviousAudit);
                    cmd.Parameters.AddWithValue("$invalidStateWhileProducingBlocks", snapshot.InvalidStateWhileProducingBlocks ? 1 : 0);
                    cmd.Parameters.AddWithValue("$minHeight", snapshot.MinHeight);
                    cmd.Parameters.AddWithValue("$maxHeight", snapshot.MaxHeight);
                    cmd.Parameters.AddWithValue("$commonTipHash", (object?)snapshot.CommonTipHash ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$explanation", (object?)snapshot.Explanation ?? DBNull.Value);
                    auditId = (long)cmd.ExecuteScalar()!;
                }

                foreach (var node in snapshot.Nodes)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO audit_nodes (audit_id, node_id, height, tip_hash, structurally_valid, reason)
                        VALUES ($auditId, $nodeId, $height, $tipHash, $structurallyValid, $reason);
                    ";
                    cmd.Parameters.AddWithValue("$auditId", auditId);
                    cmd.Parameters.AddWithValue("$nodeId", node.NodeId);
                    cmd.Parameters.AddWithValue("$height", node.Height);
                    cmd.Parameters.AddWithValue("$tipHash", (object?)node.TipHash ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$structurallyValid", node.StructurallyValid ? 1 : 0);
                    cmd.Parameters.AddWithValue("$reason", (object?)node.Reason ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        /// <summary>
        /// Blocks actually mined, per node, across this run's whole history — grouped by the
        /// mining node's own id, not the block's <c>built_by</c> (which an impersonator sets
        /// to whatever name it's framing).
        /// </summary>
        public Dictionary<string, int> GetWinCountsByNode()
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT node_id, COUNT(*)
                    FROM events
                    WHERE event_type = 'block-built' AND node_id IS NOT NULL
                    GROUP BY node_id;
                ";
                using var reader = cmd.ExecuteReader();
                var result = new Dictionary<string, int>();
                while (reader.Read())
                    result[reader.GetString(0)] = reader.GetInt32(1);
                return result;
            }
        }

        /// <summary>The most recent reorganization events, newest first.</summary>
        public List<ReorganizationEvent> GetRecentReorganizations(int limit)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT timestamp, node_id, reason
                    FROM events
                    WHERE event_type = 'reorganization'
                    ORDER BY id DESC
                    LIMIT $limit;
                ";
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                var result = new List<ReorganizationEvent>();
                while (reader.Read())
                    result.Add(new ReorganizationEvent
                    {
                        Timestamp = reader.GetString(0),
                        NodeId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        Reason = reader.IsDBNull(2) ? "" : reader.GetString(2)
                    });
                return result;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _connection.Dispose();
            }
        }
    }
}
