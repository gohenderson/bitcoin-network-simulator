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
        public int HashPower { get; }
        public string Label => Id;

        private readonly int _serverPort;
        private readonly NodeRole _role;
        private readonly Blockchain _chain;
        private readonly ConcurrentQueue<Transaction> _mempool;
        private readonly Func<List<string>> _getAllNodeIds;
        private readonly ChainWatcher _watcher;
        private readonly ECDsa _signingKey;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private readonly Random _rng = new(Guid.NewGuid().GetHashCode());

        // `serverPort` is the single port the whole network's NetworkServer
        // listens on (see NetworkServer.cs) — every peer URL this miner
        // builds is http://localhost:{serverPort}/{peerId}/... — not a
        // per-node port anymore.
        public SoloMiner(string id, int serverPort, NodeRole role, int hashPower, Blockchain chain, ConcurrentQueue<Transaction> mempool,
            Func<List<string>> getAllNodeIds, ChainWatcher watcher, ECDsa signingKey)
        {
            Id = id;
            _serverPort = serverPort;
            _role = role;
            HashPower = Math.Max(1, hashPower);
            _chain = chain;
            _mempool = mempool;
            _getAllNodeIds = getAllNodeIds;
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
        private Block? MineBlock(Block parent, string expectedTargetHex, List<Transaction> txs, string builtByLabel, int attempts, CancellationToken token)
        {
            var candidate = new Block
            {
                Index = parent.Index + 1,
                PreviousHash = parent.Hash,
                BuiltBy = builtByLabel,
                Transactions = txs,
                Target = expectedTargetHex,
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
            var expectedTarget = ProofOfWork.ComputeExpectedTargetHex(ancestors);
            var height = parent.Index + 1;
            var reward = Economics.ComputeBlockReward(ancestors, height);

            var pending = new List<Transaction>();
            while (_mempool.TryDequeue(out var tx)) pending.Add(tx);

            var fakeIdentity = _role == NodeRole.Impersonator;
            var builtBy = fakeIdentity
                ? (_getAllNodeIds().Where(n => n != Id).OrderBy(_ => _rng.Next()).FirstOrDefault() ?? Id)
                : Id;

            var txs = new List<Transaction>();
            var simulatedBalances = Ledger.ComputeBalances(ancestors);
            if (reward > 0m)
            {
                txs.Add(new Transaction { From = Economics.CoinbaseSender, To = builtBy, Amount = reward });
                simulatedBalances[builtBy] = simulatedBalances.GetValueOrDefault(builtBy) + reward;
            }
            txs.AddRange(FilterAffordable(pending, simulatedBalances));

            var block = MineBlock(parent, expectedTarget, txs, builtBy, HashPower, token);
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
                // reward — which now gets caught THREE ways: the hash no longer
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

            var currentPeers = _getAllNodeIds();
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
            var expectedTarget = ProofOfWork.ComputeExpectedTargetHex(ancestors);
            var height = parent.Index + 1;
            var reward = Economics.ComputeBlockReward(ancestors, height);

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

            var block = MineBlock(parent, expectedTarget, txs, Id, totalHashPower, token);
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
            var currentPeers = _getAllNodeIds();
            await SendBlock(block, currentPeers);
            await SendChain(currentPeers);
        }

        // Equivocator: has to mine TWO separate valid blocks on the same parent to
        // fork the network — real, doubled computational cost, unlike the earlier
        // free-forking versions. Both blocks claim the same (correct) reward, since
        // only whichever one actually survives on the eventual winning chain will
        // ever count — the other is simply never adopted anywhere.
        private async Task MineAndBroadcastEquivocationAsync(CancellationToken token)
        {
            var parent = _chain.Latest;
            var ancestors = _chain.Snapshot();
            var expectedTarget = ProofOfWork.ComputeExpectedTargetHex(ancestors);
            var height = parent.Index + 1;
            var reward = Economics.ComputeBlockReward(ancestors, height);

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

            var blockA = MineBlock(parent, expectedTarget, txsA, Id, HashPower, token);
            if (blockA == null)
            {
                foreach (var tx in pending) _mempool.Enqueue(tx);
                return;
            }
            blockA.Signature = Sign(blockA.Hash);

            var blockB = MineBlock(parent, expectedTarget, txsB, Id, HashPower, token);
            if (blockB == null)
            {
                // Only the first attempt won within its HashPower budget — don't
                // waste the real work already spent; just broadcast what we have,
                // honestly.
                _watcher.ObserveBuild(Id, blockA, _role);
                _chain.AppendTrusting(blockA);
                var earlyPeers = _getAllNodeIds();
                await SendBlock(blockA, earlyPeers);
                await SendChain(earlyPeers);
                return;
            }
            blockB.Signature = Sign(blockB.Hash);

            _watcher.ObserveBuild(Id, blockA, _role);
            _watcher.ObserveBuild(Id, blockB, _role);
            _chain.AppendTrusting(blockA);

            var currentPeers = _getAllNodeIds();
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
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await _http.PostAsync($"http://localhost:{_serverPort}/{peerId}/receiveBlock", content);
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
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await _http.PostAsync($"http://localhost:{_serverPort}/{peerId}/receiveChain", content);
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

        public async Task MineOneRoundAsync(CancellationToken token)
        {
            List<SoloMiner> members;
            lock (_lock) { members = new List<SoloMiner>(_members); }

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
