using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveChain
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
    // A "node" — an independent async worker with its own HTTP listener, its
    // own view of the chain, and its own mempool of pending transactions.
    // Node owns network-facing concerns only: serving HTTP requests,
    // validating and accepting what peers send it. Mining lives entirely
    // outside Node now, in SoloMiner/PoolMiner (see Miner.cs, PoolMiner.cs,
    // IMiner.cs) — Program.AddNodeAsync (the composition root) constructs a
    // Node's Chain and Mempool once and shares those same instances with that
    // node's SoloMiner, but Node itself holds no reference to it and knows
    // nothing about mining, roles, hash power, or pools; the round-robin
    // scheduler talks to IMiners directly, never through Node. Incoming
    // requests are handed to an ElasticTaskPool rather than getting an
    // unbounded Task.Run each.
    // ------------------------------------------------------------------

    public class Node
    {
        public string Id { get; }
        public int Port { get; }
        public Blockchain Chain { get; }
        public ConcurrentQueue<Transaction> Mempool { get; }

        private readonly HttpListener _listener;
        private readonly ElasticTaskPool _requestPool;
        private readonly ChainWatcher _watcher;
        private volatile bool _running = true;

        public Node(string id, int port, Blockchain chain, ConcurrentQueue<Transaction> mempool, ChainWatcher watcher)
        {
            Id = id;
            Port = port;
            Chain = chain;
            Mempool = mempool;
            _watcher = watcher;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _requestPool = new ElasticTaskPool($"{id}-pool", minWorkers: 2, maxWorkers: 16, scaleUpQueueThreshold: 4);
        }

        public void Start()
        {
            _listener.Start();
            Console.WriteLine($"[{Id}] listening on http://localhost:{Port}/");
            _ = Task.Run(ListenLoop);
        }

        public void Stop()
        {
            _running = false;
            _requestPool.Stop();
            try { _listener.Stop(); } catch { /* ignore on shutdown */ }
        }

        private async Task ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    if (!_running) return;
                    continue;
                }
                _requestPool.Enqueue(() => HandleRequestAsync(ctx));
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                string responseBody;
                res.ContentType = "application/json";

                switch (req.Url?.AbsolutePath)
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
}
