using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    /// <summary>Node roles. <see cref="Honest"/> is the baseline; the others each deliberately violate exactly one trust assumption.</summary>
    public enum NodeRole
    {
        Honest,
        Equivocator,
        Impersonator,
        Corruptor,
        Withholder
    }

    /// <summary>Body of a <c>POST /&lt;node-id&gt;/spendOnLineage</c> request — see <see cref="Node.HandleAsync"/>.</summary>
    public class SpendOnLineageRequest
    {
        public string Lineage { get; set; } = "";
        public string To { get; set; } = "";
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// An independent view of the chain and its own mempool of pending transactions. Owns
    /// everything about what a request does (validating and accepting what peers send it),
    /// not how it physically arrives. Mining lives entirely outside <see cref="Node"/>, in
    /// <see cref="SoloMiner"/>/<see cref="PoolMiner"/>/<see cref="IMiner"/>. A block or chain
    /// accepted from one peer is relayed on to this node's other peers so it keeps
    /// propagating hop by hop across a peer graph where no single node is connected to
    /// everyone.
    /// </summary>
    public class Node
    {
        public const string SenderIdHeaderName = "X-Sender-Id";

        public string Id { get; }
        public Blockchain Chain { get; }
        public ConcurrentQueue<Transaction> Mempool { get; }

        private readonly ChainWatcher _watcher;
        private readonly NodeNetwork.InternalDispatchFunc _dispatch;
        private readonly Func<List<string>> _getPeerIds;
        private readonly Action<string> _discouragePeer;
        private readonly ConcurrentDictionary<string, byte> _seenTransactionIds = new();

        /// <summary>
        /// This node's own peer ids per lineage other than whichever one it's currently on —
        /// completely separate from <see cref="_getPeerIds"/>'s single live gossip graph
        /// (full block/chain/tx relay, one lineage). Seeded the moment this node's own active
        /// lineage switches away from one (<see cref="SeedForeignLineagePeers"/>) and kept
        /// fresh afterward by <see cref="RefreshForeignLineagePeersAsync"/> — a minimal,
        /// peer-id-only exchange that never validates or joins that lineage.
        /// </summary>
        private readonly ConcurrentDictionary<string, HashSet<string>> _foreignLineagePeers = new();

        public Node(string id, Blockchain chain, ConcurrentQueue<Transaction> mempool, ChainWatcher watcher, NodeNetwork.InternalDispatchFunc dispatch, Func<List<string>> getPeerIds, Action<string> discouragePeer)
        {
            Id = id;
            Chain = chain;
            Mempool = mempool;
            _watcher = watcher;
            _dispatch = dispatch;
            _getPeerIds = getPeerIds;
            _discouragePeer = discouragePeer;
        }

        /// <summary>
        /// <paramref name="route"/> is the request path with this node's id segment already
        /// stripped off by <see cref="NetworkServer"/> — e.g. "/chain" for a request to
        /// /000-alpha/chain. A thin adapter over <see cref="HandleAsync"/>: pulls method,
        /// sender id, and body out of the real HTTP request, then writes the result back to
        /// the real HTTP response — the only place this class still touches
        /// <see cref="HttpListenerContext"/>.
        /// </summary>
        public async Task HandleRequestAsync(HttpListenerContext ctx, string route)
        {
            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                res.ContentType = "application/json";

                string? body = null;
                if (req.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(req.InputStream);
                    body = await reader.ReadToEndAsync();
                }

                var (statusCode, responseBody) = await HandleAsync(req.HttpMethod, route, req.Headers[SenderIdHeaderName], body);
                res.StatusCode = statusCode;

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

        /// <summary>
        /// The transport-agnostic core of every route this node serves — reachable both from
        /// a real HTTP request (via <see cref="HandleRequestAsync"/>) and from another node's
        /// in-process call (via <see cref="NodeNetwork.DispatchInternalAsync"/>), with
        /// identical behavior either way. <paramref name="senderId"/> and
        /// <paramref name="body"/> are plain strings rather than request headers/objects, so
        /// <c>/receiveBlock</c> and <c>/receiveChain</c> always independently deserialize and
        /// validate their input from scratch, never trust a shared object reference.
        /// </summary>
        public async Task<(int StatusCode, string Body)> HandleAsync(string method, string route, string? senderId, string? body)
        {
            int statusCode = 200;
            string responseBody;

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
                    responseBody = JsonSerializer.Serialize(Ledger.ToNestedDictionary(Chain.SnapshotBalancesByAsset()));
                    break;

                case "/tx" when method == "POST":
                    {
                        var tx = JsonSerializer.Deserialize<Transaction>(body ?? "",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (tx == null || string.IsNullOrWhiteSpace(tx.From) || string.IsNullOrWhiteSpace(tx.To) || tx.Amount <= 0 || string.IsNullOrWhiteSpace(tx.Asset))
                        {
                            statusCode = 400;
                            responseBody = "{\"status\":\"bad transaction\"}";
                        }
                        else
                        {
                            var (admitted, _, reason) = TryAdmitTransaction(tx);
                            if (admitted)
                            {
                                responseBody = "{\"status\":\"accepted\"}";
                            }
                            else
                            {
                                statusCode = 400;
                                responseBody = JsonSerializer.Serialize(new { status = "rejected", reason });
                            }
                        }
                        break;
                    }

                case "/receiveTx" when method == "POST":
                    {
                        if (!IsStillAPeer(senderId, out responseBody))
                        {
                            statusCode = 403;
                            break;
                        }

                        var tx = JsonSerializer.Deserialize<Transaction>(body ?? "",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (tx == null || string.IsNullOrWhiteSpace(tx.From) || string.IsNullOrWhiteSpace(tx.To) || tx.Amount <= 0 || string.IsNullOrWhiteSpace(tx.Asset))
                        {
                            statusCode = 400;
                            responseBody = "{\"status\":\"bad transaction\"}";
                            break;
                        }

                        var (admitted, alreadySeen, reason) = TryAdmitTransaction(tx);
                        responseBody = admitted
                            ? "{\"status\":\"accepted\"}"
                            : JsonSerializer.Serialize(new { status = alreadySeen ? "already seen" : "ignored", reason });
                        break;
                    }

                case var r when r.StartsWith("/peersFor/") && method == "GET":
                    {
                        var lineage = Uri.UnescapeDataString(route.Substring("/peersFor/".Length));
                        var myLineage = Chain.RuleNameForHeight(Chain.Latest.Index + 1) ?? Ledger.DefaultAssetName;
                        var ids = lineage == myLineage
                            ? _getPeerIds()
                            : _foreignLineagePeers.TryGetValue(lineage, out var known) ? known.ToList() : new List<string>();
                        responseBody = JsonSerializer.Serialize(new { Lineage = lineage, PeerIds = ids });
                        break;
                    }

                case "/spendOnLineage" when method == "POST":
                    {
                        var request = JsonSerializer.Deserialize<SpendOnLineageRequest>(body ?? "",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (request == null || string.IsNullOrWhiteSpace(request.Lineage) ||
                            string.IsNullOrWhiteSpace(request.To) || request.Amount <= 0)
                        {
                            statusCode = 400;
                            responseBody = "{\"status\":\"bad request\"}";
                            break;
                        }

                        var myCurrentLineage = Chain.RuleNameForHeight(Chain.Latest.Index + 1) ?? Ledger.DefaultAssetName;
                        var tx = new Transaction { From = Id, To = request.To, Amount = request.Amount, Asset = request.Lineage };

                        if (request.Lineage == myCurrentLineage)
                        {
                            var (admitted, _, reason) = TryAdmitTransaction(tx);
                            responseBody = admitted
                                ? "{\"status\":\"accepted\"}"
                                : JsonSerializer.Serialize(new { status = "rejected", reason });
                            if (!admitted) statusCode = 400;
                            break;
                        }

                        if (!_foreignLineagePeers.TryGetValue(request.Lineage, out var candidates) || candidates.Count == 0)
                        {
                            statusCode = 400;
                            responseBody = JsonSerializer.Serialize(new
                            {
                                status = "rejected",
                                reason = $"no known peer for lineage '{request.Lineage}' — this node has never switched away from it"
                            });
                            break;
                        }

                        var forwardTo = candidates.ElementAt(Random.Shared.Next(candidates.Count));
                        var (forwardStatus, forwardBody) = await _dispatch(forwardTo, "POST", "/tx", Id, JsonSerializer.Serialize(tx));
                        statusCode = forwardStatus;
                        responseBody = forwardBody;
                        break;
                    }

                case "/receiveBlock" when method == "POST":
                    {
                        if (!IsStillAPeer(senderId, out responseBody))
                        {
                            statusCode = 403;
                            break;
                        }

                        var block = JsonSerializer.Deserialize<Block>(body ?? "",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (block != null)
                        {
                            var (ok, reason, attributable) = Chain.TryAppend(block);
                            if (ok)
                            {
                                Console.WriteLine($"[{Id}] accepted block #{block.Index} built by {block.BuiltBy} (validated: parent + target + hash + coinbase + tx checks passed)");
                                _watcher.ObserveAccepted(Id, block);
                                PruneMempool(new[] { block });
                                responseBody = "{\"status\":\"appended\"}";
                                _ = RelayBlockAsync(block);
                            }
                            else
                            {
                                Console.WriteLine($"[{Id}] REJECTED block #{block.Index} from {block.BuiltBy}: {reason}");
                                _watcher.ObserveRejected(Id, block, reason);
                                if (attributable) DiscourageSender(senderId, reason);
                                statusCode = 409;
                                responseBody = JsonSerializer.Serialize(new { status = "rejected", reason });
                            }
                        }
                        else
                        {
                            statusCode = 400;
                            responseBody = "{\"status\":\"bad block\"}";
                        }
                        break;
                    }

                case "/receiveChain" when method == "POST":
                    {
                        if (!IsStillAPeer(senderId, out responseBody))
                        {
                            statusCode = 403;
                            break;
                        }

                        var candidate = JsonSerializer.Deserialize<List<Block>>(body ?? "",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (candidate != null)
                        {
                            var (replaced, reason, attributable) = Chain.TryReplaceWithLongerChain(candidate);

                            if (replaced)
                            {
                                Console.WriteLine($"[{Id}] REORGANIZED: {reason}");
                                _watcher.ObserveReorganization(Id, reason);
                                PruneMempool(candidate);
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
                            statusCode = 400;
                            responseBody = "{\"status\":\"bad chain\"}";
                        }

                        break;
                    }

                default:
                    statusCode = 404;
                    responseBody = "{\"error\":\"not found\"}";
                    break;
            }

            return (statusCode, responseBody);
        }

        /// <summary>This account's balance in the given asset, as of the next block this node would mine.</summary>
        private decimal NextAssetBalance(string account, string asset)
        {
            var nextHeight = Chain.Latest.Index + 1;
            return Chain.SnapshotBalancesByAsset(nextHeight).GetValueOrDefault((account, asset));
        }

        /// <summary>
        /// Shared core of <c>/tx</c> and <c>/receiveTx</c>: drops a transaction this node has
        /// already processed (by <see cref="Transaction.Id"/>), rejects one whose declared
        /// <see cref="Transaction.Asset"/> isn't this node's own current lineage or that this
        /// node's own current ledger can't afford, and otherwise enqueues it to
        /// <see cref="Mempool"/> and relays it on to peers — so a transaction gossips outward
        /// from wherever it enters the network, and only ever keeps propagating through (and
        /// eventually gets mined by) peers currently on the same lineage who can afford it.
        /// </summary>
        private (bool Admitted, bool AlreadySeen, string Reason) TryAdmitTransaction(Transaction tx)
        {
            if (!_seenTransactionIds.TryAdd(tx.Id, 0))
                return (false, true, "already seen");

            var myLineage = Chain.RuleNameForHeight(Chain.Latest.Index + 1) ?? Ledger.DefaultAssetName;
            if (tx.Asset != myLineage)
                return (false, false, $"wrong lineage: this node is currently on '{myLineage}', transaction declares '{tx.Asset}'");

            if (tx.From != Economics.CoinbaseSender)
            {
                var available = NextAssetBalance(tx.From, tx.Asset);
                if (tx.Amount > available)
                    return (false, false, $"insufficient balance: {tx.From} has {available} of '{tx.Asset}', tried to send {tx.Amount}");
            }

            Mempool.Enqueue(tx);
            _ = RelayTxAsync(tx);
            return (true, false, "ok");
        }

        /// <summary>
        /// Drops any of this node's own pending mempool transactions that a just-accepted
        /// block (or reorg) already included, so this node doesn't later try to mine — and
        /// get an honest block rejected over — a transaction that's already settled.
        /// </summary>
        private void PruneMempool(IEnumerable<Block> newlyAcceptedBlocks)
        {
            var minedIds = new HashSet<string>(newlyAcceptedBlocks.SelectMany(b => b.Transactions.Select(t => t.Id)));
            if (minedIds.Count == 0) return;

            var kept = new List<Transaction>();
            while (Mempool.TryDequeue(out var tx))
                if (!minedIds.Contains(tx.Id))
                    kept.Add(tx);

            foreach (var tx in kept) Mempool.Enqueue(tx);
        }

        /// <summary>
        /// Refuses a request from a peer this node has already discouraged (see
        /// <see cref="DiscourageSender"/>). A missing sender id is let through rather than
        /// refused, since there's no one to attribute it to either way.
        /// </summary>
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

        /// <summary>
        /// Called only for rejections flagged <c>AttributableToSender</c> — a genuine
        /// consensus-rule violation in data <paramref name="senderId"/> itself supplied, not
        /// just normal network timing. A missing sender id can't be discouraged.
        /// </summary>
        private void DiscourageSender(string? senderId, string reason)
        {
            if (string.IsNullOrEmpty(senderId)) return;
            Console.WriteLine($"[{Id}] discouraging peer {senderId}: {reason}");
            _watcher.ObserveDiscouraged(Id, senderId, reason);
            _discouragePeer(senderId);
        }

        /// <summary>
        /// Forwards a block this node just accepted from one peer on to its other peers.
        /// Best-effort and fire-and-forget: a peer that's unreachable or already has this
        /// block just gets skipped or rejects it, same as any other gossip.
        /// </summary>
        private async Task RelayBlockAsync(Block block)
        {
            var json = JsonSerializer.Serialize(block);
            foreach (var peerId in _getPeerIds())
            {
                if (peerId == Id) continue;
                try
                {
                    await _dispatch(peerId, "POST", "/receiveBlock", Id, json);
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
                    await _dispatch(peerId, "POST", "/receiveChain", Id, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] couldn't relay chain to peer {peerId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Forwards a transaction this node just admitted to its own mempool on to its other
        /// peers. Best-effort and fire-and-forget, same as block/chain relay — a peer that's
        /// unreachable, already has it, or can't currently afford it (e.g. it's on a
        /// different consensus-rules lineage) just drops it there.
        /// </summary>
        private async Task RelayTxAsync(Transaction tx)
        {
            var json = JsonSerializer.Serialize(tx);
            foreach (var peerId in _getPeerIds())
            {
                if (peerId == Id) continue;
                try
                {
                    await _dispatch(peerId, "POST", "/receiveTx", Id, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] couldn't relay tx to peer {peerId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called once, the moment this node's own active lineage switches away from
        /// <paramref name="oldLineage"/> (see <see cref="Blockchain.OnLineageSwitched"/>).
        /// Snapshots this node's current gossip peers into <see cref="_foreignLineagePeers"/>
        /// under the outgoing lineage — the one moment they're still known-good — since a
        /// lineage never previously observed gets no seed and can never be discovered later.
        /// </summary>
        public void SeedForeignLineagePeers(string oldLineage, string newLineage, int atHeight)
        {
            var mySnapshot = _getPeerIds();
            var set = _foreignLineagePeers.GetOrAdd(oldLineage, _ => new HashSet<string>());
            lock (set) { set.UnionWith(mySnapshot); }
            Console.WriteLine($"[{Id}] lineage switched '{oldLineage}' -> '{newLineage}' at height {atHeight} — seeded {mySnapshot.Count} peer(s) for '{oldLineage}'");
        }

        /// <summary>
        /// For every lineage this node tracks a foreign peer list for, asks one known peer for
        /// its own current peer-id list on that same lineage and unions the result in — a
        /// minimal, id-list-only exchange (via <c>/peersFor/&lt;lineage&gt;</c>) that never
        /// touches blocks, chains, or validation, so this node never actually joins or trusts
        /// that lineage's network.
        /// </summary>
        public async Task RefreshForeignLineagePeersAsync()
        {
            foreach (var (lineage, peers) in _foreignLineagePeers)
            {
                List<string> snapshot;
                lock (peers) { snapshot = peers.ToList(); }
                if (snapshot.Count == 0) continue;

                var askId = snapshot[Random.Shared.Next(snapshot.Count)];
                try
                {
                    var (statusCode, responseBody) = await _dispatch(askId, "GET", $"/peersFor/{Uri.EscapeDataString(lineage)}", Id, null);
                    if (statusCode < 200 || statusCode >= 300) continue;

                    var parsed = JsonSerializer.Deserialize<PeersForResponse>(responseBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed?.PeerIds == null || parsed.PeerIds.Count == 0) continue;

                    lock (peers) { peers.UnionWith(parsed.PeerIds.Where(p => p != Id)); }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{Id}] couldn't refresh '{lineage}' peers via {askId}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Response shape of <c>GET /&lt;node-id&gt;/peersFor/&lt;lineage&gt;</c> — see <see cref="Node.HandleAsync"/>.</summary>
    public class PeersForResponse
    {
        public string Lineage { get; set; } = "";
        public List<string> PeerIds { get; set; } = new();
    }

    /// <summary>
    /// A process-wide, append-only registry binding each node's Id to the public key it signs
    /// blocks with. This binding is what makes a block's BuiltBy claim verifiable: the
    /// signature must verify against this registry's independently-established record of
    /// which key belongs to which name.
    /// </summary>
    public static class NodeIdentityRegistry
    {
        private static readonly object Lock = new();
        private static readonly Dictionary<string, byte[]> PublicKeysById = new();

        /// <summary>A no-op if <paramref name="nodeId"/> is already registered — a node's key is fixed for its lifetime.</summary>
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

        /// <summary>
        /// Verifies that <paramref name="signatureHex"/> is a valid ECDSA signature over the
        /// raw bytes of <paramref name="hashHex"/>, produced by the private key matching
        /// <paramref name="publicKey"/>. Malformed hex in either field is treated as "does not
        /// verify" rather than thrown, since both ultimately come from untrusted,
        /// network-supplied block data.
        /// </summary>
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

    /// <summary>
    /// Persisted, hand-editable per-node configuration, living at
    /// <c>nodes/&lt;node-id&gt;/metadata.json</c> alongside that node's <c>blockchain.db</c>.
    /// </summary>
    public class NodeMetadata
    {
        public string Id { get; set; } = "";
        public NodeRole NodeRole { get; set; } = NodeRole.Honest;
        public int HashPower { get; set; } = 1;
        public decimal CostPerAttempt { get; set; } = 0m;
        public decimal CostOfLiving { get; set; } = 0m;
        public decimal StartingCapital { get; set; } = 0m;
        public decimal HashPowerCost { get; set; } = 0m;
        public int MaxHashPower { get; set; } = 0;
        /// <summary>
        /// Whether this node ever gets a mining turn. A node with <c>CanMine</c> false still
        /// does everything else a full node does — serves <c>/chain</c>, <c>/tx</c>,
        /// <c>/balances</c>, receives and validates blocks and chains from peers, holds a
        /// mempool — it just never builds a block itself.
        /// </summary>
        public bool CanMine { get; set; } = true;
        /// <summary>
        /// Null/empty means this node mines solo. Otherwise, the name of a mining pool this
        /// node subscribes to — only honored for <see cref="NodeRole.Honest"/> nodes; a
        /// malicious role always mines solo regardless of this value.
        /// </summary>
        public string? Pool { get; set; } = null;
        public List<string> PoolCandidates { get; set; } = new();
        public decimal PoolAdoptionThreshold { get; set; } = 0.5m;
        /// <summary>
        /// How heavily this node is weighted when other nodes are choosing their outbound
        /// peers. A node with <c>EconomicWeight</c> 20 is 20x as likely to be picked as an
        /// outbound peer by any given other node, so it ends up as a structural hub in the
        /// peer graph.
        /// </summary>
        public int EconomicWeight { get; set; } = 1;
        public List<RuleScheduleEntry> RuleSchedule { get; set; } = new();
        public List<ValueSeekingCandidate> ValueSeekingCandidates { get; set; } = new();
        /// <summary>
        /// Base64-encoded DER (<c>ECDsa.ExportECPrivateKey</c>) signing identity key. Unlike
        /// every other field here, this one should never be hand-edited or deleted once a node
        /// has mined blocks: doing so orphans every historical block it signed.
        /// </summary>
        public string? SigningKey { get; set; } = null;
    }

    /// <summary>
    /// Loads, saves, and applies <see cref="NodeMetadata"/> to/from
    /// <c>nodes/&lt;node-id&gt;/metadata.json</c> under a run's root directory.
    /// </summary>
    public static class NodeMetadataStore
    {
        public static string NodeDirFor(string runRootDir, string nodeId) =>
            Path.Combine(runRootDir, "nodes", nodeId);

        private static string MetadataPathFor(string runRootDir, string nodeId) =>
            Path.Combine(NodeDirFor(runRootDir, nodeId), "metadata.json");

        private static readonly JsonSerializerOptions MetadataJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string ExportSigningKey(ECDsa key) => Convert.ToBase64String(key.ExportECPrivateKey());

        public static ECDsa ImportSigningKey(string base64)
        {
            var key = ECDsa.Create();
            key.ImportECPrivateKey(Convert.FromBase64String(base64), out _);
            return key;
        }

        /// <summary>
        /// Loads a node's persisted <c>metadata.json</c> if one already exists so it survives
        /// a restart unchanged. Only a brand new node gets fresh defaults, written out
        /// immediately. A loaded file missing a <c>SigningKey</c> (e.g. saved before that
        /// field existed) gets a freshly generated key filled in and re-saved immediately,
        /// exactly as if it were brand new.
        /// </summary>
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

        /// <summary>
        /// Writes (or updates) <c>nodes/&lt;id&gt;/metadata.json</c> for a single node a
        /// <see cref="ScenarioNodeGroup"/> describes. If this id already has metadata on
        /// disk, its existing <c>SigningKey</c> is preserved rather than regenerated, so
        /// re-running the same scenario keeps building on the same node identity and chain
        /// history instead of resetting to genesis every time.
        /// </summary>
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
            metadata.PoolCandidates = group.PoolCandidates;
            metadata.PoolAdoptionThreshold = group.PoolAdoptionThreshold;
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

        /// <summary>
        /// Registers every already-persisted node's public key (from a previous run) before
        /// any node starts joining or resuming its chain this run. Without this preload,
        /// validating a saved chain would fail on any historical block built by a node that
        /// hasn't (re)joined yet this run, since that node's key wouldn't be registered until
        /// it does.
        /// </summary>
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
