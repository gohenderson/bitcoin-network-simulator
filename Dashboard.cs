using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    /// <summary>
    /// The web dashboard: a self-contained HTML/JS page (served at <c>/dashboard/</c>) that
    /// polls a JSON summary endpoint (<c>/dashboard/summary</c>) for participant counts, top
    /// miners by hash power/blocks won, peer-graph influence, and pool composition. Reads
    /// network-wide state rather than any single node's.
    /// </summary>
    public static class Dashboard
    {
        public static async Task HandleAsync(HttpListenerContext ctx, string route, NodeNetwork network, WatcherStore watcherStore, ChainWatcher watcher, ScenarioRuntimeInfo? scenarioRuntime)
        {
            var res = ctx.Response;
            try
            {
                switch (route)
                {
                    case "/":
                    case "/index.html":
                        await WriteAsync(res, 200, "text/html", Encoding.UTF8.GetBytes(HtmlPage));
                        break;

                    case "/summary":
                        var json = BuildSummaryJson(network, watcherStore, watcher, scenarioRuntime);
                        await WriteAsync(res, 200, "application/json", Encoding.UTF8.GetBytes(json));
                        break;

                    case "/chaingraph":
                        var graphJson = BuildChainGraphJson(watcher, network);
                        await WriteAsync(res, 200, "application/json", Encoding.UTF8.GetBytes(graphJson));
                        break;

                    default:
                        await WriteAsync(res, 404, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"not found\"}"));
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dashboard] request error: {ex.Message}");
            }
        }

        private static async Task WriteAsync(HttpListenerResponse res, int statusCode, string contentType, byte[] body)
        {
            res.StatusCode = statusCode;
            res.ContentType = contentType;
            res.ContentLength64 = body.Length;
            await res.OutputStream.WriteAsync(body, 0, body.Length);
            res.OutputStream.Close();
        }

        /// <summary>
        /// Combines the network's live participation/influence snapshot with historical win
        /// counts and the most recent convergence audit into the one JSON payload the page's
        /// JS renders.
        /// </summary>
        private static string BuildSummaryJson(NodeNetwork network, WatcherStore watcherStore, ChainWatcher watcher, ScenarioRuntimeInfo? scenarioRuntime)
        {
            var snapshot = network.GetSnapshot();
            var winCounts = watcherStore.GetWinCountsByNode();
            var lastAudit = watcher.LastSnapshot;
            var (hashPowerByNodeId, totalHashPower, validatedNodeCount) = ComputeInfluenceContext(snapshot, lastAudit);
            var (balances, currentAsset, totalSent, assetPrices) = ComputeLedgerSummary(network, lastAudit);

            var dashboardNodes = snapshot.Nodes.Select(n => new DashboardNode
            {
                Id = n.Id,
                Role = n.Role.ToString(),
                CanMine = n.CanMine,
                HashPower = n.HashPower,
                HashPowerShare = (double)n.HashPower / totalHashPower,
                Pool = n.Pool,
                EconomicWeight = n.EconomicWeight,
                PeerCount = n.PeerCount,
                BlocksWon = winCounts.GetValueOrDefault(n.Id, 0),
                Balances = balances.GetValueOrDefault(n.Id) ?? new Dictionary<string, decimal>(),
                TotalSent = totalSent.GetValueOrDefault(n.Id) ?? new Dictionary<string, decimal>()
            }).ToList();

            var summary = new DashboardSummary
            {
                GeneratedAt = DateTime.UtcNow.ToString("O"),
                NetworkState = lastAudit?.State.ToString() ?? "Unknown",
                ChainsConverged = lastAudit?.ChainsConverged ?? false,
                ChainHeight = lastAudit?.MaxHeight ?? 0,
                BlocksObserved = lastAudit?.BlocksObserved ?? 0,
                ParticipantCount = dashboardNodes.Count,
                MiningNodeCount = dashboardNodes.Count(n => n.CanMine),
                WalletOnlyCount = dashboardNodes.Count(n => !n.CanMine),
                PoolCount = snapshot.Pools.Count,
                TotalHashPower = snapshot.Nodes.Sum(n => n.HashPower),
                Pools = snapshot.Pools.Select(p => new DashboardPool
                {
                    Name = p.Name,
                    MemberCount = p.MemberCount,
                    TotalHashPower = p.TotalHashPower,
                    HashPowerShare = (double)p.TotalHashPower / totalHashPower
                }).OrderByDescending(p => p.TotalHashPower).ToList(),
                TopMinersByHashPower = dashboardNodes.Where(n => n.HashPower > 0).OrderByDescending(n => n.HashPower).Take(10).ToList(),
                TopMinersByBlocksWon = dashboardNodes.Where(n => n.BlocksWon > 0).OrderByDescending(n => n.BlocksWon).Take(10).ToList(),
                TopByInfluence = dashboardNodes.OrderByDescending(n => n.PeerCount).Take(10).ToList(),
                AllNodes = dashboardNodes.OrderByDescending(n => n.HashPower).ToList(),
                ReorganizationsObserved = lastAudit?.ReorganizationsObserved ?? 0,
                Forks = (lastAudit?.Tips ?? new List<TipGroup>()).Select(t =>
                {
                    var hashPower = t.NodeIds.Sum(id => hashPowerByNodeId.GetValueOrDefault(id, 0));
                    return new DashboardFork
                    {
                        TipHash = t.TipHash,
                        Height = t.Height,
                        NodeCount = t.NodeIds.Count,
                        Share = (double)t.NodeIds.Count / validatedNodeCount,
                        HashPower = hashPower,
                        HashPowerShare = (double)hashPower / totalHashPower,
                        NodeIds = t.NodeIds
                    };
                }).ToList(),
                RecentReorganizations = watcherStore.GetRecentReorganizations(10).Select(r => new DashboardReorg
                {
                    Timestamp = r.Timestamp,
                    NodeId = r.NodeId,
                    Reason = r.Reason
                }).ToList(),
                CurrentAsset = currentAsset,
                AssetPrices = assetPrices,
                Scenario = BuildScenarioSummary(scenarioRuntime)
            };

            return JsonSerializer.Serialize(summary, JsonOptions);
        }

        /// <summary>
        /// Current balance and total-ever-sent (excluding coinbase, since minting to yourself
        /// isn't spending), both per coin/asset; the asset currently active on that same view
        /// (so the UI can tell a node's live balance apart from a dormant one); and each
        /// distinct asset's current $-reference price (<see cref="RuleSchedule.PriceForNameAt"/>,
        /// 0 for a scenario with no value-seeking price schedule) — all read from whichever
        /// node is on the currently most-agreed-upon tip, the same "arbitrary but consistent"
        /// stand-in the rest of the dashboard already uses for a single canonical view of the
        /// network. Falls back to any live node if no audit has run yet.
        /// </summary>
        private static (Dictionary<string, Dictionary<string, decimal>> Balances, string CurrentAsset, Dictionary<string, Dictionary<string, decimal>> TotalSent, Dictionary<string, decimal> AssetPrices) ComputeLedgerSummary(NodeNetwork network, WatcherSnapshot? lastAudit)
        {
            var representativeNodeId = lastAudit?.Tips.FirstOrDefault()?.NodeIds.FirstOrDefault()
                ?? network.GetAllNodeIds().FirstOrDefault();
            var node = representativeNodeId != null ? network.ResolveNode(representativeNodeId) : null;
            if (node == null) return (new Dictionary<string, Dictionary<string, decimal>>(), "", new Dictionary<string, Dictionary<string, decimal>>(), new Dictionary<string, decimal>());

            var balances = Ledger.ToNestedDictionary(node.Chain.SnapshotBalancesByAsset());
            var nextHeight = node.Chain.Latest.Index + 1;
            var currentAsset = node.Chain.RuleNameForHeight(nextHeight) ?? Ledger.DefaultAssetName;

            var totalSent = new Dictionary<string, Dictionary<string, decimal>>();
            foreach (var block in node.Chain.Snapshot())
                foreach (var tx in block.Transactions)
                    if (tx.From != Economics.CoinbaseSender)
                    {
                        if (!totalSent.TryGetValue(tx.From, out var byAsset))
                            totalSent[tx.From] = byAsset = new Dictionary<string, decimal>();
                        byAsset[tx.Asset] = byAsset.GetValueOrDefault(tx.Asset) + tx.Amount;
                    }

            var assetNames = balances.Values.SelectMany(b => b.Keys)
                .Concat(totalSent.Values.SelectMany(b => b.Keys))
                .Append(currentAsset)
                .Distinct();
            var assetPrices = assetNames.ToDictionary(a => a, a => node.Chain.PriceForNameAt(a, nextHeight));

            return (balances, currentAsset, totalSent, assetPrices);
        }

        /// <summary>
        /// Per-node hash power and the network-wide totals it's shared against, used to turn a
        /// group of node ids (a fork's tip, say) into a hash-power share and a node-count
        /// share — the same two proportions the Forks panel and the chain graph's tip labels
        /// both report.
        /// </summary>
        private static (Dictionary<string, int> HashPowerByNodeId, int TotalHashPower, int ValidatedNodeCount) ComputeInfluenceContext(NetworkSnapshot snapshot, WatcherSnapshot? lastAudit)
        {
            var hashPowerByNodeId = snapshot.Nodes.ToDictionary(n => n.Id, n => n.HashPower);
            var totalHashPower = Math.Max(1, snapshot.Nodes.Sum(n => n.HashPower));
            var validatedNodeCount = Math.Max(1, lastAudit?.Tips.Sum(t => t.NodeIds.Count) ?? 0);
            return (hashPowerByNodeId, totalHashPower, validatedNodeCount);
        }

        /// <summary>Summarizes the scenario file (if any) driving this run and its phase timeline, for the dashboard's "Scenario" panel.</summary>
        private static DashboardScenario? BuildScenarioSummary(ScenarioRuntimeInfo? scenarioRuntime)
        {
            if (scenarioRuntime == null) return null;

            var (currentPhaseIndex, startedAtUtc) = scenarioRuntime.CurrentPhase();

            return new DashboardScenario
            {
                FileName = scenarioRuntime.ScenarioPath != null ? Path.GetFileName(scenarioRuntime.ScenarioPath) : null,
                Description = scenarioRuntime.Description,
                TotalPhases = scenarioRuntime.Phases.Count,
                CurrentPhaseIndex = currentPhaseIndex,
                CurrentPhaseElapsedSeconds = Math.Max(0, (int)(DateTime.UtcNow - startedAtUtc).TotalSeconds),
                Phases = scenarioRuntime.Phases.Select((phase, index) => new DashboardScenarioPhase
                {
                    Index = index,
                    IsCurrent = index == currentPhaseIndex,
                    Description = phase.Description,
                    DurationSeconds = phase.DurationSeconds,
                    NodeGroups = phase.NodeGroups.Select(g => new DashboardScenarioNodeGroup
                    {
                        Count = g.Count,
                        Role = g.Role.ToString(),
                        HashPower = g.HashPower,
                        CanMine = g.CanMine,
                        Pool = g.Pool,
                        ValueSeeking = g.ValueSeeking,
                        RulesName = g.RulesName
                    }).ToList()
                }).ToList()
            };
        }

        /// <summary>
        /// Builds the dashboard's already-collapsed chain graph: prunes fork branches that
        /// never grew past their first block, splits what remains into segments, and assigns
        /// each segment a column so the page only ever has to draw a handful of dots and lines
        /// per fork — never the raw, ever-growing block list.
        /// </summary>
        private static string BuildChainGraphJson(ChainWatcher watcher, NodeNetwork network)
        {
            var lastAudit = watcher.LastSnapshot;
            var rawBlocks = lastAudit?.ChainGraph ?? new List<ChainGraphBlock>();
            var tips = lastAudit?.Tips ?? new List<TipGroup>();
            var tipsByHash = tips.ToDictionary(t => t.TipHash);
            var tipHashSet = new HashSet<string>(tipsByHash.Keys);
            var (hashPowerByNodeId, totalHashPower, validatedNodeCount) = ComputeInfluenceContext(network.GetSnapshot(), lastAudit);

            var pruned = PruneUngrownForkStubs(rawBlocks, tipHashSet);
            var lanes = AssignLanes(pruned, tipHashSet);
            var segments = BuildChainSegments(pruned);
            AssignSegmentColumns(segments);
            AlignLeafTips(segments);

            var maxSeenOnNodes = pruned.Count == 0 ? 1 : Math.Max(1, pruned.Max(b => b.NodeIds.Count));

            DashboardChainSegmentBlock ToBlockDto(ChainGraphBlock b)
            {
                double? nodeShare = null, hashPowerShare = null;
                if (tipsByHash.TryGetValue(b.Hash, out var tip))
                {
                    var hashPower = tip.NodeIds.Sum(id => hashPowerByNodeId.GetValueOrDefault(id, 0));
                    nodeShare = (double)tip.NodeIds.Count / validatedNodeCount;
                    hashPowerShare = (double)hashPower / totalHashPower;
                }

                return new DashboardChainSegmentBlock
                {
                    Hash = b.Hash,
                    Height = b.Height,
                    BuiltBy = b.BuiltBy,
                    NodeIds = b.NodeIds,
                    IsTip = tipsByHash.ContainsKey(b.Hash),
                    IsShared = b.NodeIds.Count >= maxSeenOnNodes,
                    NodeShare = nodeShare,
                    HashPowerShare = hashPowerShare,
                    RuleName = network.ResolveNode(b.BuiltBy)?.Chain.RuleNameForHeight(b.Height)
                };
            }

            var graph = new DashboardChainGraph
            {
                TotalColumns = segments.Count == 0 ? 0 : segments.Max(s => s.EndCol) + 1,
                LaneCount = lanes.LaneCount,
                Segments = segments.Select(seg =>
                {
                    var hiddenStart = 1 + seg.ExtraShownPrefixCount;
                    var visible = seg.Collapsed
                        ? new List<ChainGraphBlock> { seg.Blocks[0] }
                            .Concat(seg.Blocks.Skip(1).Take(seg.ExtraShownPrefixCount))
                            .Append(seg.Blocks[^1])
                            .ToList()
                        : seg.Blocks;
                    return new DashboardChainSegment
                    {
                        Id = seg.Id,
                        ParentId = seg.Parent?.Id,
                        Lane = lanes.LaneOf[seg.Blocks[0].Hash],
                        StartCol = seg.StartCol,
                        EndCol = seg.EndCol,
                        Collapsed = seg.Collapsed,
                        Blocks = visible.Select(ToBlockDto).ToList(),
                        HiddenCount = seg.Collapsed ? seg.Blocks.Count - 1 - hiddenStart : 0,
                        HiddenFromHeight = seg.Collapsed ? seg.Blocks[hiddenStart].Height : (int?)null,
                        HiddenToHeight = seg.Collapsed ? seg.Blocks[^2].Height : (int?)null
                    };
                }).ToList()
            };

            return JsonSerializer.Serialize(graph, JsonOptions);
        }

        private sealed class LaneAssignment
        {
            public Dictionary<string, int> LaneOf { get; init; } = new();
            public int LaneCount { get; init; }
        }

        /// <summary>
        /// Greedily continues a lane when a block's parent is the current tip of that lane.
        /// A lane whose block turns out to have no children anywhere in the set, and isn't
        /// itself a currently-live tip, is freed once every block at that same height has had
        /// a chance to look for its own parent's tip, so a later fork reuses the lowest free
        /// lane instead of always growing outward — otherwise its connecting line would have
        /// to visually jump past an already-dead lane's row to reach a new one further out.
        /// Freeing must wait for the whole height group: two siblings forking from the very
        /// same parent only differ by which of them still finds that parent's tip in place,
        /// and freeing mid-group would erase it before the second sibling gets its turn,
        /// corrupting both onto the same lane. A currently-live tip is never freed even though
        /// it has no children yet — it may still grow a child on the very next block, and
        /// reusing its lane for an unrelated branch in the meantime would make two
        /// simultaneously-live branches render in the same lane color.
        /// </summary>
        private static LaneAssignment AssignLanes(List<ChainGraphBlock> blocks, HashSet<string> tipHashes)
        {
            var childCount = new Dictionary<string, int>();
            foreach (var b in blocks)
                childCount[b.PreviousHash] = childCount.GetValueOrDefault(b.PreviousHash) + 1;

            var laneTip = new List<string?>();
            var laneOf = new Dictionary<string, int>();
            foreach (var heightGroup in blocks.GroupBy(b => b.Height).OrderBy(g => g.Key))
            {
                var placed = new List<(ChainGraphBlock Block, int Lane)>();
                foreach (var b in heightGroup)
                {
                    var lane = laneTip.FindIndex(tip => tip == b.PreviousHash);
                    if (lane == -1)
                        lane = laneTip.FindIndex(tip => tip == null);
                    if (lane == -1)
                    {
                        lane = laneTip.Count;
                        laneTip.Add(b.Hash);
                    }
                    else
                    {
                        laneTip[lane] = b.Hash;
                    }
                    laneOf[b.Hash] = lane;
                    placed.Add((b, lane));
                }

                foreach (var (b, lane) in placed)
                {
                    if (!childCount.ContainsKey(b.Hash) && !tipHashes.Contains(b.Hash))
                        laneTip[lane] = null;
                }
            }
            return new LaneAssignment { LaneOf = laneOf, LaneCount = laneTip.Count };
        }

        /// <summary>
        /// A fork branch that never grew past its very first block is almost always just two
        /// honest nodes racing to mine at nearly the same instant, self-resolving within a
        /// block or two — not a fork worth breaking a collapsible run over. Drops any lane
        /// that (a) is still only one block long AND (b) has already been left behind by a
        /// taller lane, i.e. it is no longer the current frontier. A lane that's still tied
        /// for the tallest is kept regardless of length, since it may simply not have had a
        /// chance to grow yet.
        /// </summary>
        private static List<ChainGraphBlock> PruneUngrownForkStubs(List<ChainGraphBlock> blocks, HashSet<string> tipHashes)
        {
            if (blocks.Count == 0) return blocks;

            var lanes = AssignLanes(blocks, tipHashes);
            var byLane = new Dictionary<int, List<ChainGraphBlock>>();
            foreach (var b in blocks)
            {
                var lane = lanes.LaneOf[b.Hash];
                if (!byLane.TryGetValue(lane, out var list))
                    byLane[lane] = list = new List<ChainGraphBlock>();
                list.Add(b);
            }

            var globalMaxHeight = blocks.Max(b => b.Height);
            var keep = new HashSet<string>();
            foreach (var laneBlocks in byLane.Values)
            {
                var grown = laneBlocks.Count >= 2 || laneBlocks.Any(b => b.Height == globalMaxHeight);
                if (grown)
                    foreach (var b in laneBlocks) keep.Add(b.Hash);
            }

            return blocks.Where(b => keep.Contains(b.Hash)).ToList();
        }

        private sealed class ChainSegment
        {
            public int Id { get; init; }
            public List<ChainGraphBlock> Blocks { get; init; } = new();
            public ChainSegment? Parent { get; init; }
            public List<ChainSegment> Children { get; } = new();
            public bool Collapsed { get; set; }
            public int Span { get; set; }
            public int StartCol { get; set; }
            public int EndCol { get; set; }
            /// <summary>
            /// How many blocks right after the first one are shown individually instead of
            /// folded into the hidden-count gap — see <see cref="AlignLeafTips"/>. Zero for
            /// every segment except a tip segment stretched to reach the tree's common tip
            /// column.
            /// </summary>
            public int ExtraShownPrefixCount { get; set; }
        }

        /// <summary>
        /// Splits the (pruned) block set into segments: maximal simple chains with no
        /// branching in either direction. A segment starts right after a fork (or at the
        /// window's root) and ends at the next fork, dead end, or tip. Every block with more
        /// than one child is necessarily the last block of its own segment and the first
        /// block of each child segment, so fork points are always segment boundaries and
        /// therefore always individually visible.
        /// </summary>
        private static List<ChainSegment> BuildChainSegments(List<ChainGraphBlock> blocks)
        {
            var byHash = blocks.ToDictionary(b => b.Hash);
            var childBlocks = new Dictionary<string, List<ChainGraphBlock>>();
            foreach (var b in blocks)
            {
                if (byHash.ContainsKey(b.PreviousHash))
                {
                    if (!childBlocks.TryGetValue(b.PreviousHash, out var list))
                        childBlocks[b.PreviousHash] = list = new List<ChainGraphBlock>();
                    list.Add(b);
                }
            }

            var roots = blocks.Where(b => !byHash.ContainsKey(b.PreviousHash)).OrderBy(b => b.Height).ToList();

            var segments = new List<ChainSegment>();
            var visited = new HashSet<string>();
            var queue = new Queue<(ChainGraphBlock Block, ChainSegment? ParentSeg)>();
            foreach (var r in roots) queue.Enqueue((r, null));

            while (queue.Count > 0)
            {
                var (block, parentSeg) = queue.Dequeue();
                if (visited.Contains(block.Hash)) continue;

                var chain = new List<ChainGraphBlock> { block };
                visited.Add(block.Hash);
                var current = block;
                while (childBlocks.TryGetValue(current.Hash, out var kids) && kids.Count == 1)
                {
                    current = kids[0];
                    chain.Add(current);
                    visited.Add(current.Hash);
                }

                var seg = new ChainSegment { Id = segments.Count, Blocks = chain, Parent = parentSeg };
                parentSeg?.Children.Add(seg);
                segments.Add(seg);

                if (childBlocks.TryGetValue(current.Hash, out var endKids) && endKids.Count > 1)
                    foreach (var k in endKids) queue.Enqueue((k, seg));
            }

            return segments;
        }

        private const int ChainGraphMinHidden = 3;

        /// <summary>
        /// A segment of 5+ blocks collapses to its first and last block plus one hidden-count
        /// standing in for everything strictly between them, so a long straight run — forked
        /// or not — takes up three columns instead of one per block. Each child segment
        /// starts immediately after its parent's last column, so the whole tree only ever
        /// takes as many columns as its longest root-to-tip path has segments, regardless of
        /// how many real blocks that spans.
        /// </summary>
        private static void AssignSegmentColumns(List<ChainSegment> segments)
        {
            foreach (var seg in segments)
            {
                seg.Collapsed = seg.Blocks.Count >= ChainGraphMinHidden + 2;
                seg.Span = seg.Collapsed ? 3 : seg.Blocks.Count;
            }

            var roots = segments.Where(s => s.Parent == null).ToList();
            foreach (var s in roots) s.StartCol = 0;

            var queue = new Queue<ChainSegment>(roots);
            while (queue.Count > 0)
            {
                var seg = queue.Dequeue();
                seg.EndCol = seg.StartCol + seg.Span - 1;
                foreach (var child in seg.Children)
                {
                    child.StartCol = seg.EndCol + 1;
                    queue.Enqueue(child);
                }
            }
        }

        /// <summary>
        /// Stretches every tip segment (one per currently-live branch, since a tip is always
        /// its own segment's last block) out to the same end column as whichever branch's tip
        /// naturally lands furthest right, so every tip lines up in one vertical column instead
        /// of trailing off at whatever column its own history happened to reach. A stretched
        /// segment first reveals more of its real blocks instead of folding them into the
        /// hidden-count gap — never invents blocks that don't exist — and only once every real
        /// block is already shown does the tip itself simply render at the shared column, with
        /// a plain, dot-free line covering whatever distance no real block ever filled. Only
        /// tip segments move; a fork point's column stays exactly where the fork actually
        /// happened.
        /// </summary>
        private static void AlignLeafTips(List<ChainSegment> segments)
        {
            var leaves = segments.Where(s => s.Children.Count == 0).ToList();
            if (leaves.Count == 0) return;

            var targetCol = leaves.Max(s => s.EndCol);
            foreach (var seg in leaves)
            {
                if (seg.EndCol >= targetCol) continue;

                var neededSpan = targetCol - seg.StartCol + 1;
                if (neededSpan >= seg.Blocks.Count)
                {
                    seg.Collapsed = false;
                    seg.ExtraShownPrefixCount = 0;
                    seg.Span = seg.Blocks.Count;
                }
                else
                {
                    seg.ExtraShownPrefixCount = neededSpan - 3;
                    seg.Span = neededSpan;
                }
                seg.EndCol = targetCol;
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private sealed class DashboardNode
        {
            public string Id { get; init; } = "";
            public string Role { get; init; } = "";
            public bool CanMine { get; init; }
            public int HashPower { get; init; }
            public double HashPowerShare { get; init; }
            public string? Pool { get; init; }
            public int EconomicWeight { get; init; }
            public int PeerCount { get; init; }
            public int BlocksWon { get; init; }
            public Dictionary<string, decimal> Balances { get; init; } = new();
            public Dictionary<string, decimal> TotalSent { get; init; } = new();
        }

        private sealed class DashboardPool
        {
            public string Name { get; init; } = "";
            public int MemberCount { get; init; }
            public int TotalHashPower { get; init; }
            public double HashPowerShare { get; init; }
        }

        private sealed class DashboardFork
        {
            public string TipHash { get; init; } = "";
            public int Height { get; init; }
            public int NodeCount { get; init; }
            public double Share { get; init; }
            public int HashPower { get; init; }
            public double HashPowerShare { get; init; }
            public List<string> NodeIds { get; init; } = new();
        }

        private sealed class DashboardReorg
        {
            public string Timestamp { get; init; } = "";
            public string NodeId { get; init; } = "";
            public string Reason { get; init; } = "";
        }

        private sealed class DashboardChainSegmentBlock
        {
            public string Hash { get; init; } = "";
            public int Height { get; init; }
            public string BuiltBy { get; init; } = "";
            public List<string> NodeIds { get; init; } = new();
            public bool IsTip { get; init; }
            public bool IsShared { get; init; }
            public double? NodeShare { get; init; }
            public double? HashPowerShare { get; init; }
            public string? RuleName { get; init; }
        }

        private sealed class DashboardChainSegment
        {
            public int Id { get; init; }
            public int? ParentId { get; init; }
            public int Lane { get; init; }
            public int StartCol { get; init; }
            public int EndCol { get; init; }
            public bool Collapsed { get; init; }
            public List<DashboardChainSegmentBlock> Blocks { get; init; } = new();
            public int HiddenCount { get; init; }
            public int? HiddenFromHeight { get; init; }
            public int? HiddenToHeight { get; init; }
        }

        private sealed class DashboardChainGraph
        {
            public int TotalColumns { get; init; }
            public int LaneCount { get; init; }
            public List<DashboardChainSegment> Segments { get; init; } = new();
        }

        private sealed class DashboardScenarioNodeGroup
        {
            public int Count { get; init; }
            public string Role { get; init; } = "";
            public int HashPower { get; init; }
            public bool CanMine { get; init; }
            public string? Pool { get; init; }
            public bool ValueSeeking { get; init; }
            public string? RulesName { get; init; }
        }

        private sealed class DashboardScenarioPhase
        {
            public int Index { get; init; }
            public bool IsCurrent { get; init; }
            public string? Description { get; init; }
            public int? DurationSeconds { get; init; }
            public List<DashboardScenarioNodeGroup> NodeGroups { get; init; } = new();
        }

        private sealed class DashboardScenario
        {
            public string? FileName { get; init; }
            public string? Description { get; init; }
            public int TotalPhases { get; init; }
            public int CurrentPhaseIndex { get; init; }
            public int CurrentPhaseElapsedSeconds { get; init; }
            public List<DashboardScenarioPhase> Phases { get; init; } = new();
        }

        private sealed class DashboardSummary
        {
            public string GeneratedAt { get; init; } = "";
            public string NetworkState { get; init; } = "";
            public bool ChainsConverged { get; init; }
            public int ChainHeight { get; init; }
            public int BlocksObserved { get; init; }
            public int ParticipantCount { get; init; }
            public int MiningNodeCount { get; init; }
            public int WalletOnlyCount { get; init; }
            public int PoolCount { get; init; }
            public int TotalHashPower { get; init; }
            public List<DashboardPool> Pools { get; init; } = new();
            public List<DashboardNode> TopMinersByHashPower { get; init; } = new();
            public List<DashboardNode> TopMinersByBlocksWon { get; init; } = new();
            public List<DashboardNode> TopByInfluence { get; init; } = new();
            public List<DashboardNode> AllNodes { get; init; } = new();
            public int ReorganizationsObserved { get; init; }
            public List<DashboardFork> Forks { get; init; } = new();
            public List<DashboardReorg> RecentReorganizations { get; init; } = new();
            /// <summary>The asset currently active on this payload's single canonical view — see <see cref="ComputeLedgerSummary"/>.</summary>
            public string CurrentAsset { get; init; } = "";
            /// <summary>Each distinct asset's current $-reference price — see <see cref="ComputeLedgerSummary"/>.</summary>
            public Dictionary<string, decimal> AssetPrices { get; init; } = new();
            public DashboardScenario? Scenario { get; init; }
        }

        private const string HtmlPage = @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>Bitcoin Network Simulator — Dashboard</title>
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<style>
  :root {
    --bg: #0b0e14; --panel: #131822; --panel-border: #232b3a;
    --text: #e6e9ef; --text-dim: #8b93a7; --accent: #f7931a;
    --accent-dim: #7a5117; --good: #3fb950; --warn: #d29922; --bad: #f85149;
    --bar-bg: #1c2333;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 24px; background: var(--bg); color: var(--text);
    font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Helvetica, Arial, sans-serif;
  }
  h1 { font-size: 20px; margin: 0 0 4px 0; }
  .sub { color: var(--text-dim); font-size: 13px; margin-bottom: 20px; }
  .sub code { color: var(--text); }
  .badge {
    display: inline-block; padding: 2px 10px; border-radius: 12px;
    font-size: 12px; font-weight: 600; letter-spacing: .02em;
  }
  .badge.healthy { background: rgba(63,185,80,.15); color: var(--good); }
  .badge.recovering { background: rgba(210,153,34,.15); color: var(--warn); }
  .badge.invalidstate { background: rgba(248,81,73,.15); color: var(--bad); }
  .badge.unknown { background: rgba(139,147,167,.15); color: var(--text-dim); }
  .cards {
    display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
    gap: 12px; margin-bottom: 24px;
  }
  .card {
    background: var(--panel); border: 1px solid var(--panel-border);
    border-radius: 10px; padding: 14px 16px;
  }
  .card .value { font-size: 26px; font-weight: 700; font-variant-numeric: tabular-nums; }
  .card .label { font-size: 12px; color: var(--text-dim); margin-top: 2px; }
  .panels { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 16px; }
  .panel {
    background: var(--panel); border: 1px solid var(--panel-border);
    border-radius: 10px; padding: 16px; overflow-x: auto;
  }
  .panel h2 { font-size: 14px; margin: 0 0 12px 0; color: var(--text); }
  .panel h2 .hint { color: var(--text-dim); font-weight: 400; }
  .row {
    display: grid; grid-template-columns: 28px 90px 1fr 60px; align-items: center;
    gap: 8px; padding: 5px 0; font-size: 13px;
  }
  .row.fork-row { grid-template-columns: 28px 90px 1fr 170px; }
  .row .rank { color: var(--text-dim); font-variant-numeric: tabular-nums; }
  .row .id { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .row .num { text-align: right; font-variant-numeric: tabular-nums; color: var(--text-dim); }
  .bar-track { height: 8px; background: var(--bar-bg); border-radius: 4px; overflow: hidden; }
  .bar-fill { height: 100%; background: var(--accent); border-radius: 4px; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--panel-border); white-space: nowrap; }
  th { color: var(--text-dim); font-weight: 500; }
  td.id { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
  tr.child-row td { padding-top: 0; padding-bottom: 4px; }
  .role-honest { color: var(--good); }
  .role-other { color: var(--warn); }
  .full-width { grid-column: 1 / -1; }
  .chain-graph-wrap { overflow-x: auto; }
  .chain-graph-wrap svg { display: block; }
  .chain-graph-legend { display: flex; gap: 16px; margin-top: 8px; font-size: 12px; color: var(--text-dim); }
  .chain-graph-legend .dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 5px; vertical-align: middle; }
  .empty { color: var(--text-dim); font-size: 13px; padding: 8px 0; }
  .scenario-panel { margin-bottom: 24px; }
  .scenario-file { font-size: 13px; color: var(--text-dim); margin-bottom: 10px; }
  .scenario-file code { color: var(--text); }
  .phase-list { display: flex; flex-direction: column; gap: 8px; }
  .phase {
    border: 1px solid var(--panel-border); border-radius: 8px;
    padding: 10px 12px; font-size: 13px;
  }
  .phase.current { border-color: var(--accent); background: rgba(247,147,26,.06); }
  .phase-head { display: flex; align-items: center; gap: 8px; font-weight: 600; }
  .phase-head .badge { font-size: 11px; padding: 1px 8px; }
  .phase-desc { color: var(--text-dim); margin-top: 4px; }
  .phase-groups { margin-top: 6px; font-size: 12px; color: var(--text-dim); }
  .phase-groups .group { display: inline-block; background: var(--bar-bg); border-radius: 6px; padding: 2px 8px; margin: 2px 4px 0 0; color: var(--text); }
  .explorer-controls, .filters { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; flex-wrap: wrap; }
  .explorer-controls select, .explorer-controls input, .explorer-controls button,
  .filters select, .filters input, .filters button {
    background: var(--bar-bg); color: var(--text); border: 1px solid var(--panel-border);
    border-radius: 6px; padding: 6px 10px; font-size: 13px;
  }
  .explorer-controls button, .filters button { cursor: pointer; }
  .explorer-controls button:hover, .filters button:hover { border-color: var(--accent); }
  .explorer-controls input { flex: 1; min-width: 180px; }
  .filters input { min-width: 140px; }
  tr.totals-row td { font-weight: 600; border-top: 2px solid var(--panel-border); }
  .explorer-status { color: var(--text-dim); font-size: 12px; }
  .explorer-block { border: 1px solid var(--panel-border); border-radius: 8px; margin-bottom: 6px; overflow: hidden; }
  .explorer-block-row {
    display: grid; grid-template-columns: 70px 1fr 110px 60px 150px; gap: 8px;
    padding: 8px 12px; font-size: 13px; cursor: pointer; align-items: center;
  }
  .explorer-block-row:hover, .explorer-block-row.expanded { background: var(--bar-bg); }
  .explorer-block-row .h { color: var(--text-dim); font-variant-numeric: tabular-nums; }
  .explorer-block-row .hash { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .explorer-block-row .tip-badge { font-size: 10px; padding: 1px 6px; border-radius: 8px; background: rgba(247,147,26,.15); color: var(--accent); margin-left: 6px; }
  .explorer-block-detail { padding: 12px; border-top: 1px solid var(--panel-border); font-size: 13px; }
  .explorer-block-detail .kv { display: grid; grid-template-columns: 120px 1fr; gap: 4px 12px; margin-bottom: 10px; }
  .explorer-block-detail .kv div:nth-child(odd) { color: var(--text-dim); }
  .explorer-block-detail .kv div:nth-child(even) { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; word-break: break-all; }
  .explorer-load-more { text-align: center; padding: 4px 0 12px; }
</style>
</head>
<body>
  <h1>Bitcoin Network Simulator</h1>
  <div class=""sub"">
    <span id=""state-badge"" class=""badge unknown"">loading…</span>
    &nbsp;chain height <code id=""height"">—</code>
    &nbsp;·&nbsp;updated <code id=""updated"">—</code>
  </div>

  <div class=""panel scenario-panel"" id=""scenario-panel"">
    <h2>Scenario</h2>
    <div id=""scenario-info""></div>
  </div>

  <div class=""cards"" id=""cards""></div>

  <div class=""panels"">
    <div class=""panel"">
      <h2>Top miners <span class=""hint"">by hash power share</span></h2>
      <div id=""top-hashpower""></div>
    </div>
    <div class=""panel"">
      <h2>Top miners <span class=""hint"">by blocks won</span></h2>
      <div id=""top-blockswon""></div>
    </div>
    <div class=""panel"">
      <h2>Most influential nodes <span class=""hint"">by peer connections</span></h2>
      <div id=""top-influence""></div>
    </div>
    <div class=""panel"">
      <h2>Mining pools</h2>
      <div id=""pools""></div>
    </div>
    <div class=""panel"">
      <h2>Active forks <span class=""hint"">by tip hash</span></h2>
      <div id=""forks""></div>
    </div>
    <div class=""panel"">
      <h2>Recent reorganizations</h2>
      <div style=""max-height:200px; overflow-y:auto;"">
        <table>
          <thead><tr><th>Time</th><th>Node</th><th>Reason</th></tr></thead>
          <tbody id=""reorgs""></tbody>
        </table>
      </div>
    </div>
    <div class=""panel full-width"">
      <h2>Chain graph <span class=""hint"">genesis to tip, collapsed where straight</span></h2>
      <div id=""chain-graph""></div>
    </div>
    <div class=""panel full-width"">
      <h2>Block explorer <span class=""hint"">browse any node's own chain</span></h2>
      <div class=""explorer-controls"">
        <select id=""explorer-node""><option value="""">Pick a node…</option></select>
        <input id=""explorer-search"" type=""text"" placeholder=""jump to height or hash prefix"">
        <button id=""explorer-go"" type=""button"">Go</button>
        <button id=""explorer-reload"" type=""button"">Reload</button>
        <span id=""explorer-status"" class=""explorer-status""></span>
      </div>
      <div id=""explorer-body""><div class=""empty"">Pick a node above to browse its chain.</div></div>
    </div>
    <div class=""panel full-width"">
      <h2>All participants</h2>
      <div class=""filters"">
        <input id=""filter-node"" type=""text"" placeholder=""filter by node id…"">
        <select id=""filter-role""></select>
        <select id=""filter-mines"">
          <option value="""">Mines: all</option><option value=""yes"">Mines: yes</option><option value=""no"">Mines: no</option>
        </select>
        <select id=""filter-pool""></select>
        <select id=""filter-asset""></select>
        <button id=""filter-clear"" type=""button"">Clear filters</button>
      </div>
      <div style=""max-height:420px; overflow-y:auto;"">
        <table>
          <thead><tr>
            <th>Node</th><th>Role</th><th>Mines</th><th>Hash power</th>
            <th>Pool</th><th>Blocks won</th><th>Peers</th><th>Economic weight</th>
            <th>Asset</th><th>Balance</th><th>Value</th><th>Sent</th>
          </tr></thead>
          <tbody id=""all-nodes""></tbody>
        </table>
      </div>
    </div>
  </div>

<script>
function fmtPct(x) { return (x * 100).toFixed(1) + '%'; }

function barRow(rank, id, label, value, share) {
  var pct = Math.max(share * 100, value > 0 ? 1.5 : 0);
  return '<div class=""row"">' +
    '<span class=""rank"">#' + rank + '</span>' +
    '<span class=""id"" title=""' + id + '"">' + id + '</span>' +
    '<span class=""bar-track""><span class=""bar-fill"" style=""width:' + pct + '%""></span></span>' +
    '<span class=""num"">' + label + '</span>' +
    '</div>';
}

function renderRankedList(elId, nodes, valueFn, labelFn, shareFn) {
  var el = document.getElementById(elId);
  if (!nodes.length) { el.innerHTML = '<div class=""empty"">No data yet.</div>'; return; }
  var html = '';
  nodes.forEach(function (n, i) {
    html += barRow(i + 1, n.id, labelFn(n), valueFn(n), shareFn(n));
  });
  el.innerHTML = html;
}

function renderPools(pools) {
  var el = document.getElementById('pools');
  if (!pools.length) { el.innerHTML = '<div class=""empty"">No pools active.</div>'; return; }
  var html = '';
  pools.forEach(function (p, i) {
    html += barRow(i + 1, p.name, p.memberCount + ' member(s), ' + p.totalHashPower + ' hp', p.totalHashPower, p.hashPowerShare);
  });
  el.innerHTML = html;
}

function renderCards(s) {
  var cards = [
    ['Participants', s.participantCount],
    ['Mining nodes', s.miningNodeCount],
    ['Wallet-only', s.walletOnlyCount],
    ['Pools', s.poolCount],
    ['Total hash power', s.totalHashPower],
    ['Blocks observed', s.blocksObserved],
    ['Active branches', s.forks.length],
    ['Reorganizations', s.reorganizationsObserved],
  ];
  document.getElementById('cards').innerHTML = cards.map(function (c) {
    return '<div class=""card""><div class=""value"">' + c[1] + '</div><div class=""label"">' + c[0] + '</div></div>';
  }).join('');
}

function fmtCoins(x) {
  return Number(x).toFixed(8).replace(/0+$/, '').replace(/\.$/, '') || '0';
}

function fmtValue(x) {
  return '$' + Number(x).toFixed(2);
}

// One row per (node, asset) — a node with a dormant balance, or that's sent a since-drained
// asset, gets more than one. Every row carries the full node context (not just the
// asset/balance/sent) so a column filter can isolate a single row without losing track of
// which node it belongs to, and both balance and sent are that row's own asset only — never
// a flat, cross-asset total repeated on every row for the same node. value is that row's
// balance converted to the system's $-reference money via assetPrices, so — unlike balance
// and sent, which stay asset-denominated — it's the one column safe to add across different
// assets' rows.
function buildNodeAssetRows(nodes, currentAsset, assetPrices) {
  var rows = [];
  nodes.forEach(function (n) {
    var balances = n.balances || {};
    var sentByAsset = n.totalSent || {};
    var assetSet = {};
    Object.keys(balances).forEach(function (a) { if (balances[a] > 0) assetSet[a] = true; });
    Object.keys(sentByAsset).forEach(function (a) { if (sentByAsset[a] > 0) assetSet[a] = true; });
    if (currentAsset) assetSet[currentAsset] = true;
    var assets = Object.keys(assetSet).sort(function (a, b) { return a === currentAsset ? -1 : b === currentAsset ? 1 : a.localeCompare(b); });
    assets.forEach(function (asset) {
      var balance = balances[asset] || 0;
      var price = (assetPrices || {})[asset] || 0;
      rows.push({ node: n, asset: asset, balance: balance, sent: sentByAsset[asset] || 0, value: balance * price });
    });
  });
  return rows;
}

var nodeFilters = { node: '', role: '', mines: '', pool: '', asset: '' };

function rowMatchesFilters(r) {
  if (nodeFilters.node && r.node.id.toLowerCase().indexOf(nodeFilters.node.toLowerCase()) === -1) return false;
  if (nodeFilters.role && r.node.role !== nodeFilters.role) return false;
  if (nodeFilters.mines && (r.node.canMine ? 'yes' : 'no') !== nodeFilters.mines) return false;
  if (nodeFilters.pool && (r.node.pool || '(solo)') !== nodeFilters.pool) return false;
  if (nodeFilters.asset && r.asset !== nodeFilters.asset) return false;
  return true;
}

function uniqueSorted(values) {
  var seen = {}, out = [];
  values.forEach(function (v) { if (!seen[v]) { seen[v] = true; out.push(v); } });
  return out.sort();
}

/// Options are drawn from every currently-known row, not just the filtered ones, so picking
/// one filter never hides the options needed to broaden back out of another.
function populateFilterSelect(id, values, current) {
  var sel = document.getElementById(id);
  if (document.activeElement === sel) return;
  sel.innerHTML = '<option value="""">All</option>' + values.map(function (v) {
    return '<option value=""' + escapeHtml(v) + '""' + (v === current ? ' selected' : '') + '>' + escapeHtml(v) + '</option>';
  }).join('');
}

function sumByAssetText(rows, pick) {
  var byAsset = {};
  rows.forEach(function (r) { byAsset[r.asset] = (byAsset[r.asset] || 0) + pick(r); });
  return Object.keys(byAsset).sort().map(function (a) {
    return fmtCoins(byAsset[a]) + ' ' + escapeHtml(a);
  }).join(', ') || '0';
}

function renderTotalsRow(rows) {
  var seenNodes = {};
  rows.forEach(function (r) { seenNodes[r.node.id] = true; });
  var nodeCount = Object.keys(seenNodes).length;

  var valueTotal = rows.reduce(function (sum, r) { return sum + r.value; }, 0);

  return '<tr class=""totals-row"">' +
    '<td colspan=""9"">Total — ' + nodeCount + ' node' + (nodeCount === 1 ? '' : 's') +
    ' (' + rows.length + ' row' + (rows.length === 1 ? '' : 's') + ')</td>' +
    '<td>' + sumByAssetText(rows, function (r) { return r.balance; }) + '</td>' +
    '<td>' + fmtValue(valueTotal) + '</td>' +
    '<td>' + sumByAssetText(rows, function (r) { return r.sent; }) + '</td>' +
    '</tr>';
}

var lastAllNodes = [], lastCurrentAsset = '', lastAssetPrices = {};

function renderAllNodes(nodes, currentAsset, assetPrices) {
  lastAllNodes = nodes;
  lastCurrentAsset = currentAsset;
  lastAssetPrices = assetPrices || {};

  var allRows = buildNodeAssetRows(nodes, currentAsset, lastAssetPrices);
  populateFilterSelect('filter-role', uniqueSorted(allRows.map(function (r) { return r.node.role; })), nodeFilters.role);
  populateFilterSelect('filter-pool', uniqueSorted(allRows.map(function (r) { return r.node.pool || '(solo)'; })), nodeFilters.pool);
  populateFilterSelect('filter-asset', uniqueSorted(allRows.map(function (r) { return r.asset; })), nodeFilters.asset);

  var rows = allRows.filter(rowMatchesFilters);

  var tbody = document.getElementById('all-nodes');
  if (!allRows.length) { tbody.innerHTML = '<tr><td colspan=""12"" class=""empty"">No nodes yet.</td></tr>'; return; }
  if (!rows.length) { tbody.innerHTML = '<tr><td colspan=""12"" class=""empty"">No rows match the current filters.</td></tr>'; return; }

  var html = rows.map(function (r) {
    var roleClass = r.node.role === 'Honest' ? 'role-honest' : 'role-other';
    return '<tr>' +
      '<td class=""id"">' + r.node.id + '</td>' +
      '<td class=""' + roleClass + '"">' + r.node.role + '</td>' +
      '<td>' + (r.node.canMine ? 'yes' : 'no') + '</td>' +
      '<td>' + r.node.hashPower + ' (' + fmtPct(r.node.hashPowerShare) + ')</td>' +
      '<td>' + (r.node.pool || '(solo)') + '</td>' +
      '<td>' + r.node.blocksWon + '</td>' +
      '<td>' + r.node.peerCount + '</td>' +
      '<td>' + r.node.economicWeight + '</td>' +
      '<td>' + escapeHtml(r.asset) + '</td>' +
      '<td>' + fmtCoins(r.balance) + '</td>' +
      '<td>' + fmtValue(r.value) + '</td>' +
      '<td>' + fmtCoins(r.sent) + '</td>' +
      '</tr>';
  }).join('') + renderTotalsRow(rows);

  tbody.innerHTML = html;
}

function initFilterControls() {
  document.getElementById('filter-node').addEventListener('input', function (e) {
    nodeFilters.node = e.target.value;
    renderAllNodes(lastAllNodes, lastCurrentAsset, lastAssetPrices);
  });
  ['role', 'mines', 'pool', 'asset'].forEach(function (key) {
    document.getElementById('filter-' + key).addEventListener('change', function (e) {
      nodeFilters[key] = e.target.value;
      renderAllNodes(lastAllNodes, lastCurrentAsset, lastAssetPrices);
    });
  });
  document.getElementById('filter-clear').addEventListener('click', function () {
    nodeFilters = { node: '', role: '', mines: '', pool: '', asset: '' };
    document.getElementById('filter-node').value = '';
    document.getElementById('filter-mines').value = '';
    renderAllNodes(lastAllNodes, lastCurrentAsset, lastAssetPrices);
  });
}
initFilterControls();

function renderForks(forks) {
  var el = document.getElementById('forks');
  if (!forks.length) { el.innerHTML = '<div class=""empty"">No audit yet.</div>'; return; }
  if (forks.length === 1) {
    el.innerHTML = '<div class=""empty"">Converged — every validated node is on tip ' +
      forks[0].tipHash.slice(0, 8) + '… at height ' + forks[0].height + '.</div>';
    return;
  }
  var html = '';
  forks.forEach(function (f, i) {
    var pct = Math.max(Math.max(f.share, f.hashPowerShare) * 100, 1.5);
    var title = f.tipHash + '\n' + f.nodeIds.join(', ');
    html += '<div class=""row fork-row"" title=""' + title + '"">' +
      '<span class=""rank"">#' + (i + 1) + '</span>' +
      '<span class=""id"">' + f.tipHash.slice(0, 8) + '… @' + f.height + '</span>' +
      '<span class=""bar-track""><span class=""bar-fill"" style=""width:' + pct + '%""></span></span>' +
      '<span class=""num"">' + f.nodeCount + ' node(s) (' + fmtPct(f.share) + ') &middot; ' +
      f.hashPower + ' hp (' + fmtPct(f.hashPowerShare) + ')</span>' +
      '</div>';
  });
  el.innerHTML = html;
}

function renderReorgs(reorgs) {
  var tbody = document.getElementById('reorgs');
  if (!reorgs.length) { tbody.innerHTML = '<tr><td colspan=""3"" class=""empty"">No reorganizations observed.</td></tr>'; return; }
  tbody.innerHTML = reorgs.map(function (r) {
    return '<tr>' +
      '<td>' + new Date(r.timestamp).toLocaleTimeString() + '</td>' +
      '<td class=""id"">' + r.nodeId + '</td>' +
      '<td>' + r.reason + '</td>' +
      '</tr>';
  }).join('');
}

function escapeHtml(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/""/g, '&quot;')
    .replace(/'/g, '&#39;');
}

// The server (Dashboard.BuildChainGraphJson) already pruned ungrown fork stubs, split the
// remaining blocks into segments, and assigned each one a column and lane — this is a pure
// renderer over that pre-collapsed shape, never the raw block-per-height data.
var _labelMeasureCtx = document.createElement('canvas').getContext('2d');
function measureLabelWidth(text, font) {
  _labelMeasureCtx.font = font;
  return _labelMeasureCtx.measureText(text).width;
}

function renderChainGraph(graph) {
  var container = document.getElementById('chain-graph');
  if (!graph || !graph.segments.length) { container.innerHTML = '<div class=""empty"">No blocks yet.</div>'; return; }

  var colW = 34, rowH = 26, marginL = 10, marginT = 14, marginB = 10;
  var height = marginT + graph.laneCount * rowH + marginB;
  var maxLabelRight = 0;

  function xOf(col) { return marginL + col * colW + colW / 2; }
  function yOf(lane) { return marginT + lane * rowH + rowH / 2; }

  var segById = {};
  graph.segments.forEach(function (s) { segById[s.id] = s; });

  // Every lane gets its own stable, well-separated hue (golden-angle spacing), so a branch's
  // dots and connecting lines stay one consistent color from where it splits off to its tip —
  // the only way to tell two simultaneously-live chains apart at a glance.
  function laneColor(lane) { return 'hsl(' + ((lane * 137.508) % 360).toFixed(0) + ', 62%, 58%)'; }

  var lines = '', dots = '', labels = '';

  function renderBlockDot(b, col, lane) {
    var color = laneColor(lane);
    var r = b.isTip ? 7 : 5;
    var opacity = b.isTip || b.isShared ? 1 : 0.6;
    var title = 'height ' + b.height + '\nbuilt by ' + b.builtBy + '\n' + b.hash + '\n' +
      b.nodeIds.length + ' node(s): ' + b.nodeIds.join(', ');
    if (b.ruleName) title += '\nrules: ' + b.ruleName;
    if (b.isTip && b.nodeShare != null) {
      title += '\n' + fmtPct(b.nodeShare) + ' of nodes, ' + fmtPct(b.hashPowerShare) + ' of hash power';
    }
    var x = xOf(col), y = yOf(lane);
    dots += '<circle cx=""' + x + '"" cy=""' + y + '"" r=""' + r + '"" fill=""' + color + '"" opacity=""' + opacity +
      '"" stroke=""' + (b.isTip ? 'var(--text)' : 'var(--bg)') + '"" stroke-width=""' + (b.isTip ? 2 : 1.5) +
      '""><title>' + escapeHtml(title) + '</title></circle>';
    if (b.isTip && b.nodeShare != null) {
      var label = (b.ruleName ? b.ruleName + ' — ' : '') + fmtPct(b.nodeShare) + ' nodes · ' + fmtPct(b.hashPowerShare) + ' hash';
      var font = '10px -apple-system, BlinkMacSystemFont, ""Segoe UI"", Helvetica, Arial, sans-serif';
      var textW = measureLabelWidth(label, font);
      var padX = 6, pillH = 16;
      var pillX = x + r + 5, pillY = y - pillH / 2;
      var pillW = textW + padX * 2;
      labels += '<rect x=""' + pillX + '"" y=""' + pillY + '"" width=""' + pillW + '"" height=""' + pillH +
        '"" rx=""' + (pillH / 2) + '"" fill=""var(--bar-bg)"" stroke=""' + color + '"" stroke-width=""1"" />' +
        '<text x=""' + (pillX + padX) + '"" y=""' + (y + 3.5) + '"" font-size=""10"" fill=""' + color +
        '"" text-anchor=""start"">' + escapeHtml(label) + '</text>';
      maxLabelRight = Math.max(maxLabelRight, pillX + pillW);
    }
    return { x: x, y: y, col: col };
  }

  function renderGapDot(count, fromHeight, toHeight, col, lane) {
    var x = xOf(col), y = yOf(lane);
    var range = toHeight > fromHeight ? (fromHeight + '–' + toHeight) : String(fromHeight);
    var title = count + ' block(s) hidden (height ' + range + ', no forks)';
    var fontSize = count >= 100 ? 8 : 9;
    dots += '<g><title>' + escapeHtml(title) + '</title>' +
      '<circle cx=""' + x + '"" cy=""' + y + '"" r=""9"" fill=""var(--bar-bg)"" stroke=""' + laneColor(lane) + '"" stroke-width=""1.5"" />' +
      '<text x=""' + x + '"" y=""' + y + '"" font-size=""' + fontSize + '"" fill=""var(--text-dim)"" text-anchor=""middle"" dominant-baseline=""central"">' + count + '</text>' +
      '</g>';
    return { x: x, y: y, col: col };
  }

  graph.segments.forEach(function (seg) {
    var color = laneColor(seg.lane);
    var points;
    if (seg.collapsed) {
      // seg.blocks holds [first, ...any extra blocks revealed to stretch this tip segment
      // into column alignment with the tree's other tips, last] — the gap dot always sits
      // right before the last one, however many extra blocks came before it.
      var extraCount = seg.blocks.length - 2;
      points = [renderBlockDot(seg.blocks[0], seg.startCol, seg.lane)];
      for (var e = 0; e < extraCount; e++) {
        points.push(renderBlockDot(seg.blocks[1 + e], seg.startCol + 1 + e, seg.lane));
      }
      points.push(renderGapDot(seg.hiddenCount, seg.hiddenFromHeight, seg.hiddenToHeight, seg.startCol + 1 + extraCount, seg.lane));
      points.push(renderBlockDot(seg.blocks[seg.blocks.length - 1], seg.startCol + 2 + extraCount, seg.lane));
    } else {
      // A tip segment stretched to reach the tree's shared tip column can run out of real
      // blocks before it gets there — rather than inventing dots, the last (tip) block just
      // renders at the shared column directly, leaving a plain, dot-free line to cover the
      // distance no real block ever filled.
      var lastIdx = seg.blocks.length - 1;
      points = seg.blocks.map(function (b, i) {
        return renderBlockDot(b, i === lastIdx ? seg.endCol : seg.startCol + i, seg.lane);
      });
    }
    for (var i = 1; i < points.length; i++) {
      var dashed = seg.collapsed || (points[i].col - points[i - 1].col > 1);
      lines += '<line x1=""' + points[i - 1].x + '"" y1=""' + points[i - 1].y + '"" x2=""' + points[i].x + '"" y2=""' + points[i].y +
        '"" stroke=""' + color + '"" stroke-width=""2""' + (dashed ? ' stroke-dasharray=""4,3""' : '') + ' />';
    }

    if (seg.parentId !== null && seg.parentId !== undefined) {
      var parentSeg = segById[seg.parentId];
      var parentCol = parentSeg.collapsed ? parentSeg.startCol + 2 : parentSeg.startCol + parentSeg.blocks.length - 1;
      lines += '<line x1=""' + xOf(parentCol) + '"" y1=""' + yOf(parentSeg.lane) + '"" x2=""' + points[0].x +
        '"" y2=""' + points[0].y + '"" stroke=""' + color + '"" stroke-width=""2"" />';
    }
  });

  var width = Math.max(marginL + graph.totalColumns * colW + 16, maxLabelRight + 10);
  var svg = '<svg width=""' + width + '"" height=""' + height + '"" viewBox=""0 0 ' + width + ' ' + height + '"">' +
    lines + dots + labels + '</svg>';

  container.innerHTML = '<div class=""chain-graph-wrap"">' + svg + '</div>' +
    '<div class=""chain-graph-legend"">' +
    '<span>each color is one currently-live branch</span>' +
    '<span>larger, ringed dot = that branch’s tip — labeled with its ruleset name (if any) and its share of nodes and hash power</span>' +
    '<span>faded dot = minority/orphaned block, not the tip of any lane</span>' +
    '<span><span class=""dot"" style=""background:var(--bar-bg); border:1px solid var(--panel-border)""></span>N blocks compressed (no forks in between) &#8212; hover any dot for height/details</span>' +
    '</div>';
}

// Block explorer: fetches one node's own /chain endpoint directly (same origin, absolute
// path — the dashboard's own JSON endpoints are all under /dashboard/, but a node's routes
// live at the site root) rather than through any dashboard-built endpoint, and browses it
// entirely client-side. Deliberately NOT tied to the 2s auto-refresh loop — a chain can grow
// large, so it only ever fetches on an explicit user action (picking a node, searching,
// reloading), never automatically.
var EXPLORER_PAGE_SIZE = 50;
var explorerChain = null;
var explorerNodeId = '';
var explorerStart = 0;
var explorerEnd = 0;
var explorerExpandedHash = null;
var explorerKnownNodeIds = [];

function populateExplorerNodeOptions(nodeIds) {
  var changed = nodeIds.length !== explorerKnownNodeIds.length ||
    nodeIds.some(function (id, i) { return id !== explorerKnownNodeIds[i]; });
  if (!changed) return;
  explorerKnownNodeIds = nodeIds.slice();

  var select = document.getElementById('explorer-node');
  var current = select.value;
  var html = '<option value="""">Pick a node…</option>';
  nodeIds.forEach(function (id) { html += '<option value=""' + id + '"">' + id + '</option>'; });
  select.innerHTML = html;
  if (nodeIds.indexOf(current) !== -1) select.value = current;
}

function explorerSetStatus(text) {
  document.getElementById('explorer-status').textContent = text;
}

function explorerLoad(nodeId, focusQuery) {
  if (!nodeId) return;
  explorerNodeId = nodeId;
  explorerSetStatus('loading…');
  fetch('/' + nodeId + '/chain').then(function (r) { return r.json(); }).then(function (chain) {
    explorerChain = chain;
    explorerSetStatus(chain.length + ' block(s), tip height ' + (chain.length - 1));
    if (focusQuery) {
      explorerJumpTo(focusQuery);
    } else {
      explorerEnd = chain.length;
      explorerStart = Math.max(0, explorerEnd - EXPLORER_PAGE_SIZE);
      explorerExpandedHash = null;
      renderExplorer();
    }
  }).catch(function (err) {
    explorerChain = null;
    explorerSetStatus('failed to load');
    document.getElementById('explorer-body').innerHTML = '<div class=""empty"">Could not load this node&#39;s chain: ' + escapeHtml(String(err)) + '</div>';
  });
}

function explorerFindIndex(query) {
  if (/^\d+$/.test(query)) {
    var h = parseInt(query, 10);
    return (h >= 0 && h < explorerChain.length) ? h : -1;
  }
  var q = query.trim().toLowerCase();
  for (var i = explorerChain.length - 1; i >= 0; i--) {
    if (explorerChain[i].Hash.toLowerCase().indexOf(q) === 0) return i;
  }
  return -1;
}

function explorerJumpTo(query) {
  if (!explorerChain) return;
  var idx = explorerFindIndex(String(query).trim());
  if (idx === -1) { explorerSetStatus('no block matches ""' + query + '""'); return; }
  explorerEnd = Math.min(explorerChain.length, idx + 1 + Math.floor(EXPLORER_PAGE_SIZE / 2));
  explorerStart = Math.max(0, Math.min(idx, explorerEnd - EXPLORER_PAGE_SIZE));
  explorerExpandedHash = explorerChain[idx].Hash;
  explorerSetStatus(explorerChain.length + ' block(s), tip height ' + (explorerChain.length - 1));
  renderExplorer();
}

function explorerShowOlder() {
  explorerStart = Math.max(0, explorerStart - EXPLORER_PAGE_SIZE);
  renderExplorer();
}

function explorerToggle(hash) {
  explorerExpandedHash = (explorerExpandedHash === hash) ? null : hash;
  renderExplorer();
}

function explorerTxRows(txs) {
  if (!txs.length) return '<div class=""empty"">No transactions.</div>';
  return '<table><thead><tr><th>From</th><th>To</th><th>Amount</th></tr></thead><tbody>' +
    txs.map(function (t) {
      var fromClass = t.From === 'coinbase' ? ' class=""role-honest""' : '';
      return '<tr><td' + fromClass + '>' + escapeHtml(t.From) + '</td><td>' + escapeHtml(t.To) + '</td><td>' + t.Amount + '</td></tr>';
    }).join('') + '</tbody></table>';
}

function renderExplorer() {
  var body = document.getElementById('explorer-body');
  if (!explorerChain) { body.innerHTML = '<div class=""empty"">Pick a node above to browse its chain.</div>'; return; }
  if (!explorerChain.length) { body.innerHTML = '<div class=""empty"">This node has no blocks.</div>'; return; }

  var tipHeight = explorerChain.length - 1;
  var html = '';
  if (explorerStart > 0) {
    html += '<div class=""explorer-load-more""><button type=""button"" onclick=""explorerShowOlder()"">Show older blocks</button></div>';
  }

  for (var i = explorerEnd - 1; i >= explorerStart; i--) {
    var b = explorerChain[i];
    var isTip = i === tipHeight;
    var expanded = explorerExpandedHash === b.Hash;
    html += '<div class=""explorer-block"">' +
      '<div class=""explorer-block-row' + (expanded ? ' expanded' : '') + '"" onclick=""explorerToggle(&quot;' + b.Hash + '&quot;)"">' +
      '<span class=""h"">#' + b.Index + '</span>' +
      '<span class=""hash"">' + b.Hash + (isTip ? '<span class=""tip-badge"">tip</span>' : '') + '</span>' +
      '<span>' + escapeHtml(b.BuiltBy) + '</span>' +
      '<span>' + b.Transactions.length + ' tx</span>' +
      '<span>' + escapeHtml(new Date(b.Timestamp).toLocaleString()) + '</span>' +
      '</div>';
    if (expanded) {
      html += '<div class=""explorer-block-detail"">' +
        '<div class=""kv"">' +
        '<div>Hash</div><div>' + b.Hash + '</div>' +
        '<div>Previous hash</div><div>' + b.PreviousHash + '</div>' +
        '<div>Built by</div><div>' + escapeHtml(b.BuiltBy) + '</div>' +
        '<div>Signature</div><div>' + b.Signature + '</div>' +
        '<div>Target</div><div>' + b.Target + '</div>' +
        '<div>Nonce</div><div>' + b.Nonce + '</div>' +
        '<div>Timestamp</div><div>' + b.Timestamp + '</div>' +
        '</div>' +
        explorerTxRows(b.Transactions) +
        '</div>';
    }
    html += '</div>';
  }

  body.innerHTML = html;
}

document.getElementById('explorer-node').addEventListener('change', function () {
  explorerLoad(this.value);
});
document.getElementById('explorer-go').addEventListener('click', function () {
  var q = document.getElementById('explorer-search').value.trim();
  if (!q) return;
  var selected = document.getElementById('explorer-node').value;
  if (!selected) { explorerSetStatus('pick a node first'); return; }
  if (selected !== explorerNodeId || !explorerChain) {
    explorerLoad(selected, q);
  } else {
    explorerJumpTo(q);
  }
});
document.getElementById('explorer-search').addEventListener('keydown', function (e) {
  if (e.key === 'Enter') document.getElementById('explorer-go').click();
});
document.getElementById('explorer-reload').addEventListener('click', function () {
  if (explorerNodeId) explorerLoad(explorerNodeId);
});

function renderScenario(sc) {
  var el = document.getElementById('scenario-info');
  if (!sc || !sc.fileName) {
    el.innerHTML = '<div class=""empty"">No scenario file — running with the default single-node start.</div>';
    return;
  }

  var html = '<div class=""scenario-file""><code>' + escapeHtml(sc.fileName) + '</code>' +
    (sc.description ? ' — ' + escapeHtml(sc.description) : '') + '</div>';

  html += '<div class=""phase-list"">';
  sc.phases.forEach(function (p) {
    var duration = p.durationSeconds ? (p.durationSeconds + 's') : 'no automatic stop';
    var head = 'Phase ' + (p.index + 1) + ' / ' + sc.totalPhases + ' &middot; ' + duration;
    var currentBadge = p.isCurrent ? ' <span class=""badge healthy"">current &middot; ' + sc.currentPhaseElapsedSeconds + 's elapsed</span>' : '';
    var groups = p.nodeGroups.map(function (g) {
      var bits = [g.count + '&#215; ' + escapeHtml(g.role)];
      if (g.hashPower) bits.push('hp ' + g.hashPower);
      if (!g.canMine) bits.push('wallet-only');
      if (g.pool) bits.push('pool: ' + escapeHtml(g.pool));
      if (g.valueSeeking) bits.push('value-seeking');
      if (g.rulesName) bits.push('rules: ' + escapeHtml(g.rulesName));
      return '<span class=""group"">' + bits.join(', ') + '</span>';
    }).join('');

    html += '<div class=""phase' + (p.isCurrent ? ' current' : '') + '"">' +
      '<div class=""phase-head"">' + head + currentBadge + '</div>' +
      (p.description ? '<div class=""phase-desc"">' + escapeHtml(p.description) + '</div>' : '') +
      (groups ? '<div class=""phase-groups"">' + groups + '</div>' : '') +
      '</div>';
  });
  html += '</div>';

  el.innerHTML = html;
}

function renderState(s) {
  var badge = document.getElementById('state-badge');
  badge.textContent = s.networkState + (s.chainsConverged ? ' · converged' : '');
  badge.className = 'badge ' + s.networkState.toLowerCase();
  document.getElementById('height').textContent = s.chainHeight;
  document.getElementById('updated').textContent = new Date(s.generatedAt).toLocaleTimeString();
}

var maxPeers = 1;
function refresh() {
  Promise.all([
    fetch('summary').then(function (r) { return r.json(); }),
    fetch('chaingraph').then(function (r) { return r.json(); })
  ]).then(function (results) {
    var s = results[0], graph = results[1];
    renderState(s);
    renderScenario(s.scenario);
    renderCards(s);
    renderRankedList('top-hashpower', s.topMinersByHashPower,
      function (n) { return n.hashPower; },
      function (n) { return n.hashPower + ' hp (' + fmtPct(n.hashPowerShare) + ')'; },
      function (n) { return n.hashPowerShare; });
    renderRankedList('top-blockswon', s.topMinersByBlocksWon,
      function (n) { return n.blocksWon; },
      function (n) { return n.blocksWon + ' won'; },
      function (n) { return s.blocksObserved > 0 ? n.blocksWon / s.blocksObserved : 0; });
    maxPeers = Math.max(1, s.topByInfluence.length ? s.topByInfluence[0].peerCount : 1);
    renderRankedList('top-influence', s.topByInfluence,
      function (n) { return n.peerCount; },
      function (n) { return n.peerCount + ' peers'; },
      function (n) { return n.peerCount / maxPeers; });
    renderPools(s.pools);
    renderForks(s.forks);
    renderReorgs(s.recentReorganizations);
    renderChainGraph(graph);
    renderAllNodes(s.allNodes, s.currentAsset, s.assetPrices);
    populateExplorerNodeOptions(s.allNodes.map(function (n) { return n.id; }).sort());
  }).catch(function (err) { console.error('dashboard refresh failed', err); });
}

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>";
    }
}
