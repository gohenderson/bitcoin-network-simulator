using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    /// <summary>
    /// The mining/broadcast engine for a single node: searches for a valid nonce, assembles a
    /// candidate block's transactions (solo, on behalf of a pool, or an equivocator's dual
    /// candidates), and gossips the result to peers. Also owns this node's signing identity,
    /// registering its public key in <see cref="NodeIdentityRegistry"/> at construction.
    /// </summary>
    public class SoloMiner : IMiner
    {
        public string Id { get; }
        public int HashPower { get; private set; }
        public string Label => Id;
        public NodeRole Role => _role;

        private readonly NodeNetwork.InternalDispatchFunc _dispatch;
        private readonly NodeRole _role;
        private readonly RuleSchedule _ruleSchedule;
        private readonly Blockchain _chain;
        private readonly ConcurrentQueue<Transaction> _mempool;
        private readonly Func<List<string>> _getPeerIds;
        private readonly ChainWatcher _watcher;
        private readonly ECDsa _signingKey;
        private readonly Random _rng = new(Guid.NewGuid().GetHashCode());
        private readonly decimal _costPerAttempt;
        private bool _idleLastTurn = false;
        private readonly decimal _costOfLiving;
        private readonly decimal _startingCapital;
        private decimal _accruedLivingCost = 0m;
        private readonly Action _requestForcedChurn;
        private readonly decimal _hashPowerCost;
        private readonly int _maxHashPower;
        private decimal _investedInHashPower = 0m;
        private readonly List<string> _poolCandidates;
        private readonly decimal _poolAdoptionThreshold;
        private string? _currentPool;
        private readonly Func<string, int> _getPoolHashPower;
        private readonly Action<string?> _requestPoolSwitch;

        public SoloMiner(string id, NodeNetwork.InternalDispatchFunc dispatch, NodeRole role, int hashPower, decimal costPerAttempt, decimal costOfLiving, decimal startingCapital, Action requestForcedChurn, decimal hashPowerCost, int maxHashPower, List<string> poolCandidates, decimal poolAdoptionThreshold, string? initialPool, Func<string, int> getPoolHashPower, Action<string?> requestPoolSwitch, RuleSchedule ruleSchedule, Blockchain chain, ConcurrentQueue<Transaction> mempool,
            Func<List<string>> getPeerIds, ChainWatcher watcher, ECDsa signingKey)
        {
            Id = id;
            _dispatch = dispatch;
            _role = role;
            HashPower = Math.Max(1, hashPower);
            _costPerAttempt = costPerAttempt;
            _costOfLiving = costOfLiving;
            _startingCapital = startingCapital;
            _requestForcedChurn = requestForcedChurn;
            _hashPowerCost = hashPowerCost;
            _maxHashPower = maxHashPower;
            _poolCandidates = role == NodeRole.Honest ? poolCandidates : new List<string>();
            _poolAdoptionThreshold = poolAdoptionThreshold;
            _currentPool = initialPool;
            _getPoolHashPower = getPoolHashPower;
            _requestPoolSwitch = requestPoolSwitch;
            _ruleSchedule = ruleSchedule;
            _chain = chain;
            _mempool = mempool;
            _getPeerIds = getPeerIds;
            _watcher = watcher;
            _signingKey = signingKey;
            NodeIdentityRegistry.Register(Id, _signingKey.ExportSubjectPublicKeyInfo());
        }

        private string SignBlockHash(string hashHex)
        {
            var hashBytes = Convert.FromHexString(hashHex);
            var signatureBytes = _signingKey.SignHash(hashBytes);
            return Convert.ToHexString(signatureBytes).ToLowerInvariant();
        }

        public async Task MineOneRoundAsync(CancellationToken token)
        {
            ReconsiderPoolMembership();
            if (_currentPool != null) return;

            try
            {
                if (_role == NodeRole.Equivocator)
                    await MineAndBroadcastEquivocationAsync(token);
                else
                    await MineAndBroadcastSingleRoundAsync(token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Id}] mining error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called once per this node's own turn — solo or pooled — to decide whether to stay
        /// put or move. Below the pool adoption threshold this node optimizes for
        /// realization: it joins whichever option (including its current pool) maximizes the
        /// group's win probability, ignoring dilution. At or above the threshold it always
        /// prefers solo, since a proportional share of a bigger pool is never bigger than the
        /// reward kept whole.
        /// </summary>
        public void ReconsiderPoolMembership()
        {
            if (_poolCandidates.Count == 0) return;

            var height = _chain.Latest.Index + 1;
            var shift = _ruleSchedule.RulesForHeight(height).InitialDifficultyShift;
            var soloWinProbability = (decimal)ProofOfWork.WinProbability(HashPower, shift);

            if (soloWinProbability >= _poolAdoptionThreshold)
            {
                if (_currentPool != null)
                {
                    Console.WriteLine($"[{Id}] leaving pool {_currentPool}: solo win probability {soloWinProbability:P1} now clears the {_poolAdoptionThreshold:P1} adoption threshold");
                    _requestPoolSwitch(null);
                    _currentPool = null;
                }
                return;
            }

            var bestPool = _currentPool;
            var bestWinProbability = _currentPool == null
                ? soloWinProbability
                : (decimal)ProofOfWork.WinProbability(_getPoolHashPower(_currentPool), shift);

            foreach (var candidate in _poolCandidates)
            {
                if (candidate == _currentPool) continue;
                var candidateWinProbability = (decimal)ProofOfWork.WinProbability(_getPoolHashPower(candidate) + HashPower, shift);
                if (candidateWinProbability > bestWinProbability)
                {
                    bestWinProbability = candidateWinProbability;
                    bestPool = candidate;
                }
            }

            if (bestPool != _currentPool)
            {
                Console.WriteLine($"[{Id}] joining pool {bestPool} (win probability {bestWinProbability:P1}) — own solo odds ({soloWinProbability:P1}) are below the {_poolAdoptionThreshold:P1} adoption threshold");
                _requestPoolSwitch(bestPool);
                _currentPool = bestPool;
            }
        }

        public async Task MineForPoolAsync(string poolLabel, int totalHashPower, IReadOnlyList<SoloMiner> members, CancellationToken token)
        {
            try
            {
                await MineAndBroadcastPooledAsync(poolLabel, totalHashPower, members, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"[{poolLabel}] pooled mining error: {ex.Message}");
            }
        }

        /// <summary>
        /// The core proof-of-work search: tries at most <paramref name="attempts"/> nonces
        /// looking for a hash that satisfies <paramref name="expectedTargetHex"/>, returning
        /// null if none of them do or if mining is being cancelled.
        /// </summary>
        private Block? MineBlock(Block parent, string expectedTargetHex, ConsensusRules rules, List<Transaction> txs, string builtByLabel, int attempts, CancellationToken token)
        {
            var candidate = new Block
            {
                Index = parent.Index + 1,
                PreviousHash = parent.Hash,
                BuiltBy = builtByLabel,
                Transactions = txs,
                Target = expectedTargetHex,
                Rules = rules,
                Timestamp = DateTime.UtcNow
            };

            for (long nonce = 0; nonce < attempts; nonce++)
            {
                if (token.IsCancellationRequested) return null;

                candidate.Nonce = nonce;
                candidate.Hash = candidate.ComputeHash();
                if (ProofOfWork.MeetsTarget(candidate.Hash, expectedTargetHex))
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// Simulates including <paramref name="candidates"/> in order against a starting
        /// balance snapshot, dropping (and logging) any transaction its sender can't actually
        /// afford at that point. <paramref name="balances"/> is mutated in place so callers
        /// can chain further inclusions on top of the result.
        /// </summary>
        private List<Transaction> FilterAffordable(List<Transaction> candidates, Dictionary<string, decimal> balances)
        {
            var accepted = new List<Transaction>();
            foreach (var tx in candidates)
            {
                var available = balances.GetValueOrDefault(tx.From);
                if (tx.Amount > available)
                {
                    Console.WriteLine($"[{Id}] dropping mempool tx {tx.From}->{tx.To}:{tx.Amount} from this block — " +
                        $"sender's balance is only {available} at the current tip");
                    continue;
                }

                balances[tx.From] = available - tx.Amount;
                balances[tx.To] = balances.GetValueOrDefault(tx.To) + tx.Amount;
                accepted.Add(tx);
            }
            return accepted;
        }

        private async Task MineAndBroadcastSingleRoundAsync(CancellationToken token)
        {
            var parent = _chain.Latest;
            var ancestors = _chain.Snapshot();
            var height = parent.Index + 1;
            var byAsset = Ledger.ComputeBalancesByAsset(ancestors, _ruleSchedule.NameForHeight, height);
            var currentAsset = _ruleSchedule.NameForHeight(height) ?? Ledger.DefaultAssetName;
            var simulatedBalances = byAsset
                .Where(kv => kv.Key.Asset == currentAsset)
                .ToDictionary(kv => kv.Key.Account, kv => kv.Value);

            var debasement = _ruleSchedule.DebasementFactorAt(height);
            var netWorth = byAsset
                .Where(kv => kv.Key.Account == Id)
                .Sum(kv => kv.Value * _ruleSchedule.PriceForNameAt(kv.Key.Asset, height));

            if (_costOfLiving > 0m && _ruleSchedule.IsValueSeeking)
            {
                _accruedLivingCost += _costOfLiving * debasement;
                if (_accruedLivingCost > netWorth + _startingCapital)
                {
                    Console.WriteLine($"[{Id}] insolvent: accrued living cost {_accruedLivingCost} exceeds net worth {netWorth} plus starting capital {_startingCapital} — leaving the network");
                    _requestForcedChurn();
                    return;
                }
            }

            if (_hashPowerCost > 0m && _ruleSchedule.IsValueSeeking && (_maxHashPower <= 0 || HashPower < _maxHashPower))
            {
                var effectiveHashPowerCost = _hashPowerCost * debasement;
                var uncommitted = netWorth - _accruedLivingCost - _investedInHashPower;
                if (uncommitted >= effectiveHashPowerCost)
                {
                    _investedInHashPower += effectiveHashPowerCost;
                    HashPower++;
                    Console.WriteLine($"[{Id}] reinvesting profit: HashPower {HashPower - 1} -> {HashPower} ({effectiveHashPowerCost} committed, {uncommitted} was available)");
                }
            }

            var effectiveCostPerAttempt = _costPerAttempt * debasement;
            if (_costPerAttempt > 0m && _ruleSchedule.BestValueAt(height) <= effectiveCostPerAttempt * HashPower)
            {
                if (!_idleLastTurn)
                    Console.WriteLine($"[{Id}] going idle: no candidate ruleset covers this turn's mining cost ({effectiveCostPerAttempt} x {HashPower} = {effectiveCostPerAttempt * HashPower})");
                _idleLastTurn = true;
                return;
            }
            if (_idleLastTurn)
            {
                Console.WriteLine($"[{Id}] resuming mining: a candidate ruleset now covers this turn's cost");
                _idleLastTurn = false;
            }

            var rules = _ruleSchedule.RulesForHeight(height);
            var expectedTarget = ProofOfWork.ComputeExpectedTargetHex(ancestors, rules);
            var reward = Economics.ComputeBlockReward(ancestors, height, rules);

            var pending = new List<Transaction>();
            while (_mempool.TryDequeue(out var tx)) pending.Add(tx);

            var fakeIdentity = _role == NodeRole.Impersonator;
            var builtBy = fakeIdentity
                ? (_getPeerIds().Where(n => n != Id).OrderBy(_ => _rng.Next()).FirstOrDefault() ?? Id)
                : Id;

            var txs = new List<Transaction>();
            if (reward > 0m)
            {
                txs.Add(new Transaction { From = Economics.CoinbaseSender, To = builtBy, Amount = reward });
                simulatedBalances[builtBy] = simulatedBalances.GetValueOrDefault(builtBy) + reward;
            }
            txs.AddRange(FilterAffordable(pending, simulatedBalances));

            var block = MineBlock(parent, expectedTarget, rules, txs, builtBy, HashPower, token);
            if (block == null)
            {
                foreach (var tx in pending) _mempool.Enqueue(tx);
                return;
            }

            block.Signature = SignBlockHash(block.Hash);

            if (fakeIdentity)
                Console.WriteLine($"[{Id}] (Impersonator) mined block #{block.Index} (nonce {block.Nonce}) falsely claiming it was built by {builtBy}" +
                    (reward > 0m ? $" — the {reward}-coin reward is recorded as paid to {builtBy}, not {Id}" : ""));

            if (_role == NodeRole.Corruptor)
            {
                if (block.Transactions.Count > 0)
                    block.Transactions[0].Amount += 1000m;
                else
                    block.Transactions.Add(new Transaction { From = "attacker", To = "attacker", Amount = 999m });
                Console.WriteLine($"[{Id}] (Corruptor) mined block #{block.Index} (nonce {block.Nonce}), then tampered with it after the fact");
            }

            var currentPeers = _getPeerIds();
            var peersToNotify = currentPeers;
            if (_role == NodeRole.Withholder)
            {
                var others = currentPeers.Where(p => p != Id).ToList();
                var subsetSize = Math.Max(1, others.Count / 2);
                peersToNotify = others.OrderBy(_ => _rng.Next()).Take(subsetSize).ToList();
                Console.WriteLine($"[{Id}] (Withholder) mined block #{block.Index} (nonce {block.Nonce}) but only notifying {peersToNotify.Count}/{others.Count} peers");
            }

            if (!fakeIdentity && _role != NodeRole.Corruptor && _role != NodeRole.Withholder)
                Console.WriteLine($"[{Id}] *** mined block #{block.Index} (nonce {block.Nonce}, target {block.Target[..8]}...) " +
                    $"with {block.Transactions.Count} tx(s), reward {reward} ***");

            _watcher.ObserveBuild(Id, block, _role);
            _chain.AppendTrusting(block);
            await SendBlock(block, peersToNotify);
            await SendChain(currentPeers);
        }

        /// <summary>
        /// Mines one turn on behalf of a pool: tries up to <paramref name="totalHashPower"/>
        /// nonces — the sum of every current member's own HashPower. If successful, the
        /// coinbase reward is paid to <paramref name="poolLabel"/>, then immediately split
        /// among <paramref name="members"/> proportional to each one's HashPower share.
        /// </summary>
        private async Task MineAndBroadcastPooledAsync(string poolLabel, int totalHashPower, IReadOnlyList<SoloMiner> members, CancellationToken token)
        {
            var parent = _chain.Latest;
            var ancestors = _chain.Snapshot();
            var height = parent.Index + 1;
            var rules = _ruleSchedule.RulesForHeight(height);
            var expectedTarget = ProofOfWork.ComputeExpectedTargetHex(ancestors, rules);
            var reward = Economics.ComputeBlockReward(ancestors, height, rules);

            var pending = new List<Transaction>();
            while (_mempool.TryDequeue(out var tx)) pending.Add(tx);

            var txs = new List<Transaction>();
            var currentAsset = _ruleSchedule.NameForHeight(height) ?? Ledger.DefaultAssetName;
            var simulatedBalances = Ledger.ComputeBalancesByAsset(ancestors, _ruleSchedule.NameForHeight, height)
                .Where(kv => kv.Key.Asset == currentAsset)
                .ToDictionary(kv => kv.Key.Account, kv => kv.Value);

            if (reward > 0m)
            {
                txs.Add(new Transaction { From = Economics.CoinbaseSender, To = poolLabel, Amount = reward });
                simulatedBalances[poolLabel] = simulatedBalances.GetValueOrDefault(poolLabel) + reward;

                var distributed = 0m;
                for (int i = 0; i < members.Count; i++)
                {
                    var member = members[i];
                    var share = i == members.Count - 1
                        ? reward - distributed
                        : Math.Round(reward * member.HashPower / totalHashPower, 8);

                    if (share <= 0m) continue;
                    distributed += share;

                    txs.Add(new Transaction { From = poolLabel, To = member.Id, Amount = share });
                    simulatedBalances[poolLabel] -= share;
                    simulatedBalances[member.Id] = simulatedBalances.GetValueOrDefault(member.Id) + share;
                }
            }

            txs.AddRange(FilterAffordable(pending, simulatedBalances));

            var block = MineBlock(parent, expectedTarget, rules, txs, Id, totalHashPower, token);
            if (block == null)
            {
                foreach (var tx in pending) _mempool.Enqueue(tx);
                return;
            }

            block.Signature = SignBlockHash(block.Hash);

            Console.WriteLine($"[{poolLabel}] *** mined block #{block.Index} (nonce {block.Nonce}, target {block.Target[..8]}...) " +
                $"— built by {Id} on behalf of {members.Count} pool member(s) contributing {totalHashPower} combined hash power, " +
                $"reward {reward} split proportionally ***");

            _watcher.ObserveBuild(Id, block, _role);
            _chain.AppendTrusting(block);
            var currentPeers = _getPeerIds();
            await SendBlock(block, currentPeers);
            await SendChain(currentPeers);
        }

        /// <summary>
        /// Mines two separate valid blocks on the same parent to fork the network — real,
        /// doubled computational cost. Both blocks claim the same reward, since only whichever
        /// one actually survives on the eventual winning chain will ever count.
        /// </summary>
        private async Task MineAndBroadcastEquivocationAsync(CancellationToken token)
        {
            var parent = _chain.Latest;
            var ancestors = _chain.Snapshot();
            var height = parent.Index + 1;
            var rules = _ruleSchedule.RulesForHeight(height);
            var expectedTarget = ProofOfWork.ComputeExpectedTargetHex(ancestors, rules);
            var reward = Economics.ComputeBlockReward(ancestors, height, rules);

            var pending = new List<Transaction>();
            while (_mempool.TryDequeue(out var tx)) pending.Add(tx);
            var half = pending.Count / 2;
            var restA = pending.Take(half).ToList();
            var restB = pending.Skip(half).ToList();
            restB.Add(new Transaction { From = Id, To = "shadow-peer", Amount = 1m });

            var currentAsset = _ruleSchedule.NameForHeight(height) ?? Ledger.DefaultAssetName;
            var byAsset = Ledger.ComputeBalancesByAsset(ancestors, _ruleSchedule.NameForHeight, height);

            List<Transaction> BuildTxs(List<Transaction> rest)
            {
                var txs = new List<Transaction>();
                var simulatedBalances = byAsset
                    .Where(kv => kv.Key.Asset == currentAsset)
                    .ToDictionary(kv => kv.Key.Account, kv => kv.Value);
                if (reward > 0m)
                {
                    txs.Add(new Transaction { From = Economics.CoinbaseSender, To = Id, Amount = reward });
                    simulatedBalances[Id] = simulatedBalances.GetValueOrDefault(Id) + reward;
                }
                txs.AddRange(FilterAffordable(rest, simulatedBalances));
                return txs;
            }

            var txsA = BuildTxs(restA);
            var txsB = BuildTxs(restB);

            Console.WriteLine($"[{Id}] (Equivocator) mining TWO competing block #{parent.Index + 1}s in sequence — " +
                "this costs roughly twice the work of an honest block");

            var blockA = MineBlock(parent, expectedTarget, rules, txsA, Id, HashPower, token);
            if (blockA == null)
            {
                foreach (var tx in pending) _mempool.Enqueue(tx);
                return;
            }
            blockA.Signature = SignBlockHash(blockA.Hash);

            var blockB = MineBlock(parent, expectedTarget, rules, txsB, Id, HashPower, token);
            if (blockB == null)
            {
                _watcher.ObserveBuild(Id, blockA, _role);
                _chain.AppendTrusting(blockA);
                var earlyPeers = _getPeerIds();
                await SendBlock(blockA, earlyPeers);
                await SendChain(earlyPeers);
                return;
            }
            blockB.Signature = SignBlockHash(blockB.Hash);

            _watcher.ObserveBuild(Id, blockA, _role);
            _watcher.ObserveBuild(Id, blockB, _role);
            _chain.AppendTrusting(blockA);

            var currentPeers = _getPeerIds();
            var others = currentPeers.Where(p => p != Id).OrderBy(_ => _rng.Next()).ToList();
            var half1 = others.Take(others.Count / 2).ToList();
            var half2 = others.Skip(others.Count / 2).ToList();

            Console.WriteLine($"[{Id}] (Equivocator) finished both mined blocks for #{blockA.Index} " +
                $"(nonces {blockA.Nonce} / {blockB.Nonce}, reward {reward} each if adopted) — sending different ones to different peers");

            await SendBlock(blockA, half1);
            await SendBlock(blockB, half2);
            await SendChain(currentPeers);
        }

        private async Task SendBlock(Block block, IEnumerable<string> peerIds)
        {
            var json = JsonSerializer.Serialize(block);
            foreach (var peerId in peerIds)
            {
                if (peerId == Id) continue;
                try
                {
                    var (statusCode, body) = await _dispatch(peerId, "POST", "/receiveBlock", Id, json);
                    if (statusCode < 200 || statusCode >= 300)
                        Console.WriteLine($"[{Id}] peer {peerId} rejected block #{block.Index}: {body}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] couldn't reach peer {peerId}: {ex.Message}");
                }
            }
        }

        private async Task SendChain(IEnumerable<string> peerIds)
        {
            var json = JsonSerializer.Serialize(_chain.Snapshot());
            foreach (var peerId in peerIds)
            {
                if (peerId == Id) continue;
                try
                {
                    var (statusCode, body) = await _dispatch(peerId, "POST", "/receiveChain", Id, json);
                    if (statusCode < 200 || statusCode >= 300)
                        Console.WriteLine($"[{Id}] peer {peerId} rejected chain: {body}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] couldn't send chain to peer {peerId}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// A named group of <see cref="SoloMiner"/>s that mines as one combined <see cref="IMiner"/>
    /// instead of each member getting its own separate turn — combining member hash power,
    /// picking who coordinates a given turn, and splitting the reward.
    /// </summary>
    public class PoolMiner : IMiner
    {
        public string Label { get; }

        private readonly object _lock = new();
        private readonly List<SoloMiner> _members;
        private readonly Random _rng;

        public PoolMiner(string poolName, IEnumerable<SoloMiner> initialMembers, Random rng)
        {
            Label = poolName;
            _members = new List<SoloMiner>(initialMembers);
            _rng = rng;
        }

        public void AddMember(SoloMiner member)
        {
            lock (_lock) { _members.Add(member); }
        }

        public int MemberCount { get { lock (_lock) { return _members.Count; } } }

        public IReadOnlyList<SoloMiner> Members { get { lock (_lock) { return _members.ToList(); } } }

        public int TotalHashPower { get { lock (_lock) { return _members.Sum(m => m.HashPower); } } }

        /// <summary>Removes a departing member. Returns whether the id was actually a member.</summary>
        public bool RemoveMemberIfPresent(string nodeId)
        {
            lock (_lock) { return _members.RemoveAll(m => m.Id == nodeId) > 0; }
        }

        public bool TryRemoveMember(string nodeId, out SoloMiner? member)
        {
            lock (_lock)
            {
                member = _members.FirstOrDefault(m => m.Id == nodeId);
                if (member != null) _members.Remove(member);
                return member != null;
            }
        }

        public async Task MineOneRoundAsync(CancellationToken token)
        {
            List<SoloMiner> forReconsideration;
            lock (_lock) { forReconsideration = new List<SoloMiner>(_members); }
            foreach (var member in forReconsideration)
                member.ReconsiderPoolMembership();

            List<SoloMiner> members;
            lock (_lock) { members = new List<SoloMiner>(_members); }
            if (members.Count == 0) return;

            var totalHashPower = members.Sum(m => m.HashPower);
            var coordinator = WeightedRandomMember(members, totalHashPower, _rng);
            await coordinator.MineForPoolAsync(Label, totalHashPower, members, token);
        }

        /// <summary>
        /// Picks one member at random, weighted by each member's own HashPower, to coordinate
        /// (build, mine, and broadcast) this turn on the pool's behalf.
        /// </summary>
        private static SoloMiner WeightedRandomMember(List<SoloMiner> members, int totalHashPower, Random rng)
        {
            var roll = rng.Next(totalHashPower);
            var cumulative = 0;
            foreach (var m in members)
            {
                cumulative += m.HashPower;
                if (roll < cumulative) return m;
            }
            return members[^1];
        }
    }

    /// <summary>
    /// Common mining entry point implemented by both <see cref="SoloMiner"/> and
    /// <see cref="PoolMiner"/>. <see cref="MiningScheduler"/> works purely in terms of this
    /// interface and knows nothing about pools, roles, or hash power.
    /// </summary>
    public interface IMiner
    {
        string Label { get; }

        Task MineOneRoundAsync(CancellationToken token);
    }
}
