using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Data model
    // ------------------------------------------------------------------

    public class Transaction
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public decimal Amount { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class Block
    {
        public int Index { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public List<Transaction> Transactions { get; set; } = new();
        public string PreviousHash { get; set; } = "";
        public string Hash { get; set; } = "";
        // The node that "won" the race and built this block.
        public string BuiltBy { get; set; } = "";

        // ECDSA signature over this block's Hash, produced with BuiltBy's own
        // private signing key — see NodeIdentityRegistry and the "Signed
        // blocks" note in README.md. Deliberately excluded from ComputeHash's
        // payload (it's computed FROM the hash, so including it would be
        // circular); a node can still put any name it likes in BuiltBy, but
        // it can only produce a Signature that verifies against the key
        // actually registered for that name if it genuinely holds that
        // name's private key.
        public string Signature { get; set; } = "";

        // Proof-of-work fields. Target is the PUBLIC 256-bit ceiling this block's
        // hash must be less than or equal to — carried right in the header, so any
        // peer can check it without asking anyone. Nonce is the value a miner
        // searched over to find a hash meeting that target. Both are part of the
        // hashed payload below, so tampering with either after the fact breaks the
        // hash-integrity check during validation.
        public string Target { get; set; } = "";
        public long Nonce { get; set; }

        public string ComputeHash()
        {
            var payload = $"{Index}|{Timestamp:O}|{PreviousHash}|{BuiltBy}|{Target}|{Nonce}|" +
                          string.Join(",", Transactions.Select(t => $"{t.From}>{t.To}:{t.Amount}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }
    }

    // ------------------------------------------------------------------
    // Proof-of-work math: target encoding, hash-vs-target comparison, and the
    // deterministic retarget rule every node uses to independently compute what
    // a block's target SHOULD be, purely from public chain history. Nobody
    // announces or holds this as a secret — it's a pure function of data every
    // node already has.
    // ------------------------------------------------------------------

    public static class ProofOfWork
    {
        // How often (in blocks) to retarget, and how long a block "should" take
        // on average. Default to real Bitcoin's own numbers (2016-block / ~2-week
        // retarget window, 10-minute block target) — scenario-configurable via
        // RetargetIntervalBlocks/TargetSecondsPerBlock (see "Scenarios" in
        // README.md) for a faster-paced run. Consensus-critical: every node
        // uses these same two values to independently recompute the SAME
        // expected target for a given height, so they're fixed for the whole
        // run (resolved once from phase 0, not phase-mutable like growth/churn
        // — see the note atop Scenario.cs) rather than something that could
        // drift node to node or block to block.
        public const int DefaultRetargetIntervalBlocks = 2016;
        public static int RetargetIntervalBlocks = DefaultRetargetIntervalBlocks;
        public const double DefaultTargetSecondsPerBlock = 600.0;
        public static double TargetSecondsPerBlock = DefaultTargetSecondsPerBlock;

        // Bitcoin-style clamp so a single retarget can't swing wildly in either
        // direction, even if the last interval's timing was a fluke — already
        // real Bitcoin's own clamp, scenario-configurable via
        // MinAdjustmentFactor/MaxAdjustmentFactor same as above.
        public const double DefaultMinAdjustmentFactor = 0.25;
        public static double MinAdjustmentFactor = DefaultMinAdjustmentFactor;
        public const double DefaultMaxAdjustmentFactor = 4.0;
        public static double MaxAdjustmentFactor = DefaultMaxAdjustmentFactor;

        // Higher = harder (lower per-attempt success probability, slower
        // blocks). Lower = easier. Deliberately NOT matched to real Bitcoin's
        // actual difficulty — real difficulty would make a block practically
        // unmineable here, since MineBlock only gets a bounded number of
        // attempts per turn (a node's HashPower — see the "Mining" note in
        // README.md) rather than the unbounded, massively-parallel search a
        // real miner performs. At the default shift 8, a single attempt
        // succeeds with probability 1/256, so a regular (HashPower 1) node
        // still has a real, if modest, chance each turn, while a node with
        // HashPower 1000 succeeds on the vast majority of its turns — exactly
        // the "1000x more likely to win" effect simulated hash power is meant
        // to produce. Scenario-configurable via InitialDifficultyShift, same
        // fixed-for-the-whole-run rule as above — raising it combined with
        // real RetargetIntervalBlocks/TargetSecondsPerBlock is itself a
        // network-effect worth observing: retargeting on Bitcoin's real
        // cadence against hash power that isn't Bitcoin's real magnitude
        // pushes difficulty to keep climbing every interval, since blocks
        // keep arriving faster than the 10-minute goal expects.
        public const int DefaultInitialDifficultyShift = 8;
        public static int InitialDifficultyShift = DefaultInitialDifficultyShift;

        public static readonly BigInteger MaxTarget = (BigInteger.One << 256) - 1;
        // Computed, not cached at type-init, so setting InitialDifficultyShift
        // from a scenario before this is first read takes effect.
        public static BigInteger InitialTarget => MaxTarget >> InitialDifficultyShift;

        public static BigInteger HashToBigInteger(string hex)
        {
            var bytes = Convert.FromHexString(hex);
            return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        }

        public static string TargetToHex(BigInteger target)
        {
            var bytes = target.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (bytes.Length < 32)
            {
                var padded = new byte[32];
                Array.Copy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
                bytes = padded;
            }
            else if (bytes.Length > 32)
            {
                bytes = bytes[^32..];
            }
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static bool MeetsTarget(string hashHex, string targetHex)
        {
            return HashToBigInteger(hashHex) <= HashToBigInteger(targetHex);
        }

        // Deterministically derives the target the NEXT block (at height =
        // ancestors.Count) must satisfy, purely from public chain history — no
        // secret, nothing to trust, nothing to announce. Every node computes this
        // identically, the same way every real Bitcoin node independently
        // recomputes the same expected difficulty from block timestamps.
        //
        // Known quirk, left in deliberately rather than engineered around:
        // genesis has a fixed, hardcoded timestamp (see CreateGenesisBlock), so
        // the very FIRST retarget interval spans from that fixed point to
        // whenever the run actually started — a huge apparent elapsed time.
        // That first retarget will almost always saturate the
        // MaxAdjustmentFactor clamp (target gets 4x easier). Every retarget
        // after that behaves normally, based purely on real elapsed mining
        // time between real blocks.
        public static string ComputeExpectedTargetHex(List<Block> ancestors)
        {
            if (ancestors == null || ancestors.Count == 0)
                return TargetToHex(InitialTarget);

            var nextHeight = ancestors.Count;
            var parent = ancestors[^1];

            if (nextHeight < RetargetIntervalBlocks || nextHeight % RetargetIntervalBlocks != 0)
                return parent.Target; // no adjustment due yet — inherit parent's target

            var intervalStart = ancestors[nextHeight - RetargetIntervalBlocks];
            var actualSeconds = Math.Max(1.0, (parent.Timestamp - intervalStart.Timestamp).TotalSeconds);
            var expectedSeconds = RetargetIntervalBlocks * TargetSecondsPerBlock;
            var ratio = Math.Clamp(actualSeconds / expectedSeconds, MinAdjustmentFactor, MaxAdjustmentFactor);

            var parentTarget = HashToBigInteger(parent.Target);
            var ratioMicros = (long)Math.Round(ratio * 1_000_000.0);
            var scaled = parentTarget * ratioMicros / 1_000_000;

            if (scaled < BigInteger.One) scaled = BigInteger.One;
            if (scaled > MaxTarget) scaled = MaxTarget;

            return TargetToHex(scaled);
        }
    }

    // ------------------------------------------------------------------
    // Coin issuance: a coinbase transaction (From == CoinbaseSender) is how new
    // coins enter existence, exactly one per block, paid to whoever built it.
    // The nominal reward halves every HalvingIntervalBlocks, and the running
    // total ever minted across the whole chain is hard-capped at MaxSupply —
    // both computed the same deterministic way ProofOfWork.ComputeExpectedTargetHex
    // computes its target: purely from public chain history, so every node
    // independently verifies the SAME expected reward for any given block
    // without trusting the builder's claim.
    //
    // ARITHMETIC NOTE, worth being upfront about: the defaults below are real
    // Bitcoin's own constants, tuned so the reward series converges to
    // exactly MaxSupply: HalvingIntervalBlocks * InitialBlockReward *
    // (1 + 1/2 + 1/4 + ...) = 210,000 * 50 * 2 = 21,000,000 — so the cap
    // actually binds (asymptotically) at these defaults, not just in theory.
    // All three are scenario-configurable (see "Scenarios" in README.md) for
    // a faster-paced run — e.g. halving every 210 blocks instead of 210,000
    // reaches the same-shaped reward curve 1000x sooner, but then the series
    // only converges to 210 * 50 * 2 = 21,000, so MaxSupply would need
    // shrinking to match if you want the cap to actually bind again.
    // Consensus-critical, same fixed-for-the-whole-run rule as
    // ProofOfWork's RetargetIntervalBlocks/TargetSecondsPerBlock above.
    public static class Economics
    {
        public const string CoinbaseSender = "coinbase";
        public const decimal DefaultInitialBlockReward = 50m;
        public static decimal InitialBlockReward = DefaultInitialBlockReward;
        public const int DefaultHalvingIntervalBlocks = 210_000;
        public static int HalvingIntervalBlocks = DefaultHalvingIntervalBlocks;
        public const decimal DefaultMaxSupply = 21_000_000m;
        public static decimal MaxSupply = DefaultMaxSupply;

        // Schedule-only reward for a given height, ignoring the max-supply cap.
        public static decimal NominalBlockReward(int height)
        {
            if (height <= 0) return 0m; // genesis pays no reward

            var halvings = height / HalvingIntervalBlocks;
            if (halvings >= 50) return 0m; // decayed to zero long before this many halvings

            var divisor = BigInteger.Pow(2, halvings);
            return InitialBlockReward / (decimal)divisor;
        }

        // Sums every coinbase-labeled transaction across the given chain prefix —
        // i.e. everything ever minted so far, purely from public chain data.
        public static decimal TotalMintedSoFar(List<Block> ancestors)
        {
            decimal total = 0m;
            foreach (var block in ancestors)
                foreach (var tx in block.Transactions)
                    if (tx.From == CoinbaseSender)
                        total += tx.Amount;
            return total;
        }

        // The actual reward a block at this height may claim: the schedule's
        // nominal reward, clamped so the running total minted across the whole
        // chain never exceeds MaxSupply.
        public static decimal ComputeBlockReward(List<Block> ancestors, int height)
        {
            var nominal = NominalBlockReward(height);
            if (nominal <= 0m) return 0m;

            var mintedSoFar = TotalMintedSoFar(ancestors);
            var remaining = MaxSupply - mintedSoFar;
            if (remaining <= 0m) return 0m;

            return nominal > remaining ? remaining : nominal;
        }
    }

    // ------------------------------------------------------------------
    // Balance tracking: derives every account's current balance purely from
    // public chain history, exactly the same "recompute it yourself, don't
    // trust a claim" pattern ProofOfWork and Economics use above. This is
    // what lets ValidateChain (and a miner's own mempool selection) catch a
    // sender trying to spend coins they don't have, or spend the same coins
    // twice.
    // ------------------------------------------------------------------
    public static class Ledger
    {
        public static Dictionary<string, decimal> ComputeBalances(IEnumerable<Block> chain)
        {
            var balances = new Dictionary<string, decimal>();
            foreach (var block in chain)
            {
                foreach (var tx in block.Transactions)
                {
                    if (tx.From != Economics.CoinbaseSender)
                        balances[tx.From] = balances.GetValueOrDefault(tx.From) - tx.Amount;
                    balances[tx.To] = balances.GetValueOrDefault(tx.To) + tx.Amount;
                }
            }
            return balances;
        }

        public static decimal GetBalance(IEnumerable<Block> chain, string account) =>
            ComputeBalances(chain).GetValueOrDefault(account);
    }

    // Thread-safe append-only chain. Each "node" below keeps its OWN copy of
    // a Blockchain to simulate a real distributed system where nodes can
    // (and, in this naive design, do) disagree.
    public class Blockchain
    {
        private readonly object _lock = new();
        public List<Block> Blocks { get; private set; } = new();

        public Blockchain()
        {
            Blocks.Add(CreateGenesisBlock());
        }

        // Genesis must be byte-for-byte identical across every node, or their chains
        // can never agree on a shared "block #0" and every subsequent block gets
        // rejected everywhere except on the node that built it. That means NO
        // DateTime.UtcNow here — timestamps captured milliseconds apart on
        // different nodes would hash differently and break consensus before it
        // even starts. Genesis is exempt from proof-of-work (it's the fixed,
        // universally-agreed starting point every node is hardcoded to trust, the
        // same way real Bitcoin's genesis block is a checkpoint, not something
        // your own node re-verifies by mining).
        private static Block CreateGenesisBlock()
        {
            var genesis = new Block
            {
                Index = 0,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                PreviousHash = "0",
                BuiltBy = "genesis",
                Target = ProofOfWork.TargetToHex(ProofOfWork.InitialTarget),
                Nonce = 0,
                Transactions = new List<Transaction>()
            };
            genesis.Hash = genesis.ComputeHash();
            return genesis;
        }

        public Block Latest
        {
            get { lock (_lock) { return Blocks[^1]; } }
        }

        // Used only for the local build path (a node building its own block never
        // needs to "validate" itself — it just appends what it made).
        public void AppendTrusting(Block block)
        {
            lock (_lock)
            {
                Blocks.Add(block);
            }
        }

        // Validates an incoming chain block-by-block. What this DOES catch:
        //   - structural corruption / tampering (recomputed hash must match claimed hash)
        //   - wrong parent (PreviousHash must match the previous block's hash)
        //   - wrong height (Index must be sequential)
        //   - malformed transactions (basic sanity checks)
        //   - insufficient proof-of-work: the declared Target must match what's
        //     independently recomputed from prior block timestamps (nobody gets
        //     to just claim an easy target), AND the block's hash must actually
        //     satisfy that target
        //   - incorrect coinbase reward: at most one coinbase-labeled transaction
        //     per block, and its amount must exactly match what every node
        //     independently computes as the correct reward for that height,
        //     respecting both the halving schedule and the max-supply cap
        //   - insufficient balance / double-spends: every non-coinbase transaction
        //     is checked against a running balance derived purely from chain
        //     history up to that exact point (see Ledger.ComputeBalances) — a
        //     sender can never spend more than they actually have, and a second
        //     spend of the same coins finds the balance already gone
        //   - a node lying about who built it: BuiltBy must have a registered
        //     signing key (see NodeIdentityRegistry) and the block's Signature
        //     must actually verify against that key — see the "Signed
        //     blocks" note in README.md
        // Being selected to build a block genuinely costs something real:
        // computational search work.
        private static (bool Ok, string Reason) ValidateChain(List<Block> candidate)
        {
            if (candidate == null || candidate.Count == 0)
                return (false, "candidate chain is empty");

            if (candidate[0].Index != 0)
                return (false, "candidate chain does not start at genesis");

            // Running balance derived purely from chain history as we walk
            // forward — this is what lets the per-transaction check below catch
            // both an outright insufficient-balance spend and a double-spend
            // (the second attempt simply finds the balance already gone).
            var balances = new Dictionary<string, decimal>();

            for (int i = 0; i < candidate.Count; i++)
            {
                var block = candidate[i];

                if (block.Transactions == null)
                    return (false, $"block #{block.Index} transactions list is null");

                if (block.Index != i)
                    return (false, $"block position {i} has index {block.Index}");

                if (i == 0)
                {
                    if (block.PreviousHash != "0")
                        return (false, "candidate genesis has an invalid previous hash");
                }
                else
                {
                    var previous = candidate[i - 1];
                    if (block.PreviousHash != previous.Hash)
                        return (false, $"block #{block.Index} has previous-hash mismatch");

                    var ancestors = candidate.GetRange(0, i); // blocks 0..i-1, i.e. up through parent

                    var expectedTarget = ProofOfWork.ComputeExpectedTargetHex(ancestors);
                    if (block.Target != expectedTarget)
                        return (false, $"block #{block.Index} declares an incorrect target — expected {expectedTarget[..8]}..., " +
                            $"got {(block.Target.Length >= 8 ? block.Target[..8] : block.Target)}... " +
                            "(target must match what every node independently computes from prior block timestamps)");

                    if (!ProofOfWork.MeetsTarget(block.Hash, block.Target))
                        return (false, $"block #{block.Index} hash does not satisfy its declared target — not a valid proof of work");

                    var builderKey = NodeIdentityRegistry.GetPublicKey(block.BuiltBy);
                    if (builderKey == null)
                        return (false, $"block #{block.Index} claims BuiltBy '{block.BuiltBy}', which has no registered signing key");
                    if (!NodeIdentityRegistry.Verify(builderKey, block.Hash, block.Signature))
                        return (false, $"block #{block.Index} signature does not verify against the registered key for '{block.BuiltBy}' — possible impersonation");

                    var coinbaseTxs = block.Transactions.Where(t => t.From == Economics.CoinbaseSender).ToList();
                    if (coinbaseTxs.Count > 1)
                        return (false, $"block #{block.Index} contains {coinbaseTxs.Count} coinbase transactions — only one is allowed per block");

                    var expectedReward = Economics.ComputeBlockReward(ancestors, block.Index);
                    if (expectedReward > 0m)
                    {
                        if (coinbaseTxs.Count != 1)
                            return (false, $"block #{block.Index} is missing its coinbase transaction (expected reward {expectedReward})");
                        if (coinbaseTxs[0].Amount != expectedReward)
                            return (false, $"block #{block.Index} coinbase amount {coinbaseTxs[0].Amount} does not match the independently computed reward {expectedReward} for this height");
                    }
                    else if (coinbaseTxs.Count != 0)
                    {
                        return (false, $"block #{block.Index} includes a coinbase transaction, but the reward at this height has decayed to zero or the {Economics.MaxSupply}-coin max supply has already been reached");
                    }
                }

                foreach (var tx in block.Transactions)
                {
                    if (string.IsNullOrWhiteSpace(tx.From) || string.IsNullOrWhiteSpace(tx.To))
                        return (false, $"block #{block.Index} contains a transaction missing From/To");

                    if (tx.Amount <= 0)
                        return (false, $"block #{block.Index} contains a non-positive transaction amount: {tx.Amount}");

                    if (tx.From == Economics.CoinbaseSender)
                    {
                        balances[tx.To] = balances.GetValueOrDefault(tx.To) + tx.Amount;
                    }
                    else
                    {
                        var available = balances.GetValueOrDefault(tx.From);
                        if (tx.Amount > available)
                            return (false, $"block #{block.Index} contains a transaction spending {tx.Amount} from '{tx.From}', " +
                                $"who only has a balance of {available} at that point in the chain — insufficient funds or a double-spend");

                        balances[tx.From] = available - tx.Amount;
                        balances[tx.To] = balances.GetValueOrDefault(tx.To) + tx.Amount;
                    }
                }

                var recomputed = block.ComputeHash();
                if (recomputed != block.Hash)
                    return (false, $"block #{block.Index} hash does not match its contents");
            }

            return (true, "ok");
        }

        public static (bool Ok, string Reason) ValidateSnapshot(List<Block> candidate)
        {
            return ValidateChain(candidate);
        }

        public (bool Ok, string Reason) TryAppend(Block block)
        {
            lock (_lock)
            {
                var tip = Blocks[^1];

                if (block.Index != tip.Index + 1)
                    return (false, $"expected index {tip.Index + 1}, got {block.Index}");

                if (block.PreviousHash != tip.Hash)
                    return (false, $"previous hash mismatch: expected {tip.Hash}, got {block.PreviousHash}");

                var candidate = new List<Block>(Blocks) { block };
                var validation = ValidateChain(candidate);
                if (!validation.Ok)
                    return validation;

                Blocks.Add(block);
                return (true, "ok");
            }
        }

        // Fork choice rule:
        // A valid candidate chain (including every block's proof-of-work AND
        // coinbase correctness) replaces our current chain only when it is
        // strictly longer. This lets a node undo blocks it previously accepted
        // when another branch proves to be the longer valid history.
        public (bool Replaced, string Reason) TryReplaceWithLongerChain(List<Block> candidate)
        {
            lock (_lock)
            {
                var validation = ValidateChain(candidate);
                if (!validation.Ok)
                    return (false, $"candidate rejected: {validation.Reason}");

                if (candidate.Count <= Blocks.Count)
                    return (false, $"candidate is not longer (candidate={candidate.Count - 1}, local={Blocks.Count - 1})");

                if (candidate[0].Hash != Blocks[0].Hash)
                    return (false, "candidate has a different genesis block");

                Blocks = new List<Block>(candidate);
                return (true, $"replaced local chain with longer chain at height {Blocks[^1].Index}");
            }
        }

        // Used once at startup to resume a node's chain from a previously persisted
        // snapshot on disk. Accepts the saved chain only if it's structurally valid
        // (including every block's proof-of-work and coinbase correctness) AND
        // shares this build's canonical genesis block.
        public (bool Loaded, string Reason) TryLoadFrom(List<Block> candidate)
        {
            lock (_lock)
            {
                var validation = ValidateChain(candidate);
                if (!validation.Ok)
                    return (false, $"saved chain failed validation: {validation.Reason}");

                if (candidate[0].Hash != Blocks[0].Hash)
                    return (false, "saved chain has a different genesis than this build's canonical genesis");

                Blocks = new List<Block>(candidate);
                return (true, $"resumed at height {Blocks[^1].Index} ({Blocks.Count} block(s) loaded)");
            }
        }

        public List<Block> Snapshot()
        {
            lock (_lock) { return new List<Block>(Blocks); }
        }
    }

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
