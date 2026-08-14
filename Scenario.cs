using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Declarative description of how a run should start up and how long it
    // should last — see "Scenarios" in README.md. Loaded
    // once at startup (see ScenarioLoader.LoadAsync); Program is responsible
    // for turning this into actual persisted node metadata and startup
    // behavior — see NodeMetadataStore.ApplyScenarioAsync. Deliberately a
    // plain data model with no dependency on Program's internals, so this
    // file can be read in isolation to understand the whole format.
    // ------------------------------------------------------------------
    public class Scenario
    {
        // Purely informational — echoed to the console when the scenario
        // loads, so a scenario file is self-explanatory without needing a
        // separate README to cross-reference.
        public string? Description { get; set; }

        // How long this run should last before automatically shutting down,
        // exactly as if Enter had been pressed. Null/omitted means no
        // automatic stop — waits indefinitely for Enter, same as running
        // with no scenario at all.
        public int? DurationSeconds { get; set; }

        // Whether the network keeps growing organically (see
        // NodeNetwork.GrowthLoopAsync) on top of the nodes NodeGroups create
        // up front. Defaults to true, matching behavior with no scenario at
        // all; set false to freeze the network at exactly the node count
        // NodeGroups add up to, for this run's whole duration.
        public bool AutoGrowth { get; set; } = true;

        // Overrides for organic growth's pacing/rate/cap — only consulted
        // when AutoGrowth is true. Null means "use NodeNetwork's built-in
        // defaults" (DefaultGrowthIntervalMs, DefaultGrowthRate, DefaultMaxNodes).
        public int? GrowthIntervalSeconds { get; set; }

        // Multiplier applied to the current node count each growth tick —
        // see NodeNetwork.GrowthLoopAsync. 2.0 (the default) doubles the
        // network every tick; 1.5 adds 50% more nodes per tick. A value at
        // or below 1.0 stalls growth entirely — the tick keeps firing but
        // never adds a node, so the loop only exits via cancellation.
        public double? GrowthRate { get; set; }

        // Random +/- range applied to GrowthIntervalSeconds on every tick, so
        // growth doesn't land on a perfectly metronomic schedule — see
        // NodeNetwork.GrowthLoopAsync. Null/0 means no jitter (exactly
        // GrowthIntervalSeconds every tick, the pre-existing behavior).
        public double? GrowthJitterSeconds { get; set; }

        // Floor the network tops up to — one node per tick, ignoring
        // GrowthRate — before exponential growth-rate scaling takes over.
        // Null/0 means no floor: growth-rate scaling applies from the very
        // first tick, the pre-existing behavior. Useful when NodeGroups (or
        // the single-node default start) seed fewer nodes than you want
        // established before the network starts compounding.
        public int? GrowthMinSeedNodes { get; set; }

        public int? MaxNodes { get; set; }

        // How many outbound peers each node picks at creation — see the
        // "Peer topology" note in README.md. Null means
        // NodeNetwork.DefaultOutboundPeerCount (8, matching real Bitcoin).
        public int? OutboundPeerCount { get; set; }

        // Fraction of newly-created nodes (both the initial dynamic-start
        // node and every node organic growth adds) assigned a malicious role
        // instead of Honest, cycling through the four malicious types in
        // order — see NodeNetwork.AssignRole. Null means
        // NodeNetwork.DefaultMaliciousFraction (0.5, the pre-existing
        // index%8 behavior). Only affects nodes with no metadata.json yet;
        // NodeGroups-authored nodes always use their own Role.
        public double? GrowthMaliciousFraction { get; set; }

        // Fraction of newly-created nodes assigned wallet-only (CanMine
        // false) instead of mining-capable — see NodeNetwork.AssignCanMine.
        // Null means NodeNetwork.DefaultWalletOnlyFraction (1/3, the
        // pre-existing index%3 behavior). Same "no metadata.json yet" scope
        // as GrowthMaliciousFraction.
        public double? GrowthWalletOnlyFraction { get; set; }

        // Node churn — nodes leaving the live network, the counterpart to
        // organic growth — see NodeNetwork.ChurnLoopAsync. Independent of
        // AutoGrowth: churn runs whenever ChurnRate is set above 0, whether
        // or not the network is also growing. Null/0 ChurnRate means no
        // churn at all (the pre-existing behavior — nodes never used to
        // leave). ChurnIntervalSeconds null means
        // NodeNetwork.DefaultChurnIntervalMs.
        public int? ChurnIntervalSeconds { get; set; }

        // Fraction of the current node count removed each churn tick
        // (floored — a low rate on a small network simply skips removal
        // that tick rather than over-shrinking it). Null means
        // NodeNetwork.DefaultChurnRate (0.0, disabled).
        public double? ChurnRate { get; set; }

        // Floor churn will never shrink the network below. Null means
        // NodeNetwork.DefaultChurnMinNodes (1 — mining needs at least one
        // node to make progress).
        public int? ChurnMinNodes { get; set; }

        // Each entry describes Count identically-configured nodes to create
        // up front, applied in the order listed — e.g. a group of 10 plain
        // nodes followed by a group of 5 nodes in "poolA" creates 15 nodes
        // total: the first 10 get NodeNameFor(0..9), the pooled 5 get
        // NodeNameFor(10..14), matching how node identity is already
        // assigned positionally. An empty list means "no scenario-defined
        // nodes" — Program falls back to its normal single-node start.
        public List<ScenarioNodeGroup> NodeGroups { get; set; } = new();
    }

    // One group of Count nodes sharing a starting configuration — the same
    // fields NodeMetadata carries, minus Id (assigned positionally by
    // ApplyScenarioAsync) and SigningKey (never scenario-authored: an
    // existing identity at a given position is preserved rather than
    // overwritten, so re-running the same scenario keeps building on the
    // same node identities and chain history instead of resetting to
    // genesis every time — see NodeMetadataStore.ApplyScenarioAsync).
    public class ScenarioNodeGroup
    {
        public int Count { get; set; } = 1;
        public NodeRole Role { get; set; } = NodeRole.Honest;
        public int HashPower { get; set; } = 1;
        public bool CanMine { get; set; } = true;
        public string? Pool { get; set; } = null;
        // See NodeMetadata.EconomicWeight and the "Peer topology" note in
        // README.md. 1 is an ordinary node; higher values make this group's
        // nodes proportionally more likely to be picked as another node's
        // outbound peer, turning them into structural hubs.
        public int EconomicWeight { get; set; } = 1;
    }

    public static class ScenarioLoader
    {
        // NodeRole serialized by name, same as NodeMetadata, so a scenario
        // file reads the same way metadata.json does.
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // Returns null if `path` doesn't exist (no scenario — start
        // normally) or fails to parse (logged, then treated the same as
        // absent, so a typo'd scenario file can't crash startup).
        public static async Task<Scenario?> LoadAsync(string path)
        {
            if (!File.Exists(path)) return null;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var scenario = JsonSerializer.Deserialize<Scenario>(json, Options);
                if (scenario == null)
                {
                    Console.WriteLine($"[scenario] {path} parsed to nothing; ignoring, starting normally");
                    return null;
                }
                return scenario;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[scenario] failed to read {path}: {ex.Message}; ignoring, starting normally");
                return null;
            }
        }

        private static string SanitizeForFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        // Root directory for this run's node folders and watcher.db —
        // ScenarioResults/<timestamp>-<scenario name, or "no-scenario">/,
        // computed once at startup before anything else happens, so every
        // run's artifacts land in their own timestamped, reviewable folder
        // instead of always overwriting the same nodes/ next to the
        // executable. Also copies the exact scenario file that was executed
        // into the new result folder (unmodified, same filename) when one
        // was used, so the folder is a self-contained record of both what
        // happened and exactly what configuration produced it — no need to
        // go find Scenarios/whatever.json separately, which may have since
        // been edited or deleted. See "Scenarios" in README.md.
        public static string DetermineRunRootDir(string scenarioPath, Scenario? scenario)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            var label = scenario != null
                ? SanitizeForFileName(Path.GetFileNameWithoutExtension(scenarioPath))
                : "no-scenario";
            var dir = Path.Combine(AppContext.BaseDirectory, "ScenarioResults", $"{timestamp}-{label}");
            Directory.CreateDirectory(dir);

            if (scenario != null)
            {
                try
                {
                    File.Copy(scenarioPath, Path.Combine(dir, Path.GetFileName(scenarioPath)), overwrite: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[scenario] failed to copy {scenarioPath} into {dir}: {ex.Message}");
                }
            }

            return dir;
        }
    }
}
