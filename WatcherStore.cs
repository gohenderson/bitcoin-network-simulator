using System;
using Microsoft.Data.Sqlite;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // SQLite-backed persistence for ChainWatcher — replaces the old
    // watcher-report.json dump. One watcher.db lives in each run's result
    // folder (see RunRootDir in Program.cs), containing:
    //
    //   run_info    - one row identifying this run (started_at, port, scenario)
    //   events      - the append-only event log (block-built/accepted/rejected,
    //                 reorganizations, network state transitions), with the
    //                 fields that used to be packed into a formatted Details
    //                 string broken out into real columns so they're directly
    //                 queryable (e.g. nonce, role, tx_count) instead of needing
    //                 to be re-parsed out of text.
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
