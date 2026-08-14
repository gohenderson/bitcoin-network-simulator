using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Research watcher plumbing — tracks build/accept/reject/reorg events across
    // the whole simulated network and periodically audits every node's /chain
    // endpoint to report on convergence, not just what any single node believes.
    // ------------------------------------------------------------------

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
        public string Explanation { get; init; } = "";
    }

    public sealed class ChainWatcher
    {
        // Every node shares this one port (see NetworkServer.cs); a node is
        // addressed by id in the URL path, e.g. http://localhost:5000/000-alpha/chain.
        private readonly int _port;
        private List<string> _nodeIds;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private readonly object _lock = new();
        private readonly WatcherStore _store;
        private WatcherSnapshot? _lastSnapshot;
        private int _blocksObserved;
        private int _reorganizationsObserved;
        private int _rejectedBlocksObserved;

        public ChainWatcher(int port, List<string> nodeIds, WatcherStore store)
        {
            _port = port;
            _nodeIds = new List<string>(nodeIds);
            _store = store;
        }

        public void AddNode(string nodeId)
        {
            lock (_lock)
            {
                _nodeIds.Add(nodeId);
            }
        }

        // Called by NodeNetwork.RemoveNode (churn) so AuditAsync stops
        // polling a departed node's /chain endpoint — otherwise every future
        // audit would find it 404ing and permanently mark it structurally
        // invalid, skewing AllChainsValid/ChainsConverged.
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

        public async Task<WatcherSnapshot> AuditAsync(bool emitTransitions = true)
        {
            List<string> nodeIds;
            lock (_lock) { nodeIds = new List<string>(_nodeIds); }

            var audits = new List<NodeAudit>();

            foreach (var nodeId in nodeIds)
            {
                try
                {
                    using var response = await _http.GetAsync($"http://localhost:{_port}/{nodeId}/chain");
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        audits.Add(new NodeAudit { NodeId = nodeId, StructurallyValid = false, Reason = $"/chain HTTP {(int)response.StatusCode}" });
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
                }
                catch (Exception ex)
                {
                    audits.Add(new NodeAudit { NodeId = nodeId, StructurallyValid = false, Reason = $"watcher could not inspect node: {ex.Message}" });
                }
            }

            var allValid = audits.Count == nodeIds.Count && audits.All(a => a.StructurallyValid);
            var minHeight = audits.Count == 0 ? 0 : audits.Min(a => a.Height);
            var maxHeight = audits.Count == 0 ? 0 : audits.Max(a => a.Height);
            var distinctTips = audits.Where(a => a.StructurallyValid).Select(a => a.TipHash).Distinct().ToList();
            var converged = allValid && distinctTips.Count == 1;
            int observedBlocks;
            WatcherSnapshot? previousSnapshot;
            lock (_lock)
            {
                observedBlocks = _blocksObserved;
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

    // ------------------------------------------------------------------
    // SQLite-backed persistence for ChainWatcher. One watcher.db lives in
    // each run's result folder (see RunRootDir in Program.cs), containing:
    //
    //   run_info    - one row identifying this run (started_at, port, scenario)
    //   events      - the append-only event log (block-built/accepted/rejected,
    //                 reorganizations, network state transitions), with fields
    //                 like nonce, role, and tx_count broken out into real,
    //                 directly queryable columns.
    //   audits      - one row per periodic convergence audit (ChainWatcher.AuditAsync)
    //   audit_nodes - each audited node's per-audit height/tip/validity, FK'd to audits
    //
    // All timestamps are stored as UTC ISO-8601 ("O" format), which sorts
    // lexicographically, so time-range queries need no date parsing.
    //
    // A single SqliteConnection is reused for the run's lifetime, with all
    // access serialized through _lock — SQLite (even in WAL mode) doesn't
    // support concurrent use of one connection from multiple threads, and
    // ChainWatcher's Observe*/AuditAsync methods can be called concurrently
    // from many nodes' request handlers plus the audit loop.
    // ------------------------------------------------------------------
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

        public void Dispose()
        {
            lock (_lock)
            {
                _connection.Dispose();
            }
        }
    }
}
