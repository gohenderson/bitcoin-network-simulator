// BitcoinNetworkSimulator: a single-process simulation of a Bitcoin-style
// P2P network — real proof-of-work, gossip/reorg, coin issuance, balance
// enforcement, mining pools, signed blocks, and a handful of deliberately
// malicious node roles. See README.md for how the whole system works and
// how to run it; the mechanism-specific comments that used to live here now
// live next to the code they explain (ProofOfWork, Economics, Ledger,
// Blockchain.ValidateChain, and the MINING POOLS / BUILTBY SIGNING notes in
// Miner.cs).

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
    // per-node persistence — all as async Tasks — then waits for the user
    // (or a scenario's duration) to stop the run. There is no mining
    // coordinator: with a public, deterministically-derived target,
    // nothing needs one.
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

        public static async Task Main(string[] args)
        {
            Console.WriteLine("=== BitcoinNetworkSimulator ===");

            // A scenario file governs this run's starting node population,
            // growth behavior, and duration — see "Scenarios" in README.md.
            // `dotnet run -- path/to/scenario.json` picks a
            // specific file; otherwise scenario.json next to the executable
            // is used if present. No file at all means the normal
            // single-node, indefinite-runtime default, unchanged from before
            // this feature existed.
            var scenarioPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "scenario.json");
            var scenario = await ScenarioLoader.LoadAsync(scenarioPath);

            // Every run's node folders and watcher.db land under
            // their own timestamped ScenarioResults/ subfolder from this
            // point on — see "Scenarios" in README.md.
            RunRootDir = ScenarioLoader.DetermineRunRootDir(scenarioPath, scenario);
            Console.WriteLine($"Results: {RunRootDir}\n");

            var effectiveMaxNodes = scenario?.MaxNodes ?? NodeNetwork.DefaultMaxNodes;
            var effectiveGrowthIntervalMs = scenario?.GrowthIntervalSeconds is int gis ? gis * 1000 : NodeNetwork.DefaultGrowthIntervalMs;
            var autoGrowthEnabled = scenario?.AutoGrowth ?? true;

            if (scenario != null)
            {
                if (!string.IsNullOrWhiteSpace(scenario.Description))
                    Console.WriteLine($"Scenario: {scenario.Description}");
                await NodeMetadataStore.ApplyScenarioAsync(RunRootDir, scenario, NodeNetwork.NodeNameFor);
            }
            else
            {
                Console.WriteLine($"Dynamic network: starts at 1 node, roughly doubles every {effectiveGrowthIntervalMs / 1000} s (cap: {effectiveMaxNodes}).");
            }
            Console.WriteLine("Mining is round-robin across active nodes — no per-node background threads.");
            Console.WriteLine("Real proof-of-work: a public, deterministically-derived target.\n");

            var cts = new CancellationTokenSource();
            using var watcherStore = new WatcherStore(Path.Combine(RunRootDir, "watcher.db"), Port, scenario != null ? scenarioPath : null, scenario?.Description);
            var watcher = new ChainWatcher(Port, new List<string>(), watcherStore);

            var network = new NodeNetwork(RunRootDir, Port);

            // One shared listener for the whole network — see
            // NetworkServer.cs — dispatching every request by the node id in
            // its URL path (network.ResolveNode looks it up) rather than
            // each node owning its own OS-level port.
            var server = new NetworkServer(Port, network.ResolveNode);
            server.Start();

            await NodeMetadataStore.PreloadKnownSigningKeysAsync(RunRootDir);

            var initialNodeCount = scenario?.NodeGroups.Count > 0 ? scenario.NodeGroups.Sum(g => g.Count) : 1;
            for (var i = 0; i < initialNodeCount; i++)
                await network.AddNodeAsync(watcher, cts.Token);

            var miningTask = Task.Factory.StartNew(
                async () => await MiningScheduler.RunAsync(network, cts.Token),
                cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            var txTask = TransactionGenerator.RunAsync(network, Port, cts.Token);
            var growthTask = autoGrowthEnabled
                ? network.GrowthLoopAsync(watcher, cts.Token, effectiveMaxNodes, effectiveGrowthIntervalMs)
                : Task.CompletedTask;
            var watcherTask = watcher.RunAsync(cts.Token);

            Console.WriteLine($"All nodes share http://localhost:{Port}/ — address one by id in the path." +
                (autoGrowthEnabled ? " Network grows automatically." : " Auto-growth disabled — network stays fixed."));
            Console.WriteLine($"Try: curl http://localhost:{Port}/{NodeNetwork.NodeNameFor(0)}/chain");
            Console.WriteLine($"Or:  curl http://localhost:{Port}/{NodeNetwork.NodeNameFor(0)}/balances");
            Console.WriteLine("Watcher: inspect watcher.db (SQLite) for convergence/recovery history.");

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
            server.Stop();

            try
            {
                await Task.WhenAll(
                    new[] { miningTask, txTask, growthTask, watcherTask }
                    .Concat(network.SnapshotPersistTasks()));
            }
            catch (OperationCanceledException) { }

            foreach (var store in network.SnapshotBlockchainStores()) store.Dispose();

            Console.WriteLine("Stopped.");
        }
    }
}
