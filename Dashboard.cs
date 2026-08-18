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
            var totalHashPower = Math.Max(1, snapshot.Nodes.Sum(n => n.HashPower));
            var validatedNodeCount = Math.Max(1, lastAudit?.Tips.Sum(t => t.NodeIds.Count) ?? 0);

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
                BlocksWon = winCounts.GetValueOrDefault(n.Id, 0)
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
                Forks = (lastAudit?.Tips ?? new List<TipGroup>()).Select(t => new DashboardFork
                {
                    TipHash = t.TipHash,
                    Height = t.Height,
                    NodeCount = t.NodeIds.Count,
                    Share = (double)t.NodeIds.Count / validatedNodeCount,
                    NodeIds = t.NodeIds
                }).ToList(),
                RecentReorganizations = watcherStore.GetRecentReorganizations(10).Select(r => new DashboardReorg
                {
                    Timestamp = r.Timestamp,
                    NodeId = r.NodeId,
                    Reason = r.Reason
                }).ToList(),
                Scenario = BuildScenarioSummary(scenarioRuntime)
            };

            return JsonSerializer.Serialize(summary, JsonOptions);
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
            public List<string> NodeIds { get; init; } = new();
        }

        private sealed class DashboardReorg
        {
            public string Timestamp { get; init; } = "";
            public string NodeId { get; init; } = "";
            public string Reason { get; init; } = "";
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
  .row .rank { color: var(--text-dim); font-variant-numeric: tabular-nums; }
  .row .id { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .row .num { text-align: right; font-variant-numeric: tabular-nums; color: var(--text-dim); }
  .bar-track { height: 8px; background: var(--bar-bg); border-radius: 4px; overflow: hidden; }
  .bar-fill { height: 100%; background: var(--accent); border-radius: 4px; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--panel-border); white-space: nowrap; }
  th { color: var(--text-dim); font-weight: 500; }
  td.id { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
  .role-honest { color: var(--good); }
  .role-other { color: var(--warn); }
  .full-width { grid-column: 1 / -1; }
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
      <h2>All participants</h2>
      <div style=""max-height:420px; overflow-y:auto;"">
        <table>
          <thead><tr>
            <th>Node</th><th>Role</th><th>Mines</th><th>Hash power</th>
            <th>Pool</th><th>Blocks won</th><th>Peers</th><th>Economic weight</th>
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

function renderAllNodes(nodes) {
  var tbody = document.getElementById('all-nodes');
  if (!nodes.length) { tbody.innerHTML = '<tr><td colspan=""8"" class=""empty"">No nodes yet.</td></tr>'; return; }
  tbody.innerHTML = nodes.map(function (n) {
    var roleClass = n.role === 'Honest' ? 'role-honest' : 'role-other';
    return '<tr>' +
      '<td class=""id"">' + n.id + '</td>' +
      '<td class=""' + roleClass + '"">' + n.role + '</td>' +
      '<td>' + (n.canMine ? 'yes' : 'no') + '</td>' +
      '<td>' + n.hashPower + ' (' + fmtPct(n.hashPowerShare) + ')</td>' +
      '<td>' + (n.pool || '(solo)') + '</td>' +
      '<td>' + n.blocksWon + '</td>' +
      '<td>' + n.peerCount + '</td>' +
      '<td>' + n.economicWeight + '</td>' +
      '</tr>';
  }).join('');
}

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
    var pct = Math.max(f.share * 100, 1.5);
    var title = f.tipHash + '\n' + f.nodeIds.join(', ');
    html += '<div class=""row"" title=""' + title + '"">' +
      '<span class=""rank"">#' + (i + 1) + '</span>' +
      '<span class=""id"">' + f.tipHash.slice(0, 8) + '… @' + f.height + '</span>' +
      '<span class=""bar-track""><span class=""bar-fill"" style=""width:' + pct + '%""></span></span>' +
      '<span class=""num"">' + f.nodeCount + ' node(s)</span>' +
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
      var bits = [g.count + '&times; ' + escapeHtml(g.role)];
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
  fetch('summary').then(function (r) { return r.json(); }).then(function (s) {
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
    renderAllNodes(s.allNodes);
  }).catch(function (err) { console.error('dashboard refresh failed', err); });
}

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>";
    }
}
