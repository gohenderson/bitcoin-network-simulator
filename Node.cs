using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Node roles. Honest is the baseline. The others each violate exactly one of
    // the trust assumptions this system still makes, on purpose — see the
    // comment block at the top of the file for the full list of gaps.
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
    // route to that node's own HandleRequestAsync below. Node itself still
    // owns everything about what a request DOES (validating and accepting
    // what peers send it) — just not how it physically arrives anymore.
    // Mining lives entirely outside Node, in SoloMiner/PoolMiner/IMiner (see
    // Miner.cs) — Program.AddNodeAsync (the
    // composition root) constructs a Node's Chain and Mempool once and
    // shares those same instances with that node's SoloMiner, but Node
    // itself holds no reference to it and knows nothing about mining,
    // roles, hash power, or pools; the round-robin scheduler talks to
    // IMiners directly, never through Node.
    // ------------------------------------------------------------------

    public class Node
    {
        public string Id { get; }
        public Blockchain Chain { get; }
        public ConcurrentQueue<Transaction> Mempool { get; }

        private readonly ChainWatcher _watcher;

        public Node(string id, Blockchain chain, ConcurrentQueue<Transaction> mempool, ChainWatcher watcher)
        {
            Id = id;
            Chain = chain;
            Mempool = mempool;
            _watcher = watcher;
        }

        // `route` is the request path with this node's id segment already
        // stripped off by NetworkServer — e.g. "/chain" for a request to
        // /000-alpha/chain — so the switch below reads exactly as it did
        // back when each node had its own listener and AbsolutePath was
        // the whole story.
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
                            using var reader = new StreamReader(req.InputStream);
                            var body = await reader.ReadToEndAsync();
                            var block = JsonSerializer.Deserialize<Block>(body,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (block != null)
                            {
                                var (ok, reason) = Chain.TryAppend(block);
                                if (ok)
                                {
                                    Console.WriteLine($"[{Id}] accepted block #{block.Index} built by {block.BuiltBy} (validated: parent + target + hash + coinbase + tx checks passed)");
                                    _watcher.ObserveAccepted(Id, block);
                                    responseBody = "{\"status\":\"appended\"}";
                                }
                                else
                                {
                                    Console.WriteLine($"[{Id}] REJECTED block #{block.Index} from {block.BuiltBy}: {reason}");
                                    _watcher.ObserveRejected(Id, block, reason);
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
                            using var reader = new StreamReader(req.InputStream);
                            var body = await reader.ReadToEndAsync();
                            var candidate = JsonSerializer.Deserialize<List<Block>>(body,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                            if (candidate != null)
                            {
                                var (replaced, reason) = Chain.TryReplaceWithLongerChain(candidate);

                                if (replaced)
                                {
                                    Console.WriteLine($"[{Id}] REORGANIZED: {reason}");
                                    _watcher.ObserveReorganization(Id, reason);
                                    responseBody = JsonSerializer.Serialize(new { status = "reorganized", reason });
                                }
                                else
                                {
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
    }

    // ------------------------------------------------------------------
    // A process-wide, append-only registry binding each node's Id to the
    // public key it signs blocks with — see BUILTBY SIGNING at the top of
    // the file. This binding is what makes a block's BuiltBy claim
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
}
