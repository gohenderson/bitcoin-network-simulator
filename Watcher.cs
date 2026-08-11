using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveChain
{
    // ------------------------------------------------------------------
    // Research watcher plumbing — tracks build/accept/reject/reorg events across
    // the whole simulated network and periodically audits every node's /chain
    // endpoint to report on convergence, not just what any single node believes.
    // ------------------------------------------------------------------

    public enum NetworkState
    {
        Healthy,
        Recovering,
        InvalidState
    }

    public sealed class WatcherEvent
    {
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public string Type { get; init; } = "";
        public string NodeId { get; init; } = "";
        public int? Height { get; init; }
        public string TipHash { get; init; } = "";
        public string Details { get; init; } = "";
    }

    public sealed class NodeAudit
    {
        public string NodeId { get; init; } = "";
        public int Height { get; init; }
        public string TipHash { get; init; } = "";
        public bool StructurallyValid { get; init; }
        public string Reason { get; init; } = "";
    }

    public sealed class WatcherSnapshot
    {
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public NetworkState State { get; init; }
        public bool ChainsConverged { get; init; }
        public bool AllChainsValid { get; init; }
        public bool NetworkIsMakingProgress { get; init; }
        public int BlocksObserved { get; init; }
        public int BlocksObservedSincePreviousAudit { get; init; }
        public bool InvalidStateWhileProducingBlocks { get; init; }
        public int MinHeight { get; init; }
        public int MaxHeight { get; init; }
        public string CommonTipHash { get; init; } = "";
        public List<NodeAudit> Nodes { get; init; } = new();
        public string Explanation { get; init; } = "";
    }

    public sealed class ChainWatcher
    {
        private List<int> _ports;
        private List<string> _nodeIds;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private readonly object _lock = new();
        private readonly List<WatcherEvent> _events = new();
        private WatcherSnapshot? _lastSnapshot;
        private int _blocksObserved;
        private int _reorganizationsObserved;
        private int _rejectedBlocksObserved;

        public ChainWatcher(List<int> ports, List<string> nodeIds)
        {
            _ports = new List<int>(ports);
            _nodeIds = new List<string>(nodeIds);
        }

        public void AddNode(int port, string nodeId)
        {
            lock (_lock)
            {
                _ports.Add(port);
                _nodeIds.Add(nodeId);
            }
        }

        public void ObserveBuild(string nodeId, Block block, NodeRole role)
        {
            lock (_lock)
            {
                _blocksObserved++;
                _events.Add(new WatcherEvent
                {
                    Type = "block-built",
                    NodeId = nodeId,
                    Height = block.Index,
                    TipHash = block.Hash,
                    Details = $"role={role}, builtBy={block.BuiltBy}, nonce={block.Nonce}, txs={block.Transactions.Count}"
                });
            }
        }

        public void ObserveAccepted(string nodeId, Block block)
        {
            lock (_lock)
            {
                _events.Add(new WatcherEvent
                {
                    Type = "block-accepted",
                    NodeId = nodeId,
                    Height = block.Index,
                    TipHash = block.Hash,
                    Details = $"builtBy={block.BuiltBy}"
                });
            }
        }

        public void ObserveRejected(string nodeId, Block block, string reason)
        {
            lock (_lock)
            {
                _rejectedBlocksObserved++;
                _events.Add(new WatcherEvent
                {
                    Type = "block-rejected",
                    NodeId = nodeId,
                    Height = block.Index,
                    TipHash = block.Hash,
                    Details = reason
                });
            }
        }

        public void ObserveReorganization(string nodeId, string reason)
        {
            lock (_lock)
            {
                _reorganizationsObserved++;
                _events.Add(new WatcherEvent
                {
                    Type = "reorganization",
                    NodeId = nodeId,
                    Details = reason
                });
            }
        }

        public async Task<WatcherSnapshot> AuditAsync(bool emitTransitions = true)
        {
            List<int> ports;
            List<string> nodeIds;
            lock (_lock) { ports = new List<int>(_ports); nodeIds = new List<string>(_nodeIds); }

            var audits = new List<NodeAudit>();

            for (int i = 0; i < ports.Count; i++)
            {
                var nodeId = nodeIds[i];
                try
                {
                    using var response = await _http.GetAsync($"http://localhost:{ports[i]}/chain");
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        audits.Add(new NodeAudit { NodeId = nodeId, StructurallyValid = false, Reason = $"/chain HTTP {(int)response.StatusCode}" });
                        continue;
                    }

                    var chain = JsonSerializer.Deserialize<List<Block>>(body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (chain == null)
                    {
                        audits.Add(new NodeAudit { NodeId = nodeId, StructurallyValid = false, Reason = "chain endpoint returned null" });
                        continue;
                    }

                    var validation = Blockchain.ValidateSnapshot(chain);
                    audits.Add(new NodeAudit
                    {
                        NodeId = nodeId,
                        Height = Math.Max(0, chain.Count - 1),
                        TipHash = chain.Count == 0 ? "" : chain[^1].Hash,
                        StructurallyValid = validation.Ok,
                        Reason = validation.Reason
                    });
                }
                catch (Exception ex)
                {
                    audits.Add(new NodeAudit { NodeId = nodeId, StructurallyValid = false, Reason = $"watcher could not inspect node: {ex.Message}" });
                }
            }

            var allValid = audits.Count == ports.Count && audits.All(a => a.StructurallyValid);
            var minHeight = audits.Count == 0 ? 0 : audits.Min(a => a.Height);
            var maxHeight = audits.Count == 0 ? 0 : audits.Max(a => a.Height);
            var distinctTips = audits.Where(a => a.StructurallyValid).Select(a => a.TipHash).Distinct().ToList();
            var converged = allValid && distinctTips.Count == 1;
            int observedBlocks;
            WatcherSnapshot? previousSnapshot;
            lock (_lock)
            {
                observedBlocks = _blocksObserved;
                previousSnapshot = _lastSnapshot;
            }
            var blocksSincePreviousAudit = previousSnapshot == null
                ? observedBlocks
                : Math.Max(0, observedBlocks - previousSnapshot.BlocksObserved);
            var progress = previousSnapshot != null && maxHeight > previousSnapshot.MaxHeight;

            NetworkState state;
            string explanation;
            if (!allValid)
            {
                state = NetworkState.InvalidState;
                explanation = "At least one node is invalid or unreachable. The network may still be producing blocks, so apparent progress is not evidence of correctness.";
            }
            else if (!converged)
            {
                state = NetworkState.Recovering;
                explanation = $"All observed chains are structurally valid but divergent: heights {minHeight}-{maxHeight}, {distinctTips.Count} valid tip(s).";
            }
            else
            {
                state = NetworkState.Healthy;
                explanation = "All nodes have structurally valid chains and the same tip; the network has converged.";
            }

            var snapshot = new WatcherSnapshot
            {
                Timestamp = DateTime.UtcNow,
                State = state,
                ChainsConverged = converged,
                AllChainsValid = allValid,
                NetworkIsMakingProgress = progress,
                BlocksObserved = observedBlocks,
                BlocksObservedSincePreviousAudit = blocksSincePreviousAudit,
                InvalidStateWhileProducingBlocks = state == NetworkState.InvalidState && blocksSincePreviousAudit > 0,
                MinHeight = minHeight,
                MaxHeight = maxHeight,
                CommonTipHash = converged ? audits[0].TipHash : "",
                Nodes = audits,
                Explanation = explanation
            };

            if (snapshot.InvalidStateWhileProducingBlocks)
            {
                Console.WriteLine($"[watcher] !!! INVALID STATE WHILE CHAIN CONTINUES BUILDING: {blocksSincePreviousAudit} block build(s) observed since last audit !!!");
            }

            if (emitTransitions)
            {
                WatcherSnapshot? previous;
                lock (_lock) previous = _lastSnapshot;
                if (previous == null || previous.State != snapshot.State)
                {
                    var label = snapshot.State switch
                    {
                        NetworkState.Healthy => "RECOVERED",
                        NetworkState.Recovering => "DIVERGENCE",
                        NetworkState.InvalidState => "INVALID-STATE",
                        _ => "STATE"
                    };
                    Console.WriteLine($"\n[watcher] *** {label} *** {snapshot.Explanation}");
                    lock (_lock)
                    {
                        _events.Add(new WatcherEvent
                        {
                            Type = $"network-{snapshot.State.ToString().ToLowerInvariant()}",
                            Details = snapshot.Explanation
                        });
                    }
                }
            }

            lock (_lock) _lastSnapshot = snapshot;
            return snapshot;
        }

        public object Report()
        {
            lock (_lock)
            {
                return new
                {
                    lastAudit = _lastSnapshot,
                    blocksObserved = _blocksObserved,
                    reorganizationsObserved = _reorganizationsObserved,
                    rejectedBlocksObserved = _rejectedBlocksObserved,
                    events = _events.ToList()
                };
            }
        }

        public async Task RunAsync(CancellationToken token, int intervalMs = 2000)
        {
            await AuditAsync();
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(intervalMs, token); }
                catch (OperationCanceledException) { break; }
                await AuditAsync();
            }
        }
    }
}
