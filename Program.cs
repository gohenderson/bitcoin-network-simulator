using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    /// <summary>
    /// The composition root: wires up a <see cref="NodeNetwork"/>, a
    /// <see cref="MiningScheduler"/>, a <see cref="TransactionGenerator"/>, and per-node
    /// persistence, then walks a scenario file's phases in order, each phase getting its own
    /// growth/churn settings and node groups, until the last phase's duration elapses or the
    /// user stops the run.
    /// </summary>
    public static class Program
    {
        private const int Port = 5000;

        private static string RunRootDir = AppContext.BaseDirectory;

        /// <summary>
        /// Resolved growth/churn settings currently in effect, threaded through the phase
        /// loop. Each phase's non-null fields override the previous phase's resolved values;
        /// a null field inherits.
        /// </summary>
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

            var scenarioPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "scenario.yaml");
            var loadedScenarioFile = await ScenarioLoader.LoadAsync(scenarioPath);
            var phases = loadedScenarioFile?.Phases ?? new List<Scenario> { new Scenario() };

            RunRootDir = ScenarioLoader.DetermineRunRootDir(scenarioPath, loadedScenarioFile);
            Console.WriteLine($"Results: {RunRootDir}\n");
            Console.WriteLine("Mining is round-robin across active nodes — no per-node background threads.");
            Console.WriteLine("Real proof-of-work: a public, deterministically-derived target.\n");

            var cts = new CancellationTokenSource();
            var combinedDescription = string.Join(" -> ", phases.Select(p => p.Description).Where(d => !string.IsNullOrWhiteSpace(d)));
            using var watcherStore = new WatcherStore(Path.Combine(RunRootDir, "watcher.db"), Port, loadedScenarioFile != null ? scenarioPath : null, combinedDescription.Length > 0 ? combinedDescription : null);
            var scenarioRuntime = new ScenarioRuntimeInfo(loadedScenarioFile != null ? scenarioPath : null, combinedDescription.Length > 0 ? combinedDescription : null, phases);

            var settings = new GrowthSettings().ApplyPhase(phases[0]);
            var network = new NodeNetwork(RunRootDir, settings.OutboundPeerCount, settings.MaliciousFraction, settings.WalletOnlyFraction, loadedScenarioFile?.ResolvedDefaultRuleSchedule ?? new List<ResolvedDefaultRuleScheduleEntry>(), loadedScenarioFile?.DebasementRatePerBlock ?? 0m);
            var watcher = new ChainWatcher(network.DispatchInternalAsync, new List<string>(), watcherStore);

            var server = new NetworkServer(Port, network.ResolveNode,
                (ctx, route) => Dashboard.HandleAsync(ctx, route, network, watcherStore, watcher, scenarioRuntime));
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
            Console.WriteLine("Watcher: inspect watcher.db (SQLite) for convergence/recovery history.");
            Console.WriteLine($"Dashboard: http://localhost:{Port}/dashboard/ — participants, top miners, pools, peer-graph influence.\n");

            var enterTask = Task.Run(() => Console.ReadLine());

            var enterPressed = false;
            var growthTask = Task.CompletedTask;
            var churnTask = Task.CompletedTask;
            CancellationTokenSource? phaseCts = null;

            for (var phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
            {
                var phase = phases[phaseIndex];
                var isLastPhase = phaseIndex == phases.Count - 1;
                scenarioRuntime.SetCurrentPhase(phaseIndex);
                if (phaseIndex > 0)
                    settings = settings.ApplyPhase(phase);

                if (!string.IsNullOrWhiteSpace(phase.Description))
                    Console.WriteLine($"Scenario phase {phaseIndex + 1}/{phases.Count}: {phase.Description}");

                network.SetNodeCreationSettings(settings.OutboundPeerCount, settings.MaliciousFraction, settings.WalletOnlyFraction);

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

            phaseCts?.Dispose();

            foreach (var store in network.SnapshotBlockchainStores()) store.Dispose();

            Console.WriteLine("Stopped.");
        }
    }
}
