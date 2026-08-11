// BitcoinNetworkSimulator — now with REAL proof-of-work instead of a coordinator-run race.
//
// WHAT CHANGED FROM THE GUESS-THE-NUMBER VERSION: the target a block's hash must
// satisfy is no longer a secret held by a central coordinator. It's a PUBLIC
// 256-bit number, carried right in the block header (Block.Target), and every
// node independently derives what that target SHOULD be for any given height
// purely from public chain history (prior block timestamps) — see
// ProofOfWork.ComputeExpectedTargetHex. That single change eliminates the need
// for a coordinator entirely: there's no secret left for anyone to hold, so
// there's nothing left for a referee to do. Each node now mines CONTINUOUSLY
// and independently against its own current tip, in its own background loop,
// and broadcasts the moment it finds a valid nonce. This closes the exact gap
// called out in the previous version's big caveat comment.
//
// A pleasant side effect: because multiple nodes now race in real, unsynchronized
// parallel time against the same public target, it's completely normal for two
// honest nodes to find a valid block at nearly the same moment — a genuine,
// non-malicious fork, resolved by whichever branch gets extended first (the
// existing longest-valid-chain gossip/reorg logic, unchanged from before). This
// is the same phenomenon discussed earlier re: real Bitcoin reorgs, now visible
// in this toy for free, without any of the deliberately malicious NodeRoles.
//
// Concurrency: each node's HTTP request handling is backed by an ElasticTaskPool
// that grows/shrinks with load. Mining is single-threaded round-robin across
// all active nodes — node 0 gets a turn, then node 1, then node 2, etc.,
// cycling back to 0 — so there is exactly one CPU-bound mining attempt happening
// at any moment and no per-node background threads. A "turn" is no longer a
// guaranteed win: see SIMULATED HASH POWER below. Nodes validate incoming blocks (correct height, correct parent hash,
// correct expected target, hash actually satisfies that target, recomputed hash
// matches, BuiltBy's signature verifies — see BUILTBY SIGNING below —
// well-formed transactions) before appending, and also gossip their
// FULL local chain after every build; a peer adopts a received chain whenever
// it is strictly longer than its own AND shares the same genesis AND is fully
// valid (including every block's proof-of-work) — this is how a node replaces
// blocks it previously accepted once a longer, valid history shows up (a "reorg").
//
// COIN ISSUANCE: each block's winning miner is paid a coinbase reward — see
// Economics below. It starts at 50 coins, halves every 210 blocks (toy-scaled
// down from Bitcoin's 210,000), and the running total ever minted across the
// whole chain is hard-capped at 21,000,000 — enforced the same way the PoW
// target is: every node independently recomputes the expected reward for any
// block from public chain history and rejects a mismatch. See the note at the
// top of the Economics class for an honest arithmetic caveat: with THESE
// particular numbers, the halving schedule's own asymptotic total is actually
// 21,000 coins, well under the enforced 21,000,000 cap — the cap is real and
// enforced, it just isn't the thing that ends up binding here.
//
// BALANCE ENFORCEMENT: every node also independently recomputes every
// account's balance purely from public chain history (see Ledger.Compute-
// Balances below) — the same "derive it yourself, trust nobody's claim"
// pattern already used for PoW targets and coinbase rewards. ValidateChain
// rejects any block containing a transaction that spends more than its
// sender's balance at that exact point in the chain, which also transitively
// catches double-spends (the second spend of the same coins simply finds the
// balance already gone). Each mining node additionally pre-filters its own
// mempool against this same simulated balance before assembling a candidate
// block, so it doesn't waste real proof-of-work mining a block its peers
// will just reject outright.
//
// SIMULATED HASH POWER: each node has a HashPower rating (default 1, meaning
// "one regular single-hash node"). A round-robin turn is no longer an
// unbounded search that always eventually succeeds — MineBlock now tries at
// most HashPower nonces before giving up and handing the turn to the next
// node. A node with HashPower 1000 therefore gets 1000 tries per turn versus
// a regular node's 1, i.e. it mines roughly 1000x faster in the same sense a
// real miner with 1000x the hash rate finds valid nonces 1000x more often —
// NOT by making success deterministic, just far more probable per turn. This
// is why ProofOfWork.InitialDifficultyShift is now tuned much lower than the
// old "search until found" model needed: with bounded per-turn attempts, a
// target too hard for anyone to plausibly hit within a handful of tries would
// stall the chain before the retarget logic ever got a single block to learn
// from. Turn ORDER is randomized too, not just fixed join order: whoever
// happened to join earliest would otherwise always go first in every round —
// see RoundRobinMiningLoopAsync, which reshuffles who goes first each time a
// new block appears.
//
// MINING PARTICIPATION: mining is now optional per node (NodeMetadata.CanMine,
// default true; every 3rd node defaults to false — see AssignCanMine). A
// wallet-only node (CanMine false) is a completely normal network participant
// otherwise: it serves /chain, /balances, and /mempool, accepts and relays
// transactions via /tx, receives and validates blocks and chains from peers
// exactly like anyone else, and can send or receive coins once it has a
// balance — it simply never gets a round-robin mining turn, so it never builds
// a block or earns a coinbase reward itself. RoundRobinMiningLoopAsync skips
// straight over these nodes when handing out turns.
//
// MINING POOLS: an Honest, CanMine node can subscribe to a named pool
// (NodeMetadata.Pool — null/empty means solo, the default). Every current
// Honest member of the same pool has its HashPower combined into ONE shared
// round-robin turn instead of each getting its own separate one — this is
// what lets ten nodes with HashPower 100 each genuinely compete, as a single
// 1000-strong entity, against one node with HashPower 1000 solo, rather than
// just taking ten separate, individually-weaker shots at the target. A pool
// is a PoolMiner (see PoolMiner.cs): on its turn it sums its members'
// HashPower and picks one member at random — weighted by that member's own
// HashPower share — to coordinate: build the candidate block, run the (now
// much larger) nonce search, and broadcast if it wins. That coordinator's Id
// ends up as the block's BuiltBy, purely as flavor; it has no bearing on
// payout. The reward itself is paid to the pool's own account first
// (coinbase To = "pool:name"), then immediately split among every current
// member proportional to their HashPower share, as ordinary transactions
// right after the coinbase entry in that same block. No new consensus rule is
// needed to enforce a correct split: ValidateChain already accepts it as a
// plain sequence of regular, balance-checked transactions spending an account
// (the pool) that the coinbase transaction immediately before it, earlier in
// the very same block, already credited — see BALANCE ENFORCEMENT above. Pool
// membership itself is pure local mining configuration, like HashPower and
// CanMine — it isn't gossiped or otherwise part of chain consensus, only the
// resulting transactions are. Malicious NodeRoles can't pool (a Pool value on
// a non-Honest node is ignored, and AddNodeAsync always gives it its own
// SoloMiner turn instead of routing it into a PoolMiner) — mixing pooled
// payouts with equivocation, impersonation, or corruption would multiply the
// number of edge cases far out of proportion to what this toy is trying to
// demonstrate. The round-robin scheduler treats a PoolMiner as just another
// IMiner (see IMiner.cs) — it has no pool-specific logic of its own at all.
//
// BUILTBY SIGNING: every block's BuiltBy claim is now backed by a real
// signature, not just an unverified label. Each node generates (or, on
// restart, reloads — see PERSISTENCE & RESUME below) its own ECDSA keypair
// the moment its SoloMiner is constructed, and immediately registers the
// public half under its own Id in NodeIdentityRegistry — a process-wide
// table binding names to keys, established independently of anything any
// block claims about itself. Every block a SoloMiner mines, solo or on a
// pool's behalf, gets signed with that same key before being broadcast.
// ValidateChain looks up whatever key is registered for a block's BuiltBy
// and rejects the block outright if there isn't one, or if the signature
// doesn't verify against it. This is what finally closes the gap an
// Impersonator relies on: it can still put any name it likes in BuiltBy, but
// it can only sign with its own real key, and that signature will never
// verify against the name it's framing — the deception gets caught at
// validation, everywhere, before the reward redirection it used to pull off
// ever sticks.
//
// What's still missing, by design:
//   - mining difficulty here is tiny compared to real Bitcoin (tunable via
//     ProofOfWork.InitialDifficultyShift) — this is a toy meant to demonstrate
//     the MECHANISM, not to be remotely secure against a real attacker
//
// SCENARIO EXECUTION: how a run starts up and how long it lasts can be
// declared up front in a scenario file (see Scenario.cs for the full format,
// and the Scenarios/ directory for a default set covering each major feature
// — hash power disparity, pooling, malicious roles, wallet-only nodes,
// large-scale organic growth) instead of hand-editing metadata.json files
// and eyeballing the clock. At startup, Main looks for a scenario.json next
// to the executable — or a specific file passed as
// `dotnet run -- path/to/file.json` (relative to the current directory,
// which is why `dotnet run -- Scenarios/mining-pool-fairness.json` from the
// project root just works) — and, if found, applies it (see
// Program.ApplyScenarioAsync) before anything else happens: NodeGroups
// describe the starting population as a list of {Count, NodeRole, HashPower,
// CanMine, Pool} groups applied in order (e.g. ten plain nodes then five
// pooled ones creates 15 total, matching how node identity is already
// assigned positionally by join index), AutoGrowth (default true) can be set
// false to freeze the network at exactly that count instead of also growing
// organically, GrowthIntervalSeconds/MaxNodes optionally retune organic
// growth's pace/cap when it stays on, and DurationSeconds automatically
// triggers the same clean shutdown pressing Enter does once that many
// seconds have passed (Enter still works too, to stop early). No
// scenario.json at all reproduces the exact behavior this project always
// had: one node, organic growth, run until Enter — except for where its
// output goes, see RESULTS below.
//
// RESULTS: every run — scenario-driven or not — gets its own directory,
// ScenarioResults/<timestamp>-<name>/ (DetermineRunRootDir, called before
// anything else in Main), where <name> is the scenario file's name without
// its extension, or "no-scenario" when none was used. All of that run's node
// folders (nodes/<node-id>/, holding blockchain.json and metadata.json —
// see PERSISTENCE & RESUME below) and its watcher-report.json land inside
// that one timestamped folder instead of always overwriting the same nodes/
// next to the executable, so separate runs never collide and are each
// independently reviewable afterward. When a scenario was used,
// DetermineRunRootDir also copies that exact scenario file, unmodified, into
// the new result folder — so the folder is a self-contained record of both
// what happened and exactly what configuration produced it, without having
// to go find Scenarios/whatever.json separately (which may have since been
// edited or deleted). One consequence of every run getting a brand new,
// empty folder: "an existing node identity is preserved rather than
// regenerated" (ApplyScenarioAsync's SigningKey handling) essentially never
// has an existing file to find under this default automatic layout — it
// only matters if something (a script, a future feature, you by hand)
// points two separate runs at literally the same ScenarioResults folder.
//
// PERSISTENCE & RESUME: each node gets its own directory, nodes/<node-id>/,
// holding two files. blockchain.json is its chain — on startup, before a
// node starts serving traffic, it looks for its own file and — if the saved
// chain is structurally valid, including every block's proof-of-work, and
// shares this build's canonical genesis — resumes from it instead of
// starting over at genesis. metadata.json holds that node's NodeRole,
// HashPower, CanMine, and SigningKey (see BUILTBY SIGNING above) — on
// startup a node loads whatever is already on disk there (so a role, hash
// power, or mining-participation flag you hand-edit into the file, or that
// got assigned on a previous run, survives a restart); only when no
// metadata.json exists yet — or one exists but predates SigningKey — does a
// node get fresh defaults (HashPower 1, CanMine from the pattern below, role
// from the pattern below, a freshly generated signing key) written out.
// Unlike the other fields, SigningKey is not meant to be hand-edited or
// deleted once a node has mined blocks: doing so orphans every historical
// block it signed, since the key registered for its Id on the next run would
// no longer be the one that actually signed that history. Every persisted
// metadata.json across all of nodes/ gets preloaded into NodeIdentityRegistry
// before any node starts joining or resuming this run — see
// PreloadKnownSigningKeysAsync — so a resumed chain's historical BuiltBy
// claims can be verified regardless of which order nodes happen to (re)join
// in.
//
// NODE ROLES: the network includes both honest and malicious nodes (see
// NodeRole below), normally assigned by the pattern below but overridable per
// node via metadata.json as described above. Equivocator now has to mine TWO separate valid blocks to
// fork the chain — real work, not a free action like in the earlier versions —
// which is exactly why equivocation is naturally rare and costly under real PoW.
// Impersonator still does the same real mining work as everyone else and still
// tries to claim a different node's identity — but now gets caught too: it can
// only sign a block with its own real key, which never verifies against
// whatever key is actually registered for the name it's framing (see BUILTBY
// SIGNING above), so the block is rejected everywhere rather than redirecting
// the reward. Corruptor tampers with a block AFTER finding a valid
// nonce, which now gets caught TWICE over: the hash no longer matches the
// block's contents, AND (independently) a randomly-different hash essentially
// never still satisfies a hard target by chance. Withholder only tells some
// peers about a new block; they catch up via the next round's full-chain gossip.
//
// RUN:
//   dotnet run
// (First run: `dotnet new console` scaffolding is already assumed if you dropped
// this file into a project. Or just: mkdir BitcoinNetworkSimulator && cd BitcoinNetworkSimulator && dotnet
// new console and overwrite Program.cs with this file.)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
        // private signing key — see NodeIdentityRegistry and BUILTBY SIGNING
        // at the top of the file. Deliberately excluded from ComputeHash's
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
        // on average. Tuned small/fast for a toy demo, unlike Bitcoin's real
        // 2016-block / ~2-week retarget window and 10-minute block target.
        public const int RetargetIntervalBlocks = 10;
        public const double TargetSecondsPerBlock = 3.0;

        // Bitcoin-style clamp so a single retarget can't swing wildly in either
        // direction, even if the last interval's timing was a fluke.
        public const double MinAdjustmentFactor = 0.25;
        public const double MaxAdjustmentFactor = 4.0;

        // Higher = harder (lower per-attempt success probability, slower
        // blocks). Lower = easier (faster blocks, good for a quick demo).
        // Chosen much lower than a "search until found" model would need,
        // because MineBlock now only gets a bounded number of attempts per
        // turn (a node's HashPower — see SIMULATED HASH POWER at the top of
        // the file) rather than searching indefinitely: at shift 8, a single
        // attempt succeeds with probability 1/256, so a regular (HashPower 1)
        // node still has a real, if modest, chance each turn, while a node
        // with HashPower 1000 succeeds on the vast majority of its turns —
        // exactly the "1000x more likely to win" effect simulated hash power
        // is meant to produce.
        public const int InitialDifficultyShift = 8;

        public static readonly BigInteger MaxTarget = (BigInteger.One << 256) - 1;
        public static readonly BigInteger InitialTarget = MaxTarget >> InitialDifficultyShift;

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
        // Known toy quirk, left in deliberately rather than engineered around:
        // genesis has a fixed, hardcoded timestamp (see CreateGenesisBlock), so
        // the very FIRST retarget interval spans from that fixed point to whenever
        // you actually ran the demo — a huge apparent elapsed time. That first
        // retarget will almost always saturate the MaxAdjustmentFactor clamp
        // (target gets 4x easier). Every retarget after that behaves normally,
        // based purely on real elapsed mining time between real blocks.
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
    // ARITHMETIC NOTE, worth being upfront about: real Bitcoin's constants
    // (50 coins, halving every 210,000 blocks) are tuned so the reward series
    // converges to exactly 21,000,000: 210,000 * 50 * (1 + 1/2 + 1/4 + ...) =
    // 210,000 * 50 * 2 = 21,000,000. Here, halving every 210 blocks (instead of
    // 210,000 — scaled down 1000x for a fast demo) with the SAME 50-coin reward
    // converges to only 210 * 50 * 2 = 21,000 total coins — the natural
    // asymptotic supply is 21,000, not 21,000,000. MaxSupply below is still
    // implemented as a real, enforced hard cap (and the check exists exactly
    // like it would in a "real" implementation), it's just that with these
    // particular numbers the halving schedule alone will never actually reach
    // it — the cap is a ceiling far above what mining could ever produce. If you
    // want the cap to actually bind, either shrink MaxSupply to match (21,000)
    // or scale HalvingIntervalBlocks up to 210,000 to match real Bitcoin's ratio.
    public static class Economics
    {
        public const string CoinbaseSender = "coinbase";
        public const decimal InitialBlockReward = 50m;
        public const int HalvingIntervalBlocks = 210;
        public const decimal MaxSupply = 21_000_000m;

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

    // Thread-safe append-only chain shared by the JSON persistence layer.
    // Each "node" below keeps its OWN copy of a Blockchain to simulate a real
    // distributed system where nodes can (and, in this naive design, do) disagree.
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
        //     must actually verify against that key — see BUILTBY SIGNING at
        //     the top of the file
        // Unlike the earlier coordinator-picked versions, being selected to build
        // now genuinely costs something real: computational search work.
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

    // Persisted, hand-editable per-node configuration — see PERSISTENCE & RESUME
    // at the top of the file. Lives at nodes/<node-id>/metadata.json, alongside
    // that node's blockchain.json. NodeRole is serialized as its string name
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
        // relay-only participant. See MINING PARTICIPATION at the top of the file.
        public bool CanMine { get; set; } = true;
        // Null/empty = mines solo (default). Otherwise, the name of a mining
        // pool this node subscribes to — its HashPower is combined with every
        // other current member's into the pool's single shared turn instead of
        // getting its own. Only honored for NodeRole.Honest nodes; a malicious
        // role's Pool value, if set, is ignored and it always mines solo. See
        // MINING POOLS at the top of the file.
        public string? Pool { get; set; } = null;
        // Base64-encoded DER (ECDsa.ExportECPrivateKey) signing identity key
        // — see BUILTBY SIGNING at the top of the file. Unlike every other
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
        private const int BasePort = 5000;
        private const int MaxNodes = 100;
        private const int GrowthIntervalMs = 8000; // roughly double the network every 8 s — see NodeGrowthLoopAsync
        private static readonly Random Rng = new();

        // Root directory for this run's node folders and watcher-report.json
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
        // LoadOrCreateMetadataAsync and MINING PARTICIPATION at the top of the
        // file.
        private static bool AssignCanMine(int index) => index % 3 != 2;

        // Shared, lock-protected registry — nodes call the getters at broadcast time
        // so newly joined peers are automatically included without any wiring.
        private static readonly object NetworkLock = new();
        private static readonly List<int> AllPorts = new();
        private static readonly List<string> AllNodeIds = new();
        private static readonly List<Node> AllNodes = new();
        private static readonly List<Task> PersistTasks = new();

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
        // AllNodes/AllPorts/AllNodeIds since the scheduler reads AllMiners
        // concurrently with node growth.
        private static readonly List<IMiner> AllMiners = new();
        private static readonly Dictionary<string, PoolMiner> PoolMinersByName = new();

        private static List<int> GetAllPorts() { lock (NetworkLock) { return new List<int>(AllPorts); } }
        private static List<string> GetAllNodeIds() { lock (NetworkLock) { return new List<string>(AllNodeIds); } }

        private static string NodeDirFor(string nodeId) =>
            Path.Combine(RunRootDir, "nodes", nodeId);

        private static string PersistPathFor(string nodeId) =>
            Path.Combine(NodeDirFor(nodeId), "blockchain.json");

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
        // re-saved immediately, exactly as if it were brand new — see BUILTBY
        // SIGNING at the top of the file for why that key, once established,
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
        // preserved rather than regenerated — see PERSISTENCE & RESUME and
        // BUILTBY SIGNING at the top of the file for why — so re-running the
        // same scenario keeps building on the same node identities and chain
        // history instead of resetting to genesis every time. Only a
        // genuinely new position gets a freshly generated key. See SCENARIO
        // EXECUTION at the top of the file.
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
            // growth behavior, and duration — see SCENARIO EXECUTION at the
            // top of the file. `dotnet run -- path/to/scenario.json` picks a
            // specific file; otherwise scenario.json next to the executable
            // is used if present. No file at all means the normal
            // single-node, indefinite-runtime default, unchanged from before
            // this feature existed.
            var scenarioPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "scenario.json");
            var scenario = await ScenarioLoader.LoadAsync(scenarioPath);

            // Every run's node folders and watcher-report.json land under
            // their own timestamped ScenarioResults/ subfolder from this
            // point on — see SCENARIO EXECUTION at the top of the file.
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
            var watcher = new ChainWatcher(new List<int>(), new List<string>());

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
            var watcherPersistTask = WatcherPersistenceLoopAsync(watcher, cts.Token);

            Console.WriteLine($"Node 0 ({NodeNameFor(0)}) listening on port {BasePort}." +
                (autoGrowthEnabled ? " Network grows automatically." : " Auto-growth disabled — network stays fixed."));
            Console.WriteLine("Try: curl http://localhost:5000/chain");
            Console.WriteLine("Or:  curl http://localhost:5000/balances");
            Console.WriteLine("Watcher: inspect watcher-report.json for convergence/recovery history.");

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
            List<Node> nodeSnapshot;
            lock (NetworkLock) { nodeSnapshot = new List<Node>(AllNodes); }
            foreach (var node in nodeSnapshot) node.Stop();

            try
            {
                List<Task> persistSnapshot;
                lock (NetworkLock) { persistSnapshot = new List<Task>(PersistTasks); }
                await Task.WhenAll(
                    new[] { miningTask, txTask, growthTask, watcherTask, watcherPersistTask }
                    .Concat(persistSnapshot));
            }
            catch (OperationCanceledException) { }
            Console.WriteLine("Stopped.");
        }

        private static async Task AddNodeAsync(ChainWatcher watcher, CancellationToken token)
        {
            int index;
            lock (NetworkLock) { index = AllPorts.Count; }

            var id = NodeNameFor(index);
            var port = BasePort + index;

            Directory.CreateDirectory(NodeDirFor(id));
            var metadata = await LoadOrCreateMetadataAsync(id, index);

            lock (NetworkLock)
            {
                AllPorts.Add(port);
                AllNodeIds.Add(id);
            }
            watcher.AddNode(port, id);

            // Composition root: Chain and Mempool are constructed once here and
            // shared between Node (which serves them over HTTP) and SoloMiner
            // (which reads/mutates them while mining) — see the comments atop
            // Node.cs and Miner.cs for why SoloMiner takes these directly
            // instead of holding a reference back to the Node it mines for.
            var chain = new Blockchain();
            var mempool = new ConcurrentQueue<Transaction>();
            var signingKey = ImportSigningKey(metadata.SigningKey!);
            var soloMiner = new SoloMiner(id, port, metadata.NodeRole, metadata.HashPower, chain, mempool, GetAllPorts, GetAllNodeIds, watcher, signingKey);
            var node = new Node(id, port, chain, mempool, watcher);
            await ResumeNodeFromDiskAsync(node);
            node.Start();

            lock (NetworkLock)
            {
                AllNodes.Add(node);
                PersistTasks.Add(PersistenceLoopAsync(node, token));

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

            Console.WriteLine($"[network] node #{index} ({id}, {metadata.NodeRole}, hashPower={metadata.HashPower}, canMine={metadata.CanMine}, pool={metadata.Pool ?? "(solo)"}) joined on port {port} — total: {index + 1}");
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

        // Rotates across mining "turns" — see MINING PARTICIPATION and MINING
        // POOLS at the top of the file. Deliberately knows nothing about
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

        private static async Task ResumeNodeFromDiskAsync(Node node)
        {
            var persistPath = PersistPathFor(node.Id);

            if (!File.Exists(persistPath))
            {
                Console.WriteLine($"[{node.Id}] no saved chain found ({Path.GetFileName(persistPath)}); starting from genesis");
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(persistPath);
                var candidate = JsonSerializer.Deserialize<List<Block>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (candidate == null || candidate.Count == 0)
                {
                    Console.WriteLine($"[{node.Id}] saved chain file was empty or unreadable; starting from genesis");
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
                    var ports = GetAllPorts();
                    var nodeIds = GetAllNodeIds();
                    if (ports.Count == 0 || nodeIds.Count == 0) { if (!await DelayOrCancelled(500, token)) break; continue; }

                    var queryPort = ports[Rng.Next(ports.Count)];
                    var chainJson = await http.GetStringAsync($"http://localhost:{queryPort}/chain", token);
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

                    var targetPort = ports[Rng.Next(ports.Count)];
                    var json = JsonSerializer.Serialize(tx);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await http.PostAsync($"http://localhost:{targetPort}/tx", content, token);
                }
                catch (OperationCanceledException) { break; }
                catch { }

                if (!await DelayOrCancelled(1500, token)) break;
            }
        }

        private static async Task PersistenceLoopAsync(Node node, CancellationToken token)
        {
            var persistPath = PersistPathFor(node.Id);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = node.Chain.Snapshot();
                    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(persistPath, json, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[persistence:{node.Id}] failed to write {persistPath}: {ex.Message}");
                }

                if (!await DelayOrCancelled(3000, token)) break;
            }
        }

        private static async Task WatcherPersistenceLoopAsync(ChainWatcher watcher, CancellationToken token)
        {
            var path = Path.Combine(RunRootDir, "watcher-report.json");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var json = JsonSerializer.Serialize(watcher.Report(), new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(path, json, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[watcher-persistence] failed to write {path}: {ex.Message}");
                }

                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { break; }
            }

            try
            {
                var json = JsonSerializer.Serialize(watcher.Report(), new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json);
            }
            catch { }
        }
    }
}
