using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // SQLite-backed persistence for one node's local chain. Each node gets
    // its own blockchain.db under nodes/<node-id>/, with two tables:
    //
    //   blocks       - one row per block, PK'd by height (idx)
    //   transactions - one row per transaction, FK'd to blocks(idx), with a
    //                  position column preserving in-block order (order is
    //                  part of Block.ComputeHash's payload, so it must
    //                  round-trip exactly)
    //
    // Amount is stored as TEXT (decimal.ToString(InvariantCulture)/decimal.Parse)
    // rather than a numeric SQLite column, since SQLite has no native decimal
    // type and REAL (double) would silently lose precision on a ledger.
    //
    // Sync() is append-only in the common case (new blocks land as INSERTs,
    // nothing already on disk is touched) and, on a reorg, only replaces the
    // records at and after the height where the new chain actually diverges
    // from what's persisted — see its comment below.
    // ------------------------------------------------------------------
    public sealed class BlockchainStore : IDisposable
    {
        private readonly object _lock = new();
        private readonly SqliteConnection _connection;

        // Mirrors the hash persisted at each height (index == block height),
        // kept in memory so Sync() can find a reorg's divergence point
        // without re-reading the database on every call.
        private readonly List<string> _persistedHashes = new();

        public BlockchainStore(string dbPath)
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
                    CREATE TABLE IF NOT EXISTS blocks (
                        idx INTEGER PRIMARY KEY,
                        timestamp TEXT NOT NULL,
                        previous_hash TEXT NOT NULL,
                        hash TEXT NOT NULL,
                        built_by TEXT NOT NULL,
                        signature TEXT NOT NULL,
                        target TEXT NOT NULL,
                        nonce INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS transactions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        block_index INTEGER NOT NULL REFERENCES blocks(idx) ON DELETE CASCADE,
                        position INTEGER NOT NULL,
                        sender TEXT NOT NULL,
                        receiver TEXT NOT NULL,
                        amount TEXT NOT NULL,
                        timestamp TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_transactions_block ON transactions(block_index);
                ";
                schema.ExecuteNonQuery();
            }

            using var hashes = _connection.CreateCommand();
            hashes.CommandText = "SELECT hash FROM blocks ORDER BY idx;";
            using var reader = hashes.ExecuteReader();
            while (reader.Read())
                _persistedHashes.Add(reader.GetString(0));
        }

        // Used once at startup to resume: returns the full saved chain in
        // height order, or null if nothing has been persisted yet.
        public List<Block>? LoadAll()
        {
            lock (_lock)
            {
                if (_persistedHashes.Count == 0) return null;

                var blocksByIndex = new Dictionary<int, Block>();
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT idx, timestamp, previous_hash, hash, built_by, signature, target, nonce FROM blocks ORDER BY idx;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var block = new Block
                        {
                            Index = reader.GetInt32(0),
                            Timestamp = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                            PreviousHash = reader.GetString(2),
                            Hash = reader.GetString(3),
                            BuiltBy = reader.GetString(4),
                            Signature = reader.GetString(5),
                            Target = reader.GetString(6),
                            Nonce = reader.GetInt64(7),
                            Transactions = new List<Transaction>()
                        };
                        blocksByIndex[block.Index] = block;
                    }
                }

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT block_index, sender, receiver, amount, timestamp FROM transactions ORDER BY block_index, position;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var blockIndex = reader.GetInt32(0);
                        if (!blocksByIndex.TryGetValue(blockIndex, out var block)) continue;
                        block.Transactions.Add(new Transaction
                        {
                            From = reader.GetString(1),
                            To = reader.GetString(2),
                            Amount = decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                            Timestamp = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                        });
                    }
                }

                return blocksByIndex.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            }
        }

        // Reconciles the database with the given in-memory chain snapshot.
        // Finds the first height (if any) where the snapshot's hash differs
        // from what's already persisted there — everything below that height
        // is untouched, since a block, once mined, never changes in place.
        // The common case (blocks only ever appended) has no such height at
        // all: this is a pure INSERT of the new tail. A reorg trims exactly
        // the persisted records at and after the divergence point and
        // reinserts the snapshot's replacement blocks from there on, rather
        // than clearing and rewriting the whole table.
        public void Sync(List<Block> snapshot)
        {
            lock (_lock)
            {
                var divergeAt = 0;
                var sharedLength = Math.Min(_persistedHashes.Count, snapshot.Count);
                while (divergeAt < sharedLength && _persistedHashes[divergeAt] == snapshot[divergeAt].Hash)
                    divergeAt++;

                if (divergeAt == _persistedHashes.Count && divergeAt == snapshot.Count)
                    return; // nothing changed since the last sync

                using var transaction = _connection.BeginTransaction();

                if (divergeAt < _persistedHashes.Count)
                {
                    using var trim = _connection.CreateCommand();
                    trim.Transaction = transaction;
                    trim.CommandText = "DELETE FROM transactions WHERE block_index >= $from; DELETE FROM blocks WHERE idx >= $from;";
                    trim.Parameters.AddWithValue("$from", divergeAt);
                    trim.ExecuteNonQuery();

                    _persistedHashes.RemoveRange(divergeAt, _persistedHashes.Count - divergeAt);
                }

                for (var i = divergeAt; i < snapshot.Count; i++)
                {
                    InsertBlock(transaction, snapshot[i]);
                    _persistedHashes.Add(snapshot[i].Hash);
                }

                transaction.Commit();
            }
        }

        private void InsertBlock(SqliteTransaction transaction, Block block)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT INTO blocks (idx, timestamp, previous_hash, hash, built_by, signature, target, nonce)
                    VALUES ($idx, $timestamp, $previousHash, $hash, $builtBy, $signature, $target, $nonce);
                ";
                cmd.Parameters.AddWithValue("$idx", block.Index);
                cmd.Parameters.AddWithValue("$timestamp", block.Timestamp.ToString("O"));
                cmd.Parameters.AddWithValue("$previousHash", block.PreviousHash);
                cmd.Parameters.AddWithValue("$hash", block.Hash);
                cmd.Parameters.AddWithValue("$builtBy", block.BuiltBy);
                cmd.Parameters.AddWithValue("$signature", block.Signature);
                cmd.Parameters.AddWithValue("$target", block.Target);
                cmd.Parameters.AddWithValue("$nonce", block.Nonce);
                cmd.ExecuteNonQuery();
            }

            for (var i = 0; i < block.Transactions.Count; i++)
            {
                var tx = block.Transactions[i];
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT INTO transactions (block_index, position, sender, receiver, amount, timestamp)
                    VALUES ($blockIndex, $position, $sender, $receiver, $amount, $timestamp);
                ";
                cmd.Parameters.AddWithValue("$blockIndex", block.Index);
                cmd.Parameters.AddWithValue("$position", i);
                cmd.Parameters.AddWithValue("$sender", tx.From);
                cmd.Parameters.AddWithValue("$receiver", tx.To);
                cmd.Parameters.AddWithValue("$amount", tx.Amount.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$timestamp", tx.Timestamp.ToString("O"));
                cmd.ExecuteNonQuery();
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
