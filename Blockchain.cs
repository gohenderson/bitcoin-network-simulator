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
    public class Transaction
    {
        /// <summary>
        /// Stable identity for relay dedup — set once at construction and preserved across
        /// JSON round-trips, but not part of <see cref="Block.ComputeHash"/>'s payload or
        /// SQLite persistence, since it's only ever needed against a live, in-process mempool
        /// during the current run.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public decimal Amount { get; set; }
        /// <summary>
        /// The coin/asset this transaction spends — the sender's own declaration of intent,
        /// required at every entry point. <see cref="Blockchain.ValidateChain"/> rejects a
        /// block outright if a transaction's declared asset doesn't match that block's own
        /// height-derived asset; ledger bookkeeping itself still derives asset from block
        /// height, not this field, the same way a block's own self-declared
        /// <see cref="Block.Rules"/> is checked against but never trusted for arithmetic.
        /// </summary>
        public string Asset { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// One block in a node's chain, including its proof-of-work fields, the builder's
    /// signature over its hash, and the consensus/economics ruleset its builder recorded
    /// using. <see cref="Signature"/> is excluded from <see cref="ComputeHash"/>'s payload
    /// since it is computed from the hash itself. <see cref="Rules"/> is informational only:
    /// a validating peer checks a block against its own <see cref="RuleSchedule"/> for that
    /// height, never against this field.
    /// </summary>
    public class Block
    {
        public int Index { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public List<Transaction> Transactions { get; set; } = new();
        public string PreviousHash { get; set; } = "";
        public string Hash { get; set; } = "";
        public string BuiltBy { get; set; } = "";
        public string Signature { get; set; } = "";
        public string Target { get; set; } = "";
        public long Nonce { get; set; }
        public ConsensusRules Rules { get; set; } = new();

        public string ComputeHash()
        {
            var payload = $"{Index}|{Timestamp:O}|{PreviousHash}|{BuiltBy}|{Target}|{Nonce}|" +
                          $"{Rules.RetargetIntervalBlocks}|{Rules.TargetSecondsPerBlock.ToString(CultureInfo.InvariantCulture)}|" +
                          $"{Rules.MinAdjustmentFactor.ToString(CultureInfo.InvariantCulture)}|{Rules.MaxAdjustmentFactor.ToString(CultureInfo.InvariantCulture)}|" +
                          $"{Rules.InitialDifficultyShift}|{Rules.InitialBlockReward.ToString(CultureInfo.InvariantCulture)}|" +
                          $"{Rules.HalvingIntervalBlocks}|{Rules.MaxSupply.ToString(CultureInfo.InvariantCulture)}|" +
                          string.Join(",", Transactions.Select(t => $"{t.From}>{t.To}:{t.Amount}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Proof-of-work math: target encoding, hash-vs-target comparison, and the deterministic
    /// retarget rule every node uses to independently compute what a block's target should be,
    /// purely from public chain history.
    /// </summary>
    public static class ProofOfWork
    {
        public const int DefaultRetargetIntervalBlocks = 2016;
        public const double DefaultTargetSecondsPerBlock = 600.0;
        public const double DefaultMinAdjustmentFactor = 0.25;
        public const double DefaultMaxAdjustmentFactor = 4.0;
        public const int DefaultInitialDifficultyShift = 8;

        public static readonly BigInteger MaxTarget = (BigInteger.One << 256) - 1;

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

        /// <summary>
        /// Deterministically derives the target the next block (at height = ancestors.Count)
        /// must satisfy, purely from public chain history and <paramref name="rules"/>.
        /// </summary>
        public static string ComputeExpectedTargetHex(List<Block> ancestors, ConsensusRules rules)
        {
            if (ancestors == null || ancestors.Count == 0)
                return TargetToHex(MaxTarget >> rules.InitialDifficultyShift);

            var nextHeight = ancestors.Count;
            var parent = ancestors[^1];

            if (nextHeight < rules.RetargetIntervalBlocks || nextHeight % rules.RetargetIntervalBlocks != 0)
                return parent.Target;

            var intervalStart = ancestors[nextHeight - rules.RetargetIntervalBlocks];
            var actualSeconds = Math.Max(1.0, (parent.Timestamp - intervalStart.Timestamp).TotalSeconds);
            var expectedSeconds = rules.RetargetIntervalBlocks * rules.TargetSecondsPerBlock;
            var ratio = Math.Clamp(actualSeconds / expectedSeconds, rules.MinAdjustmentFactor, rules.MaxAdjustmentFactor);

            var parentTarget = HashToBigInteger(parent.Target);
            var ratioMicros = (long)Math.Round(ratio * 1_000_000.0);
            var scaled = parentTarget * ratioMicros / 1_000_000;

            if (scaled < BigInteger.One) scaled = BigInteger.One;
            if (scaled > MaxTarget) scaled = MaxTarget;

            return TargetToHex(scaled);
        }

        /// <summary>
        /// Probability of winning at least one of <paramref name="hashPower"/> independent
        /// nonce attempts against a ruleset whose difficulty is <paramref name="initialDifficultyShift"/>.
        /// </summary>
        public static double WinProbability(int hashPower, int initialDifficultyShift)
        {
            var perAttempt = Math.Pow(2.0, -initialDifficultyShift);
            return 1.0 - Math.Pow(1.0 - perAttempt, hashPower);
        }
    }

    /// <summary>
    /// One consensus/economics ruleset. A node's active ruleset can change by block height —
    /// see <see cref="RuleSchedule"/>, which every node owns instead of a single fixed
    /// <see cref="ConsensusRules"/>.
    /// </summary>
    public class ConsensusRules
    {
        public int RetargetIntervalBlocks { get; set; } = ProofOfWork.DefaultRetargetIntervalBlocks;
        public double TargetSecondsPerBlock { get; set; } = ProofOfWork.DefaultTargetSecondsPerBlock;
        public double MinAdjustmentFactor { get; set; } = ProofOfWork.DefaultMinAdjustmentFactor;
        public double MaxAdjustmentFactor { get; set; } = ProofOfWork.DefaultMaxAdjustmentFactor;
        public int InitialDifficultyShift { get; set; } = ProofOfWork.DefaultInitialDifficultyShift;
        public decimal InitialBlockReward { get; set; } = Economics.DefaultInitialBlockReward;
        public int HalvingIntervalBlocks { get; set; } = Economics.DefaultHalvingIntervalBlocks;
        public decimal MaxSupply { get; set; } = Economics.DefaultMaxSupply;
    }

    /// <summary>
    /// One entry in a node's <see cref="RuleSchedule"/>: <see cref="Rules"/> becomes the
    /// active ruleset starting at height <see cref="FromHeight"/>, until (if ever) a
    /// later-<see cref="FromHeight"/> entry supersedes it.
    /// </summary>
    public class RuleScheduleEntry
    {
        public int FromHeight { get; set; } = 0;
        public ConsensusRules Rules { get; set; } = new();
        /// <summary>The <see cref="NamedConsensusRules.Name"/> <see cref="Rules"/> was resolved from, or null for a hardcoded/unnamed default.</summary>
        public string? Name { get; set; }
    }

    /// <summary>
    /// One entry in a <see cref="NamedConsensusRules"/>' price schedule: <see cref="Price"/>
    /// becomes that ruleset's $-reference value starting at height <see cref="FromHeight"/>,
    /// until (if ever) a later-<see cref="FromHeight"/> entry supersedes it.
    /// </summary>
    public class PriceScheduleEntry
    {
        public int FromHeight { get; set; } = 0;
        public decimal Price { get; set; } = 0m;
    }

    /// <summary>
    /// One candidate ruleset a value-seeking node compares against its peers — see
    /// <see cref="RuleSchedule"/>'s value-seeking constructor.
    /// </summary>
    public class ValueSeekingCandidate
    {
        public ConsensusRules Rules { get; set; } = new();
        public List<PriceScheduleEntry> PriceSchedule { get; set; } = new();
        /// <summary>The <see cref="NamedConsensusRules.Name"/> <see cref="Rules"/> was resolved from.</summary>
        public string? Name { get; set; }
    }

    /// <summary>
    /// A node's own timeline of which <see cref="ConsensusRules"/> is active at which height.
    /// Either a static, author-scripted timeline or, in value-seeking mode, a dynamic pick of
    /// whichever candidate has the highest expected value at a given height.
    /// </summary>
    public class RuleSchedule
    {
        private readonly List<RuleScheduleEntry> _entries;
        private readonly List<ValueSeekingCandidate> _valueSeekingCandidates;
        private readonly int _hashPower;
        private readonly decimal _debasementRatePerBlock;

        public RuleSchedule(IEnumerable<RuleScheduleEntry> entries, decimal debasementRatePerBlock)
        {
            _entries = entries.OrderBy(e => e.FromHeight).ToList();
            _valueSeekingCandidates = new List<ValueSeekingCandidate>();
            _debasementRatePerBlock = debasementRatePerBlock;
        }

        public RuleSchedule(IEnumerable<ValueSeekingCandidate> candidates, int hashPower, decimal debasementRatePerBlock)
        {
            _entries = new List<RuleScheduleEntry>();
            _hashPower = hashPower;
            _valueSeekingCandidates = candidates
                .Select(c => new ValueSeekingCandidate { Rules = c.Rules, PriceSchedule = c.PriceSchedule.OrderBy(p => p.FromHeight).ToList(), Name = c.Name })
                .ToList();
            _debasementRatePerBlock = debasementRatePerBlock;
        }

        /// <summary>
        /// The ruleset active at <paramref name="height"/>. An empty schedule, or a height
        /// before every entry's <c>FromHeight</c>, resolves to <c>new ConsensusRules()</c>.
        /// </summary>
        public ConsensusRules RulesForHeight(int height)
        {
            if (_valueSeekingCandidates.Count > 0)
                return MostProfitableAt(height);

            var active = new ConsensusRules();
            foreach (var entry in _entries)
            {
                if (entry.FromHeight > height) break;
                active = entry.Rules;
            }
            return active;
        }

        /// <summary>
        /// The <see cref="NamedConsensusRules.Name"/> behind whichever <see cref="ConsensusRules"/>
        /// <see cref="RulesForHeight"/> would return, or null when that ruleset is unnamed
        /// (hardcoded defaults, or a value-seeking node currently idle for lack of a
        /// profitable candidate).
        /// </summary>
        public string? NameForHeight(int height)
        {
            if (_valueSeekingCandidates.Count > 0)
            {
                var (best, value, _, name) = BestCandidateAt(height);
                return (best != null && value > 0m) ? name : null;
            }

            string? active = null;
            foreach (var entry in _entries)
            {
                if (entry.FromHeight > height) break;
                active = entry.Name;
            }
            return active;
        }

        /// <summary>
        /// The best candidate's expected value (win probability x reward x price) at
        /// <paramref name="height"/>, or <see cref="decimal.MaxValue"/> in static mode.
        /// </summary>
        public decimal BestValueAt(int height) =>
            _valueSeekingCandidates.Count > 0 ? BestCandidateAt(height).Value : decimal.MaxValue;

        public bool IsValueSeeking => _valueSeekingCandidates.Count > 0;

        /// <summary>
        /// How much a $ figure authored at height 0 is worth, nominally, at
        /// <paramref name="height"/> — 1 (no change) when the debasement rate is 0.
        /// </summary>
        public decimal DebasementFactorAt(int height) =>
            (decimal)Math.Pow((double)(1m + _debasementRatePerBlock), height);

        /// <summary>The price of whichever candidate is currently most profitable, or 0m in static mode.</summary>
        public decimal CurrentPriceAt(int height) =>
            _valueSeekingCandidates.Count > 0 ? BestCandidateAt(height).Price : 0m;

        /// <summary>
        /// The price of the named candidate at <paramref name="height"/>, or 0m if this node
        /// isn't tracking a value-seeking candidate by that name (including when it isn't
        /// value-seeking at all) — e.g. a legacy asset held from before value-seeking began.
        /// </summary>
        public decimal PriceForNameAt(string assetName, int height)
        {
            var candidate = _valueSeekingCandidates.FirstOrDefault(c => c.Name == assetName);
            if (candidate == null) return 0m;
            return PriceAt(candidate.PriceSchedule, height) * DebasementFactorAt(height);
        }

        private ConsensusRules MostProfitableAt(int height)
        {
            var (best, value, _, _) = BestCandidateAt(height);
            return (best != null && value > 0m) ? best : new ConsensusRules();
        }

        private (ConsensusRules? Rules, decimal Value, decimal Price, string? Name) BestCandidateAt(int height)
        {
            ConsensusRules? best = null;
            var bestValue = 0m;
            var bestPrice = 0m;
            string? bestName = null;
            var debasement = DebasementFactorAt(height);
            foreach (var candidate in _valueSeekingCandidates)
            {
                var price = PriceAt(candidate.PriceSchedule, height) * debasement;
                var winProbability = ProofOfWork.WinProbability(_hashPower, candidate.Rules.InitialDifficultyShift);
                var value = (decimal)winProbability * Economics.NominalBlockReward(height, candidate.Rules) * price;
                if (best == null || value > bestValue)
                {
                    best = candidate.Rules;
                    bestValue = value;
                    bestPrice = price;
                    bestName = candidate.Name;
                }
            }
            return (best, bestValue, bestPrice, bestName);
        }

        private static decimal PriceAt(List<PriceScheduleEntry> schedule, int height)
        {
            var price = 0m;
            foreach (var entry in schedule)
            {
                if (entry.FromHeight > height) break;
                price = entry.Price;
            }
            return price;
        }
    }

    /// <summary>
    /// Coin issuance: a coinbase transaction (<c>From == CoinbaseSender</c>) is how new coins
    /// enter existence, exactly one per block, paid to whoever built it. The nominal reward
    /// halves every <see cref="ConsensusRules.HalvingIntervalBlocks"/>, and the running total
    /// ever minted across the whole chain is hard-capped at <see cref="ConsensusRules.MaxSupply"/>.
    /// </summary>
    public static class Economics
    {
        public const string CoinbaseSender = "coinbase";
        public const decimal DefaultInitialBlockReward = 50m;
        public const int DefaultHalvingIntervalBlocks = 210_000;
        public const decimal DefaultMaxSupply = 21_000_000m;

        /// <summary>Schedule-only reward for a given height, ignoring the max-supply cap.</summary>
        public static decimal NominalBlockReward(int height, ConsensusRules rules)
        {
            if (height <= 0) return 0m;

            var halvings = height / rules.HalvingIntervalBlocks;
            if (halvings >= 50) return 0m;

            var divisor = BigInteger.Pow(2, halvings);
            return rules.InitialBlockReward / (decimal)divisor;
        }

        /// <summary>Sums every coinbase-labeled transaction across the given chain prefix.</summary>
        public static decimal TotalMintedSoFar(List<Block> ancestors)
        {
            decimal total = 0m;
            foreach (var block in ancestors)
                foreach (var tx in block.Transactions)
                    if (tx.From == CoinbaseSender)
                        total += tx.Amount;
            return total;
        }

        /// <summary>
        /// The actual reward a block at this height may claim: the schedule's nominal reward,
        /// clamped so the running total minted across the whole chain never exceeds
        /// <paramref name="rules"/>'s <see cref="ConsensusRules.MaxSupply"/>.
        /// </summary>
        public static decimal ComputeBlockReward(List<Block> ancestors, int height, ConsensusRules rules)
        {
            var nominal = NominalBlockReward(height, rules);
            if (nominal <= 0m) return 0m;

            var mintedSoFar = TotalMintedSoFar(ancestors);
            var remaining = rules.MaxSupply - mintedSoFar;
            if (remaining <= 0m) return 0m;

            return nominal > remaining ? remaining : nominal;
        }
    }

    /// <summary>
    /// Derives every account's balance per coin/asset purely from public chain history. An
    /// asset is identified by the name of whichever <see cref="ConsensusRules"/> was active
    /// at a given height (see <see cref="RuleSchedule.NameForHeight"/>), falling back to
    /// <see cref="DefaultAssetName"/> for unnamed/hardcoded-default rulesets. When the active
    /// asset changes from one height to the next, every account's balance in the outgoing
    /// asset is cloned into the new asset — the same "everyone's coin becomes spendable on
    /// both branches" effect a real hard fork has.
    /// </summary>
    public static class Ledger
    {
        public const string DefaultAssetName = "(default)";

        /// <summary>
        /// Balances as of the top of <paramref name="throughHeight"/> (default: the chain's
        /// own last height). Passing a height one past the chain's last block lets a caller
        /// ask "what would balances be at the moment the next block is minted" — picking up
        /// an asset-change clone that happens exactly at that height, before the block exists.
        /// </summary>
        public static Dictionary<(string Account, string Asset), decimal> ComputeBalancesByAsset(
            IReadOnlyList<Block> chain, Func<int, string?> ruleNameForHeight, int? throughHeight = null)
        {
            var balances = new Dictionary<(string, string), decimal>();
            var upTo = throughHeight ?? (chain.Count - 1);
            string? currentAsset = null;

            for (var h = 0; h <= upTo; h++)
            {
                var asset = ruleNameForHeight(h) ?? DefaultAssetName;
                if (currentAsset != null && asset != currentAsset)
                    CloneForward(balances, currentAsset, asset);
                currentAsset = asset;

                if (h >= chain.Count) continue;
                foreach (var tx in chain[h].Transactions)
                {
                    if (tx.From != Economics.CoinbaseSender)
                        balances[(tx.From, asset)] = balances.GetValueOrDefault((tx.From, asset)) - tx.Amount;
                    balances[(tx.To, asset)] = balances.GetValueOrDefault((tx.To, asset)) + tx.Amount;
                }
            }

            return balances;
        }

        /// <summary>Reshapes an asset-keyed balance snapshot into account → asset → amount, for JSON.</summary>
        public static Dictionary<string, Dictionary<string, decimal>> ToNestedDictionary(
            Dictionary<(string Account, string Asset), decimal> balances)
        {
            var nested = new Dictionary<string, Dictionary<string, decimal>>();
            foreach (var ((account, asset), amount) in balances)
            {
                if (!nested.TryGetValue(account, out var byAsset))
                    nested[account] = byAsset = new Dictionary<string, decimal>();
                byAsset[asset] = amount;
            }
            return nested;
        }

        /// <summary>Clones every balance held in <paramref name="fromAsset"/> into <paramref name="toAsset"/>, additively.</summary>
        internal static void CloneForward(Dictionary<(string Account, string Asset), decimal> balances, string fromAsset, string toAsset)
        {
            foreach (var (account, amount) in balances
                .Where(kv => kv.Key.Asset == fromAsset)
                .Select(kv => (kv.Key.Account, kv.Value))
                .ToList())
            {
                balances[(account, toAsset)] = balances.GetValueOrDefault((account, toAsset)) + amount;
            }
        }
    }

    /// <summary>
    /// One node's own thread-safe, append-only chain, validated against its own
    /// <see cref="RuleSchedule"/> so it only ever accepts a block that matches what this node
    /// currently expects at that height.
    /// </summary>
    public class Blockchain
    {
        private readonly object _lock = new();
        private readonly RuleSchedule _ruleSchedule;
        private string _lastObservedLineage;
        public List<Block> Blocks { get; private set; } = new();

        /// <summary>
        /// Fires exactly once per actual change in this node's own active lineage (named
        /// ruleset — see <see cref="RuleSchedule.NameForHeight"/>), with the outgoing lineage,
        /// the incoming one, and the height the incoming one took effect at. Unnamed rulesets
        /// use <see cref="Ledger.DefaultAssetName"/>.
        /// </summary>
        public Action<string, string, int>? OnLineageSwitched;

        public Blockchain(RuleSchedule? ruleSchedule = null)
        {
            _ruleSchedule = ruleSchedule ?? new RuleSchedule(Enumerable.Empty<RuleScheduleEntry>(), 0m);
            Blocks.Add(CreateGenesisBlock());
            _lastObservedLineage = RuleNameForHeight(0) ?? Ledger.DefaultAssetName;
        }

        /// <summary>
        /// Compares the current tip's lineage against the last-observed one, firing
        /// <see cref="OnLineageSwitched"/> on a change. Called after <see cref="Blocks"/> has
        /// already been updated, and always outside <see cref="_lock"/> — it only takes the
        /// lock itself, briefly, to read the comparison state.
        /// </summary>
        private void CheckLineageSwitch()
        {
            string oldLineage;
            string newLineage;
            int atHeight;
            lock (_lock)
            {
                atHeight = Blocks[^1].Index;
                newLineage = RuleNameForHeight(atHeight) ?? Ledger.DefaultAssetName;
                if (newLineage == _lastObservedLineage) return;
                oldLineage = _lastObservedLineage;
                _lastObservedLineage = newLineage;
            }
            OnLineageSwitched?.Invoke(oldLineage, newLineage, atHeight);
        }

        private static Block CreateGenesisBlock()
        {
            var genesis = new Block
            {
                Index = 0,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                PreviousHash = "0",
                BuiltBy = "genesis",
                Rules = new ConsensusRules(),
                Target = ProofOfWork.TargetToHex(ProofOfWork.MaxTarget >> ProofOfWork.DefaultInitialDifficultyShift),
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

        /// <summary>This node's named ruleset at <paramref name="height"/> — see <see cref="RuleSchedule.NameForHeight"/>.</summary>
        public string? RuleNameForHeight(int height) => _ruleSchedule.NameForHeight(height);

        /// <summary>This node's own balances, per coin/asset — see <see cref="Ledger.ComputeBalancesByAsset"/>.</summary>
        public Dictionary<(string Account, string Asset), decimal> SnapshotBalancesByAsset(int? throughHeight = null) =>
            Ledger.ComputeBalancesByAsset(Snapshot(), RuleNameForHeight, throughHeight);

        /// <summary>Appends a block this node built itself, without re-validating it.</summary>
        public void AppendTrusting(Block block)
        {
            lock (_lock)
            {
                Blocks.Add(block);
            }
            CheckLineageSwitch();
        }

        private static (bool Ok, string Reason) ValidateChain(List<Block> candidate, Func<int, ConsensusRules> rulesForHeight, Func<int, string?> ruleNameForHeight)
        {
            if (candidate == null || candidate.Count == 0)
                return (false, "candidate chain is empty");

            if (candidate[0].Index != 0)
                return (false, "candidate chain does not start at genesis");

            var balances = new Dictionary<(string Account, string Asset), decimal>();
            string? currentAsset = null;

            for (int i = 0; i < candidate.Count; i++)
            {
                var block = candidate[i];

                var asset = ruleNameForHeight(block.Index) ?? Ledger.DefaultAssetName;
                if (currentAsset != null && asset != currentAsset)
                    Ledger.CloneForward(balances, currentAsset, asset);
                currentAsset = asset;

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

                    var ancestors = candidate.GetRange(0, i);

                    var rules = rulesForHeight(block.Index);
                    var expectedTarget = ProofOfWork.ComputeExpectedTargetHex(ancestors, rules);
                    if (block.Target != expectedTarget)
                        return (false, $"block #{block.Index} declares an incorrect target — expected {expectedTarget[..8]}..., " +
                            $"got {(block.Target.Length >= 8 ? block.Target[..8] : block.Target)}... " +
                            "(target must match what the validator's own currently-active rules compute from prior block timestamps)");

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

                    var expectedReward = Economics.ComputeBlockReward(ancestors, block.Index, rules);
                    if (expectedReward > 0m)
                    {
                        if (coinbaseTxs.Count != 1)
                            return (false, $"block #{block.Index} is missing its coinbase transaction (expected reward {expectedReward})");
                        if (coinbaseTxs[0].Amount != expectedReward)
                            return (false, $"block #{block.Index} coinbase amount {coinbaseTxs[0].Amount} does not match the independently computed reward {expectedReward} for this height");
                    }
                    else if (coinbaseTxs.Count != 0)
                    {
                        return (false, $"block #{block.Index} includes a coinbase transaction, but the reward at this height has decayed to zero or the validator's own currently-active {rules.MaxSupply}-coin max supply has already been reached");
                    }
                }

                foreach (var tx in block.Transactions)
                {
                    if (string.IsNullOrWhiteSpace(tx.From) || string.IsNullOrWhiteSpace(tx.To))
                        return (false, $"block #{block.Index} contains a transaction missing From/To");

                    if (tx.Amount <= 0)
                        return (false, $"block #{block.Index} contains a non-positive transaction amount: {tx.Amount}");

                    if (tx.Asset != asset)
                        return (false, $"block #{block.Index} contains a transaction declaring asset '{tx.Asset}', " +
                            $"which does not match this block's own active asset '{asset}'");

                    if (tx.From == Economics.CoinbaseSender)
                    {
                        balances[(tx.To, asset)] = balances.GetValueOrDefault((tx.To, asset)) + tx.Amount;
                    }
                    else
                    {
                        var available = balances.GetValueOrDefault((tx.From, asset));
                        if (tx.Amount > available)
                            return (false, $"block #{block.Index} contains a transaction spending {tx.Amount} of '{asset}' from '{tx.From}', " +
                                $"who only has a balance of {available} in that asset at that point in the chain — insufficient funds or a double-spend");

                        balances[(tx.From, asset)] = available - tx.Amount;
                        balances[(tx.To, asset)] = balances.GetValueOrDefault((tx.To, asset)) + tx.Amount;
                    }
                }

                var recomputed = block.ComputeHash();
                if (recomputed != block.Hash)
                    return (false, $"block #{block.Index} hash does not match its contents");
            }

            return (true, "ok");
        }

        /// <summary>
        /// Neutral, no-node-context structural audit: validates each block against its own
        /// self-declared <see cref="Block.Rules"/> rather than any one node's active schedule.
        /// </summary>
        public static (bool Ok, string Reason) ValidateSnapshot(List<Block> candidate)
        {
            return ValidateChain(candidate, height => candidate[height].Rules, height => null);
        }

        /// <summary>
        /// Attempts to append <paramref name="block"/> to the tip. <c>AttributableToSender</c>
        /// is true only when rejection reflects a genuine consensus-rule violation in the data
        /// itself, as opposed to ordinary network timing — the signal a caller should act on to
        /// discourage whoever sent it.
        /// </summary>
        public (bool Ok, string Reason, bool AttributableToSender) TryAppend(Block block)
        {
            var result = AppendLocked();
            if (result.Ok) CheckLineageSwitch();
            return result;

            (bool Ok, string Reason, bool AttributableToSender) AppendLocked()
            {
                lock (_lock)
                {
                    var tip = Blocks[^1];

                    if (block.Index != tip.Index + 1)
                        return (false, $"expected index {tip.Index + 1}, got {block.Index}", false);

                    if (block.PreviousHash != tip.Hash)
                        return (false, $"previous hash mismatch: expected {tip.Hash}, got {block.PreviousHash}", false);

                    var candidate = new List<Block>(Blocks) { block };
                    var validation = ValidateChain(candidate, _ruleSchedule.RulesForHeight, _ruleSchedule.NameForHeight);
                    if (!validation.Ok)
                        return (false, validation.Reason, true);

                    Blocks.Add(block);
                    return (true, "ok", false);
                }
            }
        }

        /// <summary>
        /// Fork choice: a valid candidate chain replaces the current one only when it is
        /// strictly longer.
        /// </summary>
        public (bool Replaced, string Reason, bool AttributableToSender) TryReplaceWithLongerChain(List<Block> candidate)
        {
            var result = ReplaceLocked();
            if (result.Replaced) CheckLineageSwitch();
            return result;

            (bool Replaced, string Reason, bool AttributableToSender) ReplaceLocked()
            {
                lock (_lock)
                {
                    var validation = ValidateChain(candidate, _ruleSchedule.RulesForHeight, _ruleSchedule.NameForHeight);
                    if (!validation.Ok)
                        return (false, $"candidate rejected: {validation.Reason}", true);

                    if (candidate.Count <= Blocks.Count)
                        return (false, $"candidate is not longer (candidate={candidate.Count - 1}, local={Blocks.Count - 1})", false);

                    if (candidate[0].Hash != Blocks[0].Hash)
                        return (false, "candidate has a different genesis block", true);

                    Blocks = new List<Block>(candidate);
                    return (true, $"replaced local chain with longer chain at height {Blocks[^1].Index}", false);
                }
            }
        }

        /// <summary>
        /// Resumes this node's chain from a previously persisted snapshot on disk, accepting
        /// it only if structurally valid and sharing this build's canonical genesis block.
        /// </summary>
        public (bool Loaded, string Reason) TryLoadFrom(List<Block> candidate)
        {
            lock (_lock)
            {
                var validation = ValidateChain(candidate, _ruleSchedule.RulesForHeight, _ruleSchedule.NameForHeight);
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

    /// <summary>
    /// SQLite-backed persistence for one node's local chain, under
    /// <c>nodes/&lt;node-id&gt;/blockchain.db</c>.
    /// </summary>
    public sealed class BlockchainStore : IDisposable
    {
        private readonly object _lock = new();
        private readonly SqliteConnection _connection;
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
                        nonce INTEGER NOT NULL,
                        retarget_interval_blocks INTEGER NOT NULL,
                        target_seconds_per_block REAL NOT NULL,
                        min_adjustment_factor REAL NOT NULL,
                        max_adjustment_factor REAL NOT NULL,
                        initial_difficulty_shift INTEGER NOT NULL,
                        initial_block_reward TEXT NOT NULL,
                        halving_interval_blocks INTEGER NOT NULL,
                        max_supply TEXT NOT NULL
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

        /// <summary>Returns the full saved chain in height order, or null if nothing has been persisted yet.</summary>
        public List<Block>? LoadAll()
        {
            lock (_lock)
            {
                if (_persistedHashes.Count == 0) return null;

                var blocksByIndex = new Dictionary<int, Block>();
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT idx, timestamp, previous_hash, hash, built_by, signature, target, nonce,
                               retarget_interval_blocks, target_seconds_per_block, min_adjustment_factor,
                               max_adjustment_factor, initial_difficulty_shift, initial_block_reward,
                               halving_interval_blocks, max_supply
                        FROM blocks ORDER BY idx;
                    ";
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
                            Rules = new ConsensusRules
                            {
                                RetargetIntervalBlocks = reader.GetInt32(8),
                                TargetSecondsPerBlock = reader.GetDouble(9),
                                MinAdjustmentFactor = reader.GetDouble(10),
                                MaxAdjustmentFactor = reader.GetDouble(11),
                                InitialDifficultyShift = reader.GetInt32(12),
                                InitialBlockReward = decimal.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
                                HalvingIntervalBlocks = reader.GetInt32(14),
                                MaxSupply = decimal.Parse(reader.GetString(15), CultureInfo.InvariantCulture)
                            },
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

        /// <summary>
        /// Reconciles the database with the given in-memory chain snapshot: finds the first
        /// height (if any) where the snapshot's hash differs from what's persisted, trims the
        /// persisted records from that point on, then reinserts the snapshot's tail.
        /// </summary>
        public void Sync(List<Block> snapshot)
        {
            lock (_lock)
            {
                var divergeAt = 0;
                var sharedLength = Math.Min(_persistedHashes.Count, snapshot.Count);
                while (divergeAt < sharedLength && _persistedHashes[divergeAt] == snapshot[divergeAt].Hash)
                    divergeAt++;

                if (divergeAt == _persistedHashes.Count && divergeAt == snapshot.Count)
                    return;

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
                    INSERT INTO blocks (idx, timestamp, previous_hash, hash, built_by, signature, target, nonce,
                                         retarget_interval_blocks, target_seconds_per_block, min_adjustment_factor,
                                         max_adjustment_factor, initial_difficulty_shift, initial_block_reward,
                                         halving_interval_blocks, max_supply)
                    VALUES ($idx, $timestamp, $previousHash, $hash, $builtBy, $signature, $target, $nonce,
                            $retargetIntervalBlocks, $targetSecondsPerBlock, $minAdjustmentFactor,
                            $maxAdjustmentFactor, $initialDifficultyShift, $initialBlockReward,
                            $halvingIntervalBlocks, $maxSupply);
                ";
                cmd.Parameters.AddWithValue("$idx", block.Index);
                cmd.Parameters.AddWithValue("$timestamp", block.Timestamp.ToString("O"));
                cmd.Parameters.AddWithValue("$previousHash", block.PreviousHash);
                cmd.Parameters.AddWithValue("$hash", block.Hash);
                cmd.Parameters.AddWithValue("$builtBy", block.BuiltBy);
                cmd.Parameters.AddWithValue("$signature", block.Signature);
                cmd.Parameters.AddWithValue("$target", block.Target);
                cmd.Parameters.AddWithValue("$nonce", block.Nonce);
                cmd.Parameters.AddWithValue("$retargetIntervalBlocks", block.Rules.RetargetIntervalBlocks);
                cmd.Parameters.AddWithValue("$targetSecondsPerBlock", block.Rules.TargetSecondsPerBlock);
                cmd.Parameters.AddWithValue("$minAdjustmentFactor", block.Rules.MinAdjustmentFactor);
                cmd.Parameters.AddWithValue("$maxAdjustmentFactor", block.Rules.MaxAdjustmentFactor);
                cmd.Parameters.AddWithValue("$initialDifficultyShift", block.Rules.InitialDifficultyShift);
                cmd.Parameters.AddWithValue("$initialBlockReward", block.Rules.InitialBlockReward.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$halvingIntervalBlocks", block.Rules.HalvingIntervalBlocks);
                cmd.Parameters.AddWithValue("$maxSupply", block.Rules.MaxSupply.ToString(CultureInfo.InvariantCulture));
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
