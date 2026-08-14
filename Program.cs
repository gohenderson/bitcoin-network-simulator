// BitcoinNetworkSimulator: a single-process simulation of a Bitcoin-style
// P2P network — real proof-of-work, gossip/reorg, coin issuance, balance
// enforcement, mining pools, signed blocks, and a handful of deliberately
// malicious node roles. See README.md for how the whole system works and
// how to run it; the mechanism-specific comments live next to the code they
// explain (ProofOfWork, Economics, Ledger, Blockchain.ValidateChain, and the
// MINING POOLS / BUILTBY SIGNING notes in Miner.cs).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Program: the composition root. Wires up a NodeNetwork (node
    // registry/creation/growth), a MiningScheduler (round-robin turns
    // across it), a TransactionGenerator (synthetic traffic), and
    // per-node persistence — all as async Tasks — then walks a scenario
    // file's phases in order (see "Scenarios" in README.md), each phase
    // getting its own growth/churn settings and NodeGroups, until the last
    // phase's duration elapses or the user (or a phase's duration) stops
    // the run. There is no mining coordinator: with a public,
    // deterministically-derived target, nothing needs one.
    // ------------------------------------------------------------------

    public static class Program
    {
        // Every node in the network is reachable through this one shared
        // port — see NetworkServer.cs — addressed by node id in the URL
        // path (e.g. http://localhost:5000/000-alpha/chain) rather than one
        // real OS port per node.
        private const int Port = 5000;

        // Root directory for this run's node folders and watcher.db —
        // see ScenarioLoader.DetermineRunRootDir.
        private static string RunRootDir = AppContext.BaseDirectory;

        // Resolved growth/churn settings currently in effect, threaded
        // through the phase loop below. Each phase's non-null fields
        // override the previous phase's resolved values (null inherits) —
        // see the "field inheritance" comment atop Scenario.cs. Starts at
        // NodeNetwork's own Default* constants, exactly what a phase 0 that
        // sets nothing at all would resolve to.
        private sealed class GrowthSettings
        {
            public bool AutoGrowth = true;
            public int MaxNodes = NodeNetwork.DefaultMaxNodes;
            public int GrowthIntervalMs = NodeNetwork.DefaultGrowthIntervalMs;
            public double GrowthRate = NodeNetwork.DefaultGrowthRate;
            public int GrowthJitterMs = NodeNetwork.DefaultGrowthJitterMs;
            public int GrowthMinSeedNodes = NodeNetwork.DefaultGrowthMinSeedNodes;
            public int OutboundPeerCount = NodeNetwork.DefaultOutboundPeerCount;
            public double MaliciousFraction = NodeNetwork.DefaultMaliciousFraction;
            public double WalletOnlyFraction = NodeNetwork.DefaultWalletOnlyFraction;
            public int ChurnIntervalMs = NodeNetwork.DefaultChurnIntervalMs;
            public double ChurnRate = NodeNetwork.DefaultChurnRate;
            public int ChurnMinNodes = NodeNetwork.DefaultChurnMinNodes;

            public GrowthSettings ApplyPhase(Scenario phase) => new()
            {
                AutoGrowth = phase.AutoGrowth ?? AutoGrowth,
                MaxNodes = phase.MaxNodes ?? MaxNodes,
                GrowthIntervalMs = phase.GrowthIntervalSeconds is int gis ? gis * 1000 : GrowthIntervalMs,
                GrowthRate = phase.GrowthRate ?? GrowthRate,
                GrowthJitterMs = phase.GrowthJitterSeconds is double gjs ? (int)(gjs * 1000) : GrowthJitterMs,
                GrowthMinSeedNodes = phase.GrowthMinSeedNodes ?? GrowthMinSeedNodes,
                OutboundPeerCount = phase.OutboundPeerCount ?? OutboundPeerCount,
                MaliciousFraction = phase.GrowthMaliciousFraction ?? MaliciousFraction,
                WalletOnlyFraction = phase.GrowthWalletOnlyFraction ?? WalletOnlyFraction,
                ChurnIntervalMs = phase.ChurnIntervalSeconds is int cis ? cis * 1000 : ChurnIntervalMs,
                ChurnRate = phase.ChurnRate ?? ChurnRate,
                ChurnMinNodes = phase.ChurnMinNodes ?? ChurnMinNodes,
            };
        }

        public static async Task Main(string[] args)
        {
            Console.WriteLine("=== BitcoinNetworkSimulator ===");

            // A scenario file governs this run's phases — starting node
            // population, growth/churn behavior, and duration for each — see
            // "Scenarios" in README.md. `dotnet run -- path/to/scenario.json`
            // picks a specific file; otherwise scenario.json next to the
            // executable is used if present. No file at all means a normal
            // single-node, indefinite-runtime default, modeled below as an
            // implicit single empty phase so the rest of Main only ever has
            // to deal with "a list of phases," never a special no-scenario case.
            var scenarioPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "scenario.json");
            var loadedPhases = await ScenarioLoader.LoadAsync(scenarioPath);
            var phases = loadedPhases ?? new List<Scenario> { new Scenario() };

            // Every run's node folders and watcher.db land under
            // their own timestamped ScenarioResults/
            // subfolder — see "Scenarios" in README.md.
            RunRootDir = ScenarioLoader.DetermineRunRootDir(scenarioPath, loadedPhases);
            Console.WriteLine($"Results: {RunRootDir}\n");
            Console.WriteLine("Mining is round-robin across active nodes — no per-node background threads.");
            Console.WriteLine("Real proof-of-work: a public, deterministically-derived target.\n");

            var cts = new CancellationTokenSource();
            var combinedDescription = string.Join(" -> ", phases.Select(p => p.Description).Where(d => !string.IsNullOrWhiteSpace(d)));
            using var watcherStore = new WatcherStore(Path.Combine(RunRootDir, "watcher.db"), Port, loadedPhases != null ? scenarioPath : null, combinedDescription.Length > 0 ? combinedDescription : null);
            var watcher = new ChainWatcher(Port, new List<string>(), watcherStore);

            var settings = new GrowthSettings().ApplyPhase(phases[0]);
            var network = new NodeNetwork(RunRootDir, Port, settings.OutboundPeerCount, settings.MaliciousFraction, settings.WalletOnlyFraction);

            // One shared listener for the whole network — see
            // NetworkServer.cs — dispatching every request by the node id in
            // its URL path (network.ResolveNode looks it up) rather than
            // each node owning its own OS-level port.
            var server = new NetworkServer(Port, network.ResolveNode);
            server.Start();

            await NodeMetadataStore.PreloadKnownSigningKeysAsync(RunRootDir);

            var miningTask = Task.Factory.StartNew(
                async () => await MiningScheduler.RunAsync(network, cts.Token),
                cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            var txTask = TransactionGenerator.RunAsync(network, Port, cts.Token);
            var watcherTask = watcher.RunAsync(cts.Token);

            Console.WriteLine($"All nodes share http://localhost:{Port}/ — address one by id in the path.");
            Console.WriteLine($"Try: curl http://localhost:{Port}/{NodeNetwork.NodeNameFor(0)}/chain");
            Console.WriteLine($"Or:  curl http://localhost:{Port}/{NodeNetwork.NodeNameFor(0)}/balances");
            Console.WriteLine("Watcher: inspect watcher.db (SQLite) for convergence/recovery history.\n");

            // One Enter-press stops the whole run, from any phase — reused
            // (not re-created) across every phase's wait below, so a
            // still-pending read from an earlier phase is exactly what
            // resolves a later phase's Task.WhenAny the moment Enter lands.
            var enterTask = Task.Run(() => Console.ReadLine());

            var enterPressed = false;
            var growthTask = Task.CompletedTask;
            var churnTask = Task.CompletedTask;
            CancellationTokenSource? phaseCts = null;

            for (var phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
            {
                var phase = phases[phaseIndex];
                var isLastPhase = phaseIndex == phases.Count - 1;
                // settings already reflects phase 0 (resolved above, before
                // the network needed it at construction time) — applying it
                // again here would be a harmless no-op, but skipping it
                // keeps this the one place phase 0 gets resolved.
                if (phaseIndex > 0)
                    settings = settings.ApplyPhase(phase);

                if (!string.IsNullOrWhiteSpace(phase.Description))
                    Console.WriteLine($"Scenario phase {phaseIndex + 1}/{phases.Count}: {phase.Description}");

                network.SetNodeCreationSettings(settings.OutboundPeerCount, settings.MaliciousFraction, settings.WalletOnlyFraction);

                // Phase 0 with no NodeGroups falls back to the original
                // single-node default start; any other phase with no
                // NodeGroups simply adds nothing explicit this phase — see
                // the NodeGroups comment on Scenario.
                var groups = phase.NodeGroups.Count > 0 || phaseIndex > 0
                    ? phase.NodeGroups
                    : new List<ScenarioNodeGroup> { new ScenarioNodeGroup() };
                var addedCount = 0;
                foreach (var group in groups)
                {
                    for (var i = 0; i < group.Count; i++)
                    {
                        await network.AddNodeAsync(watcher, cts.Token, phase.NodeGroups.Count > 0 ? group : null);
                        addedCount++;
                    }
                }

                var churnEnabled = settings.ChurnRate > 0;
                var durationNote = phase.DurationSeconds is int d && d > 0
                    ? $"{d}s"
                    : (isLastPhase ? "no automatic stop" : "no automatic stop — later phases will never activate!");
                Console.WriteLine($"[scenario] phase {phaseIndex + 1}/{phases.Count}: added {addedCount} node(s), duration {durationNote}, autoGrowth={settings.AutoGrowth}, churn={(churnEnabled ? $"{settings.ChurnRate:P0} every {settings.ChurnIntervalMs / 1000}s (floor {settings.ChurnMinNodes})" : "off")}");

                // End the previous phase's growth/churn loops before
                // starting this phase's — see the field-by-field comment on
                // GrowthSettings for why these can differ phase to phase.
                phaseCts?.Cancel();
                await Task.WhenAll(growthTask, churnTask);
                phaseCts?.Dispose();

                phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                growthTask = settings.AutoGrowth
                    ? network.GrowthLoopAsync(watcher, phaseCts.Token, settings.MaxNodes, settings.GrowthIntervalMs, settings.GrowthRate, settings.GrowthJitterMs, settings.GrowthMinSeedNodes)
                    : Task.CompletedTask;
                churnTask = churnEnabled
                    ? network.ChurnLoopAsync(watcher, phaseCts.Token, settings.ChurnIntervalMs, settings.ChurnRate, settings.ChurnMinNodes)
                    : Task.CompletedTask;

                if (phase.DurationSeconds is int durationSeconds && durationSeconds > 0)
                {
                    var winner = await Task.WhenAny(enterTask, Task.Delay(TimeSpan.FromSeconds(durationSeconds)));
                    if (winner == enterTask) { enterPressed = true; break; }
                }
                else
                {
                    await enterTask;
                    enterPressed = true;
                    break;
                }
            }

            Console.WriteLine(enterPressed ? "\nStopping." : "\nFinal phase duration elapsed — stopping.");

            cts.Cancel();
            server.Stop();

            try
            {
                await Task.WhenAll(
                    new[] { miningTask, txTask, growthTask, churnTask, watcherTask }
                    .Concat(network.SnapshotPersistTasks()));
            }
            catch (OperationCanceledException) { }

            // Only now, after growthTask/churnTask are fully done, is it
            // safe to dispose the CancellationTokenSource their token
            // derived from — disposing any earlier risks a race with
            // whichever of them was still unwinding from cts.Cancel().
            phaseCts?.Dispose();

            foreach (var store in network.SnapshotBlockchainStores()) store.Dispose();

            Console.WriteLine("Stopped.");
        }
    }
}
