using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Node roles. Honest is the baseline. The others each violate exactly one of
    // the trust assumptions this system still makes, on purpose — see "What
    // this is not" in README.md for the full list of gaps.
    // ------------------------------------------------------------------

    public enum NodeRole
    {
        Honest,
        Equivocator,
        Impersonator,
        Corruptor,
        Withholder
    }

    // ------------------------------------------------------------------
    // A "node" — an independent view of the chain and its own mempool of
    // pending transactions. Every node in the simulation shares a single
    // real HTTP listener (see NetworkServer.cs), which dispatches by node
    // id — the first path segment of a request, e.g.
    // http://localhost:5000/000-alpha/chain — and hands the rest of the
    // route to that node's own HandleRequestAsync below. Node itself owns
    // everything about what a request DOES (validating and accepting what
    // peers send it), not how it physically arrives.
    // Mining lives entirely outside Node, in SoloMiner/PoolMiner/IMiner (see
    // Miner.cs) — NodeNetwork.AddNodeAsync (the
    // composition root) constructs a Node's Chain and Mempool once and
    // shares those same instances with that node's SoloMiner, but Node
    // itself holds no reference to it and knows nothing about mining,
    // roles, hash power, or pools; the round-robin scheduler talks to
    // IMiners directly, never through Node.
    //
    // Node also relays: since each node only gossips directly to its own
    // limited peer set (see the "Peer topology" note in README.md) rather
    // than the whole network, a block or chain accepted from one peer is
    // forwarded on to this node's OTHER peers (RelayBlockAsync/
    // RelayChainAsync below) so it keeps propagating hop by hop. This only
    // ever fires on a genuine state change (Chain.TryAppend/
    // TryReplaceWithLongerChain succeeding), so a peer re-relaying something
    // this node already has is just a harmless, self-terminating rejection
    // — the same property real flood-fill gossip relies on.
    // ------------------------------------------------------------------

    public class Node
    {
        // Carries the sending node's own Id on every /receiveBlock and
        // /receiveChain POST — see the "Peer discouragement" note in
        // README.md. There's no persistent TCP connection to key a sender off
        // of the way real Bitcoin does; this header is the stand-in.
        public const string SenderIdHeaderName = "X-Sender-Id";

        public string Id { get; }
        public Blockchain Chain { get; }
        public ConcurrentQueue<Transaction> Mempool { get; }

        private readonly ChainWatcher _watcher;
        private readonly int _serverPort;
        private readonly Func<List<string>> _getPeerIds;
        private readonly Action<string> _discouragePeer;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

        public Node(string id, Blockchain chain, ConcurrentQueue<Transaction> mempool, ChainWatcher watcher, int serverPort, Func<List<string>> getPeerIds, Action<string> discouragePeer)
        {
            Id = id;
            Chain = chain;
            Mempool = mempool;
            _watcher = watcher;
            _serverPort = serverPort;
            _getPeerIds = getPeerIds;
            _discouragePeer = discouragePeer;
        }

        // `route` is the request path with this node's id segment already
        // stripped off by NetworkServer — e.g. "/chain" for a request to
        // /000-alpha/chain.
        public async Task HandleRequestAsync(HttpListenerContext ctx, string route)
        {
            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                string responseBody;
                res.ContentType = "application/json";

                switch (route)
                {
                    case "/chain":
                        responseBody = JsonSerializer.Serialize(Chain.Snapshot(),
                            new JsonSerializerOptions { WriteIndented = true });
                        break;

                    case "/mempool":
                        responseBody = JsonSerializer.Serialize(Mempool.ToArray());
                        break;

                    case "/balances":
                        responseBody = JsonSerializer.Serialize(Ledger.ComputeBalances(Chain.Snapshot()));
                        break;

                    case "/tx" when req.HttpMethod == "POST":
                        {
                            using var reader = new StreamReader(req.InputStream);
                            var body = await reader.ReadToEndAsync();
                            var tx = JsonSerializer.Deserialize<Transaction>(body,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (tx == null || string.IsNullOrWhiteSpace(tx.From) || string.IsNullOrWhiteSpace(tx.To) || tx.Amount <= 0)
                            {
                                res.StatusCode = 400;
                                responseBody = "{\"status\":\"bad transaction\"}";
                            }
                            else if (tx.From != Economics.CoinbaseSender &&
                                Ledger.GetBalance(Chain.Snapshot(), tx.From) is var available && tx.Amount > available)
                            {
                                // Best-effort only: this checks against our own current tip, not
                                // whatever else is already sitting in the mempool. The real
                                // authority is the running-balance simulation each miner does
                                // when assembling a block (FilterAffordable) and the check
                                // ValidateChain performs on every block any peer receives — this
                                // just rejects an obviously-bad transaction early instead of
                                // letting it sit in the mempool forever.
                                res.StatusCode = 400;
                                responseBody = JsonSerializer.Serialize(new
                                {
                                    status = "rejected",
                                    reason = $"insufficient balance: {tx.From} has {available}, tried to send {tx.Amount}"
                                });
                            }
                            else
                            {
                                Mempool.Enqueue(tx);
                                responseBody = "{\"status\":\"accepted\"}";
                            }
                            break;
                        }

                    case "/receiveBlock" when req.HttpMethod == "POST":
                        {
                            var senderId = req.Headers[SenderIdHeaderName];
                            if (!IsStillAPeer(senderId, out responseBody))
                            {
                                res.StatusCode = 403;
                                break;
                            }

                            using var reader = new StreamReader(req.InputStream);
                            var body = await reader.ReadToEndAsync();
                            var block = JsonSerializer.Deserialize<Block>(body,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (block != null)
                            {
                                var (ok, reason, attributable) = Chain.TryAppend(block);
                                if (ok)
                                {
                                    Console.WriteLine($"[{Id}] accepted block #{block.Index} built by {block.BuiltBy} (validated: parent + target + hash + coinbase + tx checks passed)");
                                    _watcher.ObserveAccepted(Id, block);
                                    responseBody = "{\"status\":\"appended\"}";
                                    _ = RelayBlockAsync(block);
                                }
                                else
                                {
                                    Console.WriteLine($"[{Id}] REJECTED block #{block.Index} from {block.BuiltBy}: {reason}");
                                    _watcher.ObserveRejected(Id, block, reason);
                                    if (attributable) DiscourageSender(senderId, reason);
                                    res.StatusCode = 409;
                                    responseBody = JsonSerializer.Serialize(new { status = "rejected", reason });
                                }
                            }
                            else
                            {
                                res.StatusCode = 400;
                                responseBody = "{\"status\":\"bad block\"}";
                            }
                            break;
                        }

                    case "/receiveChain" when req.HttpMethod == "POST":
                        {
                            var senderId = req.Headers[SenderIdHeaderName];
                            if (!IsStillAPeer(senderId, out responseBody))
                            {
                                res.StatusCode = 403;
                                break;
                            }

                            using var reader = new StreamReader(req.InputStream);
                            var body = await reader.ReadToEndAsync();
                            var candidate = JsonSerializer.Deserialize<List<Block>>(body,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                            if (candidate != null)
                            {
                                var (replaced, reason, attributable) = Chain.TryReplaceWithLongerChain(candidate);

                                if (replaced)
                                {
                                    Console.WriteLine($"[{Id}] REORGANIZED: {reason}");
                                    _watcher.ObserveReorganization(Id, reason);
                                    responseBody = JsonSerializer.Serialize(new { status = "reorganized", reason });
                                    _ = RelayChainAsync(candidate);
                                }
                                else
                                {
                                    if (attributable) DiscourageSender(senderId, reason);
                                    responseBody = JsonSerializer.Serialize(new { status = "ignored", reason });
                                }
                            }
                            else
                            {
                                res.StatusCode = 400;
                                responseBody = "{\"status\":\"bad chain\"}";
                            }

                            break;
                        }

                    default:
                        res.StatusCode = 404;
                        responseBody = "{\"error\":\"not found\"}";
                        break;
                }

                var buffer = Encoding.UTF8.GetBytes(responseBody);
                res.ContentLength64 = buffer.Length;
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                res.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Id}] request error: {ex.Message}");
            }
        }

        // Refuses a request from a peer this node has already discouraged (see
        // DiscourageSender below) — the closest a stateless HTTP POST can get
        // to a real node simply refusing a banned peer's connection outright,
        // before any validation is even attempted. A missing sender id is let
        // through rather than refused, since there's no one to attribute it
        // to either way (every current caller does send one).
        private bool IsStillAPeer(string? senderId, out string rejectionBody)
        {
            if (string.IsNullOrEmpty(senderId) || _getPeerIds().Contains(senderId))
            {
                rejectionBody = "";
                return true;
            }
            Console.WriteLine($"[{Id}] refused request from {senderId}: no longer a peer (discouraged)");
            rejectionBody = JsonSerializer.Serialize(new { status = "refused", reason = "not a peer" });
            return false;
        }

        // See the "Peer discouragement" note in README.md. Called only for
        // rejections TryAppend/TryReplaceWithLongerChain flagged
        // AttributableToSender — a genuine consensus-rule violation in data
        // senderId itself supplied, not just normal network timing. A missing
        // senderId (a request that arrived with no attribution) can't be
        // discouraged.
        private void DiscourageSender(string? senderId, string reason)
        {
            if (string.IsNullOrEmpty(senderId)) return;
            Console.WriteLine($"[{Id}] discouraging peer {senderId}: {reason}");
            _watcher.ObserveDiscouraged(Id, senderId, reason);
            _discouragePeer(senderId);
        }

        // Forwards a block/chain this node just accepted from one peer on to
        // its OTHER peers, so it keeps propagating hop by hop across a peer
        // graph where no single node is connected to everyone — see the
        // "Peer topology" note in README.md. Best-effort and fire-and-forget
        // (called via `_ = RelayBlockAsync(...)`, never awaited by the
        // request handler): a peer that's unreachable or already has this
        // block just gets skipped or rejects it, same as any other gossip.
        private async Task RelayBlockAsync(Block block)
        {
            var json = JsonSerializer.Serialize(block);
            foreach (var peerId in _getPeerIds())
            {
                if (peerId == Id) continue;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_serverPort}/{peerId}/receiveBlock")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Add(SenderIdHeaderName, Id);
                    await _http.SendAsync(request);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] couldn't relay block #{block.Index} to peer {peerId}: {ex.Message}");
                }
            }
        }

        private async Task RelayChainAsync(List<Block> chain)
        {
            var json = JsonSerializer.Serialize(chain);
            foreach (var peerId in _getPeerIds())
            {
                if (peerId == Id) continue;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_serverPort}/{peerId}/receiveChain")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Add(SenderIdHeaderName, Id);
                    await _http.SendAsync(request);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] couldn't relay chain to peer {peerId}: {ex.Message}");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // A process-wide, append-only registry binding each node's Id to the
    // public key it signs blocks with — see the "Signed blocks" note in
    // README.md. This binding is what makes a block's BuiltBy claim
    // verifiable: not the mere existence of a signature (anyone can generate
    // a keypair and sign something), but that the signature verifies against
    // THIS registry's independently-established record of which key belongs
    // to which name — established once, the moment a node comes online (see
    // SoloMiner's constructor), before that name could ever legitimately
    // appear as BuiltBy in any block. An Impersonator can still put any name
    // it likes in BuiltBy, but it can only sign with its own real key, which
    // won't verify against whatever key is actually registered for the name
    // it's framing.
    //
    // Every node in this simulation runs in the same process, so an
    // in-memory static table is a faithful enough stand-in for what a real
    // network would need some independent channel (a genesis validator list,
    // an on-chain registration transaction, a PKI) to establish — the point
    // being demonstrated is the verification mechanism, not how identities
    // get bootstrapped in the first place.
    // ------------------------------------------------------------------
    public static class NodeIdentityRegistry
    {
        private static readonly object Lock = new();
        private static readonly Dictionary<string, byte[]> PublicKeysById = new();

        // A no-op if `nodeId` is already registered — a node's key is fixed
        // for its lifetime (including across restarts, since SoloMiner is
        // handed a key loaded from disk when one already exists), so the
        // first registration is always the durable one.
        public static void Register(string nodeId, byte[] publicKey)
        {
            lock (Lock)
            {
                if (!PublicKeysById.ContainsKey(nodeId))
                    PublicKeysById[nodeId] = publicKey;
            }
        }

        public static byte[]? GetPublicKey(string nodeId)
        {
            lock (Lock)
            {
                return PublicKeysById.TryGetValue(nodeId, out var key) ? key : null;
            }
        }

        // Verifies that `signatureHex` is a valid ECDSA signature over the
        // raw bytes of `hashHex`, produced by the private key matching
        // `publicKey`. Malformed hex in either field is treated as "does not
        // verify" rather than thrown, since both ultimately come from
        // untrusted, network-supplied block data.
        public static bool Verify(byte[] publicKey, string hashHex, string signatureHex)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                var hashBytes = System.Convert.FromHexString(hashHex);
                var signatureBytes = System.Convert.FromHexString(signatureHex);
                return ecdsa.VerifyHash(hashBytes, signatureBytes);
            }
            catch (System.FormatException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    // Persisted, hand-editable per-node configuration — see "Persistence &
    // resume" in README.md. Lives at nodes/<node-id>/metadata.json, alongside
    // that node's blockchain.db. NodeRole is serialized as its string name
    // (not the underlying int) specifically so it's easy to read and edit by
    // hand — see NodeMetadataStore's metadata JSON options.
    public class NodeMetadata
    {
        public string Id { get; set; } = "";
        public NodeRole NodeRole { get; set; } = NodeRole.Honest;
        public int HashPower { get; set; } = 1;
        // $ cost of a single mining attempt — see ScenarioNodeGroup.CostPerAttempt
        // and RuleSchedule.BestValueAt in Blockchain.cs. Persisted for the same
        // restart-safety reason every other group-authored field is. 0 (default)
        // means mining is free, same as omitting it entirely.
        public decimal CostPerAttempt { get; set; } = 0m;
        // $ fixed cost owed every turn regardless of outcome — see
        // ScenarioNodeGroup.CostOfLiving. Persisted for the same restart-safety
        // reason every other group-authored field is. 0 (default) means no
        // living cost, same as omitting it entirely.
        public decimal CostOfLiving { get; set; } = 0m;
        // $ runway on top of on-chain net worth before CostOfLiving can push
        // this node into insolvency — see ScenarioNodeGroup.StartingCapital.
        public decimal StartingCapital { get; set; } = 0m;
        // $ cost to buy +1 HashPower — see ScenarioNodeGroup.HashPowerCost.
        // Persisted for the same restart-safety reason every other
        // group-authored field is. 0 (default) means reinvestment is disabled.
        public decimal HashPowerCost { get; set; } = 0m;
        // Upper bound HashPowerCost-driven reinvestment won't grow HashPower
        // past — see ScenarioNodeGroup.MaxHashPower. 0 means uncapped.
        public int MaxHashPower { get; set; } = 0;
        // Whether this node ever gets a mining turn. A node with CanMine false
        // still does everything else a full node does — serves /chain, /tx,
        // /balances, receives and validates blocks and chains from peers, holds
        // a mempool — it just never builds a block itself, i.e. a wallet-only /
        // relay-only participant. See the "Mining participation" note in README.md.
        public bool CanMine { get; set; } = true;
        // Null/empty = mines solo (default). Otherwise, the name of a mining
        // pool this node subscribes to — its HashPower is combined with every
        // other current member's into the pool's single shared turn instead of
        // getting its own. Only honored for NodeRole.Honest nodes; a malicious
        // role's Pool value, if set, is ignored and it always mines solo. See
        // the "Mining pools" note in README.md.
        public string? Pool { get; set; } = null;
        // How heavily this node is weighted when other nodes are choosing
        // their outbound peers (see NodeNetwork.ChooseWeightedPeers) — 1 is
        // an ordinary node. A node with EconomicWeight 20 is 20x as likely
        // to be picked as an outbound peer by any given other node, so it
        // ends up with a disproportionate number of inbound connections and
        // becomes a structural hub in the peer graph — modeling the real
        // Bitcoin dynamic where well-run, publicly-reachable nodes (often
        // run by economically significant operators — exchanges, payment
        // processors) end up relaying for far more of the network than an
        // ordinary node does, without any special protocol role. See the
        // "Peer topology" note in README.md.
        public int EconomicWeight { get; set; } = 1;
        // This node's own timeline of which ConsensusRules is active at
        // which block height — see RuleSchedule's own comment in
        // Blockchain.cs. Sourced from whichever ScenarioNodeGroup created
        // this node (NodeGroups-authored nodes only — see
        // LoadOrCreateFromGroupAsync); organically-grown and default-start
        // nodes just get an empty schedule, which RuleSchedule.RulesForHeight
        // treats as ConsensusRules' own defaults (real Bitcoin's own
        // numbers) at every height. Persisted here so a restart's Blockchain
        // and SoloMiner build/validate against the exact same schedule they
        // always have, not whatever this run's scenario happens to say now.
        public List<RuleScheduleEntry> RuleSchedule { get; set; } = new();
        // A ValueSeeking node's own resolved candidate set — see ScenarioNodeGroup.ValueSeekingCandidates
        // and RuleSchedule's value-seeking constructor in Blockchain.cs. Mutually
        // exclusive with RuleSchedule above (NodeGroups-authored nodes get exactly
        // one populated, never both — see ScenarioLoader.ResolveNodeRules); empty
        // (the default) means this node is NOT value-seeking. Organically-grown
        // and default-start nodes always leave this empty — ValueSeeking is
        // NodeGroups-authored only in v1, no DefaultRuleSchedule-style
        // integration. Persisted here, resolved and name-free, for the same
        // restart-safety reason RuleSchedule is: a resumed node builds/validates
        // against the exact candidate set (Rules + PriceSchedule values) it
        // always has, not whatever this run's scenario file's NodeRules say now.
        public List<ValueSeekingCandidate> ValueSeekingCandidates { get; set; } = new();
        // Base64-encoded DER (ECDsa.ExportECPrivateKey) signing identity key
        // — see the "Signed blocks" note in README.md. Unlike every other
        // field here, this one should never be hand-edited or deleted once a
        // node has mined blocks: doing so orphans every historical block it
        // signed, since the key registered for its Id on the next run would
        // no longer be the one that actually signed that history.
        public string? SigningKey { get; set; } = null;
    }

    // ------------------------------------------------------------------
    // Loads, saves, and applies NodeMetadata to/from nodes/<node-id>/metadata.json
    // under a run's root directory (see Program.RunRootDir — passed in rather
    // than read directly, so this store doesn't depend on Program's state).
    // Also owns NodeDirFor: "where does this node's stuff live on disk" starts
    // with its metadata, and NodeNetwork.cs's own BlockchainDbPathFor builds
    // on this same helper so both files agree on one node directory layout.
    // ------------------------------------------------------------------
    public static class NodeMetadataStore
    {
        public static string NodeDirFor(string runRootDir, string nodeId) =>
            Path.Combine(runRootDir, "nodes", nodeId);

        private static string MetadataPathFor(string runRootDir, string nodeId) =>
            Path.Combine(NodeDirFor(runRootDir, nodeId), "metadata.json");

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
        public static string ExportSigningKey(ECDsa key) => Convert.ToBase64String(key.ExportECPrivateKey());

        public static ECDsa ImportSigningKey(string base64)
        {
            var key = ECDsa.Create();
            key.ImportECPrivateKey(Convert.FromBase64String(base64), out _);
            return key;
        }

        // Loads a node's persisted metadata.json if one already exists (from a
        // previous run, or hand-edited by a user to bump HashPower or change
        // NodeRole) so it survives a restart unchanged. Only a brand new node —
        // no metadata.json yet — gets fresh defaults, written out immediately so
        // they're there to edit or resume from next time. `defaultRole`,
        // `defaultCanMine`, and `defaultRuleSchedule` are the caller's
        // (NodeNetwork.AssignRole/AssignCanMine/PickDefaultRuleSchedule)
        // default-assignment policy for a brand new node — this store only
        // decides whether an existing file's contents win over those defaults,
        // never what the defaults themselves should be. SigningKey is handled
        // specially either way: a loaded metadata.json missing one (e.g. saved
        // before this field existed) gets a freshly generated key filled in and
        // re-saved immediately, exactly as if it were brand new — see the
        // "Signed blocks" note in README.md for why that key, once established,
        // must never change again.
        public static async Task<NodeMetadata> LoadOrCreateAsync(string runRootDir, string id, NodeRole defaultRole, bool defaultCanMine, List<RuleScheduleEntry> defaultRuleSchedule)
        {
            var path = MetadataPathFor(runRootDir, id);
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
                            await SaveAsync(runRootDir, loaded);
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
                NodeRole = defaultRole,
                HashPower = 1,
                CanMine = defaultCanMine,
                RuleSchedule = defaultRuleSchedule,
                SigningKey = ExportSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256))
            };
            await SaveAsync(runRootDir, metadata);
            return metadata;
        }

        public static async Task SaveAsync(string runRootDir, NodeMetadata metadata)
        {
            var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
            await File.WriteAllTextAsync(MetadataPathFor(runRootDir, metadata.Id), json);
        }

        // Writes (or updates) nodes/<id>/metadata.json for a single node a
        // ScenarioNodeGroup describes, called once per node as
        // NodeNetwork.AddNodeAsync creates it (whether that's phase 0's
        // initial population or a later phase's mid-run NodeGroups) — this
        // is what makes the scenario authoritative for behavior (NodeRole,
        // HashPower, CanMine, Pool) every time a group is applied, the same
        // way hand-editing metadata.json already is. If this id already has
        // metadata on disk (from a previous run, scenario or otherwise), its
        // existing SigningKey is preserved rather than regenerated — see
        // "Persistence & resume" and the "Signed blocks" note in README.md
        // for why — so re-running the same scenario keeps building on the
        // same node identity and chain history instead of resetting to
        // genesis every time. Only a genuinely new id gets a freshly
        // generated key. See "Scenarios" in README.md.
        public static async Task<NodeMetadata> LoadOrCreateFromGroupAsync(string runRootDir, string id, ScenarioNodeGroup group)
        {
            NodeMetadata? existing = null;
            var path = MetadataPathFor(runRootDir, id);
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
            metadata.EconomicWeight = group.EconomicWeight;
            metadata.RuleSchedule = group.ResolvedRuleSchedule;
            metadata.ValueSeekingCandidates = group.ResolvedValueSeekingCandidates;
            metadata.CostPerAttempt = group.CostPerAttempt;
            metadata.CostOfLiving = group.CostOfLiving;
            metadata.StartingCapital = group.StartingCapital;
            metadata.HashPowerCost = group.HashPowerCost;
            metadata.MaxHashPower = group.MaxHashPower;
            if (string.IsNullOrEmpty(metadata.SigningKey))
                metadata.SigningKey = ExportSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));

            await SaveAsync(runRootDir, metadata);
            return metadata;
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
        public static async Task PreloadKnownSigningKeysAsync(string runRootDir)
        {
            var nodesDir = Path.Combine(runRootDir, "nodes");
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
    }
}
