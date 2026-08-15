using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // The mining/broadcast engine for a single node: searching for a valid
    // nonce, assembling a candidate block's transactions (solo, on behalf of
    // a pool, or the Equivocator's dual candidates), and gossiping the result
    // to peers. A SoloMiner doesn't hold a reference to the Node it mines
    // for — it's handed its own copies of exactly what it needs (identity,
    // chain, mempool, network callbacks) at construction time. That's what
    // lets NodeNetwork.AddNodeAsync build a SoloMiner independently of Node
    // rather than the two being circularly dependent on each other. The
    // Chain and Mempool instances are shared with the owning Node — both are
    // constructed once by that same composition root and passed to Node and
    // SoloMiner alike, so they're always looking at the same state.
    //
    // A SoloMiner mines on its own behalf (MineOneRoundAsync, satisfying
    // IMiner, below) when it isn't in a pool, but also does the
    // actual mining work FOR a PoolMiner (below) when chosen, by
    // weighted random draw, to coordinate that pool's turn — MineForPoolAsync
    // is that entry point. Either way, it's this SoloMiner's own chain,
    // mempool, and network plumbing doing the work; only who the reward is
    // paid to and how many nonces get tried differ.
    //
    // A SoloMiner also owns this node's signing identity: `signingKey` is
    // handed in already loaded from NodeMetadata.SigningKey (or freshly
    // generated for a brand new node — see NodeMetadataStore.LoadOrCreateAsync),
    // so the same identity persists across restarts. Its public half is
    // registered under this node's Id in NodeIdentityRegistry — the
    // constructor is the very first thing this identity does, before it
    // could ever legitimately appear as BuiltBy in any block — and every
    // block this SoloMiner mines (solo, pooled, or either half of an
    // Equivocator's pair) gets signed with the private half before being
    // handed off. See the "Signed blocks" note in README.md.
    // ------------------------------------------------------------------

    public class SoloMiner : IMiner
    {
        public string Id { get; }
        // Mutable (private set) so Reinvestment (see MineAndBroadcastSingleRoundAsync)
        // can grow it at runtime; read fresh everywhere it's used (MineBlock,
        // the CostPerAttempt threshold), never cached, so a mid-turn increase
        // is picked up automatically with no other code changes.
        public int HashPower { get; private set; }
        public string Label => Id;

        private readonly int _serverPort;
        private readonly NodeRole _role;
        // This node's own timeline of which ConsensusRules is active at
        // which height — see RuleSchedule's own comment in Blockchain.cs.
        // Sourced from NodeMetadata.RuleSchedule at construction, and looked
        // up fresh for the height being built each time this SoloMiner
        // mines (solo, pooled, or either half of an Equivocator's pair) —
        // NOT cached once, since the active ruleset can change from one
        // height to the next.
        private readonly RuleSchedule _ruleSchedule;
        private readonly Blockchain _chain;
        private readonly ConcurrentQueue<Transaction> _mempool;
        private readonly Func<List<string>> _getPeerIds;
        private readonly ChainWatcher _watcher;
        private readonly ECDsa _signingKey;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private readonly Random _rng = new(Guid.NewGuid().GetHashCode());
        // $ cost of a single mining attempt — see ScenarioNodeGroup.CostPerAttempt
        // and RuleSchedule.BestValueAt. 0 (default) means mining is free, so the
        // idle check in MineAndBroadcastSingleRoundAsync never triggers.
        private readonly decimal _costPerAttempt;
        // Tracks whether the LAST turn was spent idle, purely so the
        // going-idle/resuming-mining console lines only print on the transition,
        // not every single turn of what could be a long idle stretch.
        private bool _idleLastTurn = false;
        // $ fixed cost owed every turn regardless of outcome — see
        // ScenarioNodeGroup.CostOfLiving. 0 (default) disables the insolvency
        // check in MineAndBroadcastSingleRoundAsync entirely.
        private readonly decimal _costOfLiving;
        // $ runway on top of on-chain net worth before CostOfLiving can push
        // this node into insolvency — see ScenarioNodeGroup.StartingCapital.
        private readonly decimal _startingCapital;
        // Cumulative CostOfLiving owed since this node's creation — never
        // reset, since "cumulative overhead exceeds cumulative wealth" is the
        // right long-run solvency test; see MineAndBroadcastSingleRoundAsync.
        private decimal _accruedLivingCost = 0m;
        // Lets this SoloMiner remove ITSELF from the network on insolvency —
        // see NodeNetwork.AddNodeAsync's requestForcedChurn closure.
        private readonly Action _requestForcedChurn;
        // $ cost to buy +1 HashPower — see ScenarioNodeGroup.HashPowerCost. 0
        // (default) disables the reinvestment check entirely.
        private readonly decimal _hashPowerCost;
        // Upper bound HashPowerCost-driven reinvestment won't grow HashPower
        // past — see ScenarioNodeGroup.MaxHashPower. 0 means uncapped.
        private readonly int _maxHashPower;
        // Cumulative $ already committed to past HashPower purchases — never
        // reset, mirrors _accruedLivingCost; see MineAndBroadcastSingleRoundAsync.
        private decimal _investedInHashPower = 0m;
        // Names of pools this node reconsiders joining every turn — see
        // ScenarioNodeGroup.PoolCandidates and ReconsiderPoolMembership.
        // Empty (default) disables reconsideration entirely.
        private readonly List<string> _poolCandidates;
        // Own solo win-probability cutoff below which ReconsiderPoolMembership
        // optimizes for realization instead of expected value — see
        // ScenarioNodeGroup.PoolAdoptionThreshold.
        private readonly decimal _poolAdoptionThreshold;
        // The pool this node currently believes it belongs to (null = solo) —
        // mirrors NodeNetwork's own actual placement, kept in sync by
        // ReconsiderPoolMembership so it never has to ask NodeNetwork what its
        // own state is. Seeded from ScenarioNodeGroup.Pool at construction.
        private string? _currentPool;
        // Looks up a named pool's current total HashPower (0 if it doesn't
        // exist yet) — see NodeNetwork.GetPoolHashPower.
        private readonly Func<string, int> _getPoolHashPower;
        // Moves this node between solo and a named pool (null = solo) — see
        // NodeNetwork.SwitchPoolMembership.
        private readonly Action<string?> _requestPoolSwitch;

        // `serverPort` is the single port the whole network's NetworkServer
        // listens on (see NetworkServer.cs) — every peer URL this miner
        // builds is http://localhost:{serverPort}/{peerId}/....
        public SoloMiner(string id, int serverPort, NodeRole role, int hashPower, decimal costPerAttempt, decimal costOfLiving, decimal startingCapital, Action requestForcedChurn, decimal hashPowerCost, int maxHashPower, List<string> poolCandidates, decimal poolAdoptionThreshold, string? initialPool, Func<string, int> getPoolHashPower, Action<string?> requestPoolSwitch, RuleSchedule ruleSchedule, Blockchain chain, ConcurrentQueue<Transaction> mempool,
            Func<List<string>> getPeerIds, ChainWatcher watcher, ECDsa signingKey)
        {
            Id = id;
            _serverPort = serverPort;
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

        // Signs `hashHex` (a block's own Hash) with this node's private
        // signing key, hex-encoded for storage in Block.Signature. Whoever
        // calls this can put any name they like in BuiltBy, but the
        // signature it produces only ever verifies against THIS node's own
        // registered key — see NodeIdentityRegistry.
        private string Sign(string hashHex)
        {
            var hashBytes = Convert.FromHexString(hashHex);
            var signatureBytes = _signingKey.SignHash(hashBytes);
            return Convert.ToHexString(signatureBytes).ToLowerInvariant();
        }

        // Called by the network's round-robin scheduler (via IMiner) to
        // perform one mining turn: try up to HashPower nonces, broadcast if
        // one meets the target, otherwise return control to the scheduler
        // empty-handed — this turn simply didn't win, exactly like a real
        // miner's hash budget for this slice of time coming up empty. See
        // the "Mining" note in README.md.
        public async Task MineOneRoundAsync(CancellationToken token)
        {
            ReconsiderPoolMembership();
            if (_currentPool != null) return; // just switched into a pool — scheduled as part of it next sweep instead

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

        // Called once per this node's own turn — whether currently solo (from
        // MineOneRoundAsync) or pooled (from PoolMiner.MineOneRoundAsync, for
        // every member, not just whoever coordinates) — to decide whether to
        // stay put or move. Below PoolAdoptionThreshold, this node's own solo
        // win probability is judged too unlikely to ever pay off, so it
        // optimizes for REALIZATION: join whichever option (including its
        // current pool) maximizes the GROUP's win probability, ignoring
        // dilution entirely — a diluted share of a group that actually wins
        // beats a share of one that almost never does. At or above the
        // threshold, EV wins instead — which is always solo, since a
        // proportional share of a bigger pool is never bigger than the
        // reward kept whole — so it leaves any pool it's in. See "Pool
        // adoption" in README.md.
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

        // Called by a PoolMiner this SoloMiner belongs to, when weighted
        // random choice (favoring higher-HashPower members) picks THIS
        // member to coordinate the pool's turn — see the "Mining pools" note
        // in README.md and the PoolMiner class below.
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

        // The core proof-of-work search: tries at most `attempts` nonces
        // looking for a hash that satisfies expectedTargetHex, returning null
        // if none of them do (this node's hash-power budget for the turn is
        // exhausted) or if we're shutting down. Runs synchronously (no awaits)
        // since it executes on this node's own dedicated LongRunning thread
        // already.
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
            return null; // exhausted this turn's hash-power budget without finding a valid nonce
        }

        // Simulates including `candidates` in order against a starting balance
        // snapshot, dropping (and logging) any transaction its sender can't
        // actually afford at that point — including a second transaction from a
        // sender the first one already drained. `balances` is mutated in place
        // so callers can chain further inclusions (e.g. after the coinbase
        // credit) on top of the result.
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

        // Honest, Impersonator, Corruptor, and Withholder all mine exactly one
        // block per round. What differs is the identity label (decided BEFORE
        // mining — it's part of the hashed payload, so it can't be swapped in
        // after the fact for free), whether it's tampered after hashing, and who
        // gets told about it.
        private async Task MineAndBroadcastSingleRoundAsync(CancellationToken token)
        {
            var parent = _chain.Latest;
            var ancestors = _chain.Snapshot();
            var height = parent.Index + 1;
            var simulatedBalances = Ledger.ComputeBalances(ancestors); // hoisted — reused below AND by the insolvency check

            // Cost of living: a FIXED bill owed every turn regardless of whether this
            // node mines, unlike CostPerAttempt (only owed while actively trying
            // nonces) — see "Cost of living" in README.md. Compared against this
            // node's actual on-chain balance's current $ value, not settled via an
            // on-chain transaction — there's no natural recipient, and letting a
            // node "spend" what it doesn't have would contradict FilterAffordable's
            // rule everywhere else. Accrues every turn regardless of outcome so
            // idling doesn't dodge it; never resets, since "cumulative overhead
            // exceeds cumulative wealth" is the right long-run solvency test.
            if (_costOfLiving > 0m && _ruleSchedule.IsValueSeeking)
            {
                _accruedLivingCost += _costOfLiving;
                var netWorth = simulatedBalances.GetValueOrDefault(Id) * _ruleSchedule.CurrentPriceAt(height);
                if (_accruedLivingCost > netWorth + _startingCapital)
                {
                    Console.WriteLine($"[{Id}] insolvent: accrued living cost {_accruedLivingCost} exceeds net worth {netWorth} plus starting capital {_startingCapital} — leaving the network");
                    _requestForcedChurn();
                    return;
                }
            }

            // Reinvestment: once this node's own EARNED profit (net worth beyond what's
            // already committed to accrued living cost or past hardware purchases)
            // covers HashPowerCost, it buys +1 HashPower — modeling real hash power
            // reinvesting profit into more hardware instead of just banking it. Same
            // virtual-ledger approach as CostOfLiving: _investedInHashPower is a
            // running tally compared against net worth, not an actual on-chain
            // transaction. Deliberately doesn't draw on StartingCapital — that's a
            // solvency buffer, not investable capital — so a node still running on its
            // starting cushion doesn't "reinvest" money it hasn't actually made. At
            // most one purchase per turn, so growth is gradual and observable. See
            // "Reinvestment" in README.md.
            if (_hashPowerCost > 0m && _ruleSchedule.IsValueSeeking && (_maxHashPower <= 0 || HashPower < _maxHashPower))
            {
                var netWorth = simulatedBalances.GetValueOrDefault(Id) * _ruleSchedule.CurrentPriceAt(height);
                var uncommitted = netWorth - _accruedLivingCost - _investedInHashPower;
                if (uncommitted >= _hashPowerCost)
                {
                    _investedInHashPower += _hashPowerCost;
                    HashPower++;
                    Console.WriteLine($"[{Id}] reinvesting profit: HashPower {HashPower - 1} -> {HashPower} ({_hashPowerCost} committed, {uncommitted} was available)");
                }
            }

            // Real operating cost (electricity, hardware) is the same regardless
            // of which candidate ruleset it's spent on, so it never changes WHICH
            // one is most profitable (RuleSchedule.MostProfitableAt is untouched)
            // — it only ever changes whether ANYTHING is worth mining this turn.
            // Mempool transactions are untouched here (dequeued further below,
            // never reached on an idle turn), so nothing is lost by sitting out.
            if (_costPerAttempt > 0m && _ruleSchedule.BestValueAt(height) <= _costPerAttempt * HashPower)
            {
                if (!_idleLastTurn)
                    Console.WriteLine($"[{Id}] going idle: no candidate ruleset covers this turn's mining cost ({_costPerAttempt} x {HashPower} = {_costPerAttempt * HashPower})");
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
                // This turn didn't win — none of our HashPower nonce attempts met
                // the target (or we're shutting down). Give the ordinary
                // transactions back so they aren't lost (the coinbase entry, if
                // any, was never "spent" — it just doesn't exist unless this
                // exact block gets mined); we'll get another turn shortly.
                foreach (var tx in pending) _mempool.Enqueue(tx);
                return;
            }

            // Signed with THIS node's own key regardless of what builtBy
            // claims — an Impersonator can put any name it likes in the
            // block, but the signature only ever proves it came from this
            // node's real identity, not the framed one. See the "Signed
            // blocks" note in README.md.
            block.Signature = Sign(block.Hash);

            if (fakeIdentity)
                Console.WriteLine($"[{Id}] (Impersonator) mined block #{block.Index} (nonce {block.Nonce}) falsely claiming it was built by {builtBy}" +
                    (reward > 0m ? $" — the {reward}-coin reward is recorded as paid to {builtBy}, not {Id}" : ""));

            if (_role == NodeRole.Corruptor)
            {
                // Tamper AFTER a valid nonce was already found. If Transactions[0] is
                // the coinbase entry (the common case), this inflates the claimed
                // reward — which gets caught THREE ways: the hash no longer
                // matches the block's contents, a freshly different hash essentially
                // never still satisfies a hard target by chance, AND independently,
                // every peer recomputes what the coinbase amount SHOULD be and will
                // reject a mismatch outright even if the other two checks somehow
                // didn't catch it.
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

        // Mines one turn on behalf of a pool: tries up to `totalHashPower`
        // nonces — the sum of every current member's own HashPower — instead
        // of just this SoloMiner's own. If successful, the coinbase reward is
        // paid to `poolLabel` (not to this node), then immediately split among
        // `members` proportional to each one's HashPower share, as ordinary
        // balance-checked transactions right after the coinbase entry in the
        // very same block. No new validation rules are needed for that split:
        // ValidateChain already accepts it as a plain sequence of regular
        // transactions from an account (the pool) that the coinbase
        // transaction immediately before it, earlier in the same block,
        // already credited — see the "Balances & double-spends" note in
        // README.md. This node's own mempool/chain/network plumbing builds and
        // broadcasts the block; BuiltBy is this node's own Id, since the
        // pool already chose this SoloMiner, by weighted random draw favoring
        // higher-HashPower members, to stand in as its coordinator for this
        // turn — see the "Mining pools" note in README.md.
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
            var simulatedBalances = Ledger.ComputeBalances(ancestors);

            if (reward > 0m)
            {
                txs.Add(new Transaction { From = Economics.CoinbaseSender, To = poolLabel, Amount = reward });
                simulatedBalances[poolLabel] = simulatedBalances.GetValueOrDefault(poolLabel) + reward;

                var distributed = 0m;
                for (int i = 0; i < members.Count; i++)
                {
                    var member = members[i];
                    // The last member absorbs whatever rounding left over, so the
                    // pool's account always nets back to exactly zero rather than
                    // accumulating undistributed dust.
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

            block.Signature = Sign(block.Hash);

            Console.WriteLine($"[{poolLabel}] *** mined block #{block.Index} (nonce {block.Nonce}, target {block.Target[..8]}...) " +
                $"— built by {Id} on behalf of {members.Count} pool member(s) contributing {totalHashPower} combined hash power, " +
                $"reward {reward} split proportionally ***");

            _watcher.ObserveBuild(Id, block, _role);
            _chain.AppendTrusting(block);
            var currentPeers = _getPeerIds();
            await SendBlock(block, currentPeers);
            await SendChain(currentPeers);
        }

        // Equivocator: has to mine TWO separate valid blocks on the same parent to
        // fork the network — real, doubled computational cost. Both blocks claim
        // the same (correct) reward, since only whichever one actually survives on
        // the eventual winning chain will ever count — the other is simply never
        // adopted anywhere.
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

            List<Transaction> BuildTxs(List<Transaction> rest)
            {
                var txs = new List<Transaction>();
                var simulatedBalances = Ledger.ComputeBalances(ancestors);
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
            blockA.Signature = Sign(blockA.Hash);

            var blockB = MineBlock(parent, expectedTarget, rules, txsB, Id, HashPower, token);
            if (blockB == null)
            {
                // Only the first attempt won within its HashPower budget — don't
                // waste the real work already spent; just broadcast what we have,
                // honestly.
                _watcher.ObserveBuild(Id, blockA, _role);
                _chain.AppendTrusting(blockA);
                var earlyPeers = _getPeerIds();
                await SendBlock(blockA, earlyPeers);
                await SendChain(earlyPeers);
                return;
            }
            blockB.Signature = Sign(blockB.Hash);

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
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_serverPort}/{peerId}/receiveBlock")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Add(Node.SenderIdHeaderName, Id);
                    var response = await _http.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"[{Id}] peer {peerId} rejected block #{block.Index}: {body}");
                    }
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
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_serverPort}/{peerId}/receiveChain")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Add(Node.SenderIdHeaderName, Id);
                    var response = await _http.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"[{Id}] peer {peerId} rejected chain: {body}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] couldn't send chain to peer {peerId}: {ex.Message}");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // A mining pool: a named group of SoloMiners (see Miner.cs) that mines as
    // one combined IMiner instead of each member getting its own separate
    // turn — see the "Mining pools" note in README.md. This is where all
    // pool-specific logic lives — combining member HashPower, picking who
    // coordinates a given turn, splitting the reward — so the round-robin
    // scheduler (MiningScheduler.RunAsync) never has to know a pool
    // is anything other than one more IMiner.
    //
    // Membership starts with whatever's passed to the constructor and can
    // grow afterward via AddMember as new nodes join this pool over the
    // network's lifetime (see NodeNetwork.AddNodeAsync) — the pool itself is the
    // one place that needs to track that, precisely so nothing else has to.
    // Reads and writes to the member list are locked because AddMember (from
    // the node-growth loop) and MineOneRoundAsync (from the mining loop) run
    // on independent, concurrently-executing loops.
    // ------------------------------------------------------------------
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

        // This pool's current combined HashPower — see NodeNetwork.GetPoolHashPower,
        // which a candidate-evaluating SoloMiner elsewhere uses to weigh joining.
        public int TotalHashPower { get { lock (_lock) { return _members.Sum(m => m.HashPower); } } }

        // Used by NodeNetwork.RemoveNode (churn) to drop a departing member.
        // Returns whether the id was actually a member — MemberCount == 0
        // afterward tells the caller this pool has no one left and should be
        // torn down entirely, since MineOneRoundAsync assumes at least one
        // member (WeightedRandomMember indexes into a non-empty list).
        public bool RemoveMemberIfPresent(string nodeId)
        {
            lock (_lock) { return _members.RemoveAll(m => m.Id == nodeId) > 0; }
        }

        // Used by NodeNetwork.SwitchPoolMembership, which — unlike
        // RemoveMemberIfPresent — needs the actual SoloMiner object back so it
        // can re-add it wherever the node decided to move to.
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
            // Give every member a chance to reconsider — not just whoever ends
            // up coordinating below, since a low-HashPower member could go a
            // long time without ever being picked. Reconsideration happens
            // against a separate snapshot, then a fresh one is taken for the
            // actual round so a member that just left doesn't still count
            // toward this round's total HashPower or coordinator draw.
            List<SoloMiner> forReconsideration;
            lock (_lock) { forReconsideration = new List<SoloMiner>(_members); }
            foreach (var member in forReconsideration)
                member.ReconsiderPoolMembership();

            List<SoloMiner> members;
            lock (_lock) { members = new List<SoloMiner>(_members); }
            if (members.Count == 0) return; // every member reconsidered its way out this round

            var totalHashPower = members.Sum(m => m.HashPower);
            var coordinator = WeightedRandomMember(members, totalHashPower, _rng);
            await coordinator.MineForPoolAsync(Label, totalHashPower, members, token);
        }

        // Picks one member at random, weighted by each member's own
        // HashPower — this is what determines who coordinates (builds, mines,
        // and broadcasts) this turn on the pool's behalf, and since the
        // coordinator ends up as the block's BuiltBy, it also gives
        // higher-HashPower members a proportionally larger share of that
        // narrative credit.
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

    // ------------------------------------------------------------------
    // Common mining entry point implemented by both SoloMiner (an individual
    // node mining on its own, above) and PoolMiner (a named group of
    // SoloMiners mining as one combined entity, above). The
    // round-robin scheduler (MiningScheduler.RunAsync) works purely
    // in terms of IMiner and deliberately knows nothing about pools, roles,
    // or hash power: it just orders whatever IMiners currently exist and
    // gives each one a turn. All of that — whether a node mines solo or as
    // part of a pool, how a pool picks who coordinates its turn, how a pool
    // splits its reward — is decided when a miner is created (see
    // NodeNetwork.AddNodeAsync) and, for pools, inside PoolMiner itself.
    // ------------------------------------------------------------------
    public interface IMiner
    {
        // Stable identity used to key this miner's spot in the scheduler's
        // per-block random turn order (MiningScheduler.OrderKeys) — a node's Id
        // for a SoloMiner, a pool's name for a PoolMiner.
        string Label { get; }

        // Perform one mining turn: try to find a valid block and broadcast
        // it, or return having found nothing — see the "Mining" note in
        // README.md for what "one turn" means.
        Task MineOneRoundAsync(CancellationToken token);
    }
}
