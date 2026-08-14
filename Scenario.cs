using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Declarative description of one phase of a run — see "Scenarios" in
    // README.md. A scenario *file* is a YAML list of these, applied in
    // order: phase 0's settings and NodeGroups take effect immediately,
    // and each later phase's settings/NodeGroups take over once the
    // previous phase's DurationSeconds elapses — letting a single run
    // model a network changing over time (e.g. a slow-growth early era
    // followed by a high-growth, pool-dominated later one) instead of
    // being fixed for its whole duration. Any field a phase leaves null
    // inherits whatever the previous phase had in effect (or NodeNetwork's
    // built-in default, for phase 0) — a phase only needs to state what's
    // actually changing. Loaded once at startup (see ScenarioLoader.LoadAsync);
    // Program is responsible for turning this into actual persisted node
    // metadata and runtime behavior — see NodeMetadataStore.LoadOrCreateFromGroupAsync.
    // Deliberately a plain data model with no dependency on Program's
    // internals, so this file can be read in isolation to understand the
    // whole format.
    // ------------------------------------------------------------------
    public class Scenario
    {
        // Purely informational — echoed to the console when this phase
        // becomes active, so a scenario file is self-explanatory without
        // needing a separate README to cross-reference.
        public string? Description { get; set; }

        // How long this phase lasts before the next one in the array takes
        // over, exactly as if Enter had been pressed and the run restarted
        // with the next phase's settings. For the *last* phase, this instead
        // means how long the whole run lasts before automatically shutting
        // down. Null/omitted on the last phase means no automatic stop —
        // waits indefinitely for Enter, same as running with no scenario at
        // all. Null/omitted on any earlier phase means that phase never
        // ends on its own — later phases in the array never activate — so
        // every non-last phase should set this.
        public int? DurationSeconds { get; set; }

        // Whether the network keeps growing organically (see
        // NodeNetwork.GrowthLoopAsync) on top of whatever NodeGroups create.
        // Null inherits the previous phase's value (true for phase 0,
        // matching behavior with no scenario at all); set false to freeze
        // the network at its current node count for this phase's duration.
        public bool? AutoGrowth { get; set; }

        // Overrides for organic growth's pacing/rate/cap — only consulted
        // when AutoGrowth is true. Null inherits the previous phase's value
        // (NodeNetwork's built-in defaults — DefaultGrowthIntervalMs,
        // DefaultGrowthRate, DefaultMaxNodes — for phase 0).
        public int? GrowthIntervalSeconds { get; set; }

        // Multiplier applied to the current node count each growth tick —
        // see NodeNetwork.GrowthLoopAsync. 2.0 (the default) doubles the
        // network every tick; 1.5 adds 50% more nodes per tick. A value at
        // or below 1.0 stalls growth entirely — the tick keeps firing but
        // never adds a node, so the loop only exits via cancellation.
        public double? GrowthRate { get; set; }

        // Random +/- range applied to GrowthIntervalSeconds on every tick, so
        // growth doesn't land on a perfectly metronomic schedule — see
        // NodeNetwork.GrowthLoopAsync. Null inherits the previous phase's
        // value (0 for phase 0 — no jitter).
        public double? GrowthJitterSeconds { get; set; }

        // Floor the network tops up to — one node per tick, ignoring
        // GrowthRate — before exponential growth-rate scaling takes over.
        // Null inherits the previous phase's value (0 for phase 0 — no
        // floor, growth-rate scaling applies from the very first tick).
        // Useful when this phase's NodeGroups (or the single-node default
        // start) seed fewer nodes than you want established before the
        // network starts compounding.
        public int? GrowthMinSeedNodes { get; set; }

        public int? MaxNodes { get; set; }

        // How many outbound peers each node picks at creation — see the
        // "Peer topology" note in README.md. Null inherits the previous
        // phase's value (NodeNetwork.DefaultOutboundPeerCount, 8, for
        // phase 0 — matching real Bitcoin).
        public int? OutboundPeerCount { get; set; }

        // Fraction of newly-created nodes (both the initial dynamic-start
        // node and every node organic growth adds during this phase)
        // assigned a malicious role instead of Honest, cycling through the
        // four malicious types in order — see NodeNetwork.AssignRole. Null
        // inherits the previous phase's value (NodeNetwork.DefaultMaliciousFraction,
        // 0.5, for phase 0). Only affects nodes with no metadata.json yet;
        // NodeGroups-authored nodes always use their own Role.
        public double? GrowthMaliciousFraction { get; set; }

        // Fraction of newly-created nodes assigned wallet-only (CanMine
        // false) instead of mining-capable — see NodeNetwork.AssignCanMine.
        // Null inherits the previous phase's value
        // (NodeNetwork.DefaultWalletOnlyFraction, 1/3, for phase 0). Same
        // "no metadata.json yet" scope as GrowthMaliciousFraction.
        public double? GrowthWalletOnlyFraction { get; set; }

        // Node churn — nodes leaving the live network, the counterpart to
        // organic growth — see NodeNetwork.ChurnLoopAsync. Independent of
        // AutoGrowth: churn runs whenever ChurnRate is above 0, whether or
        // not the network is also growing. Null inherits the previous
        // phase's value (0 for phase 0 — disabled, nodes never leave).
        public int? ChurnIntervalSeconds { get; set; }

        // Fraction of the current node count removed each churn tick
        // (floored — a low rate on a small network simply skips removal
        // that tick rather than over-shrinking it). Null inherits the
        // previous phase's value (NodeNetwork.DefaultChurnRate, 0.0, for
        // phase 0 — disabled).
        public double? ChurnRate { get; set; }

        // Floor churn will never shrink the network below. Null inherits
        // the previous phase's value (NodeNetwork.DefaultChurnMinNodes, 1,
        // for phase 0 — mining needs at least one node to make progress).
        public int? ChurnMinNodes { get; set; }

        // ------------------------------------------------------------------
        // Consensus economics/proof-of-work — UNLIKE every field above, these
        // are NOT per-phase and NOT inherited: every node uses
        // ProofOfWork.ComputeExpectedTargetHex / Economics.ComputeBlockReward
        // to independently re-derive the expected target/reward for EVERY
        // block, including historical ones, purely from a block's height and
        // these process-wide values — so changing one mid-run would make
        // already-mined blocks fail re-validation for any node that sees the
        // new value applied retroactively. Program resolves these ONCE, from
        // phase 0 only; setting any of them on a later phase is a
        // scenario-authoring mistake (logged as a warning, then ignored).
        // Null means the corresponding ProofOfWork/Economics Default*
        // constant — real Bitcoin's own numbers, except InitialDifficultyShift
        // (see its comment in Blockchain.cs for why that one deliberately
        // isn't).
        // ------------------------------------------------------------------
        public int? RetargetIntervalBlocks { get; set; }
        public double? TargetSecondsPerBlock { get; set; }
        public double? MinAdjustmentFactor { get; set; }
        public double? MaxAdjustmentFactor { get; set; }
        public int? InitialDifficultyShift { get; set; }
        public decimal? InitialBlockReward { get; set; }
        public int? HalvingIntervalBlocks { get; set; }
        public decimal? MaxSupply { get; set; }

        // Each entry describes Count identically-configured nodes to add
        // when this phase begins, applied in the order listed and added on
        // top of whatever nodes already exist from earlier phases — e.g. a
        // later phase modeling "mining pools emerge" might add a Pool-tagged
        // group without touching anything already running. An empty list
        // means "no explicit nodes this phase"; for phase 0 specifically,
        // an empty list also means Program falls back to its normal
        // single-node default start.
        public List<ScenarioNodeGroup> NodeGroups { get; set; } = new();
    }

    // One group of Count nodes sharing a starting configuration — the same
    // fields NodeMetadata carries, minus Id (assigned by NodeNetwork's
    // ever-increasing join counter) and SigningKey (never scenario-authored:
    // an existing identity at a given id is preserved rather than
    // overwritten, so re-running the same scenario keeps building on the
    // same node identities and chain history instead of resetting to
    // genesis every time — see NodeMetadataStore.LoadOrCreateFromGroupAsync).
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
        // NodeRole matched by its member name ("Honest", "Equivocator", ...)
        // — YamlDotNet's default enum handling, same idea as NodeMetadata's
        // JsonStringEnumConverter, so a scenario file reads the same way
        // metadata.json does. IgnoreUnmatchedProperties so an unrecognized
        // key (a typo, or a field from a differently-versioned scenario
        // file) is skipped rather than a hard failure — matches
        // System.Text.Json's default leniency, which this replaces.
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        // Returns null if `path` doesn't exist (no scenario — start
        // normally), the file is empty/parses to no phases, or it fails to
        // parse (all logged, then treated the same as absent, so a typo'd
        // scenario file can't crash startup). A scenario file is a YAML
        // list of phases — see the comment atop Scenario — not a single
        // mapping; a bare top-level mapping is a common enough mistake
        // (e.g. a single-phase file missing its leading `- `) to detect and
        // call out specifically rather than surfacing YamlDotNet's generic
        // deserialization error for it.
        public static async Task<List<Scenario>?> LoadAsync(string path)
        {
            if (!File.Exists(path)) return null;

            try
            {
                var yaml = await File.ReadAllTextAsync(path);

                if (Deserializer.Deserialize<object?>(yaml) is IDictionary<object, object>)
                {
                    Console.WriteLine($"[scenario] {path} is a single YAML mapping, but a scenario file must be a list of phases — prefix it with '- '; ignoring, starting normally");
                    return null;
                }

                var phases = Deserializer.Deserialize<List<Scenario>>(yaml);
                if (phases == null || phases.Count == 0)
                {
                    Console.WriteLine($"[scenario] {path} parsed to no phases; ignoring, starting normally");
                    return null;
                }
                return phases;
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
        // go find Scenarios/whatever.yaml separately, which may have since
        // been edited or deleted. See "Scenarios" in README.md.
        public static string DetermineRunRootDir(string scenarioPath, List<Scenario>? phases)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            var label = phases != null
                ? SanitizeForFileName(Path.GetFileNameWithoutExtension(scenarioPath))
                : "no-scenario";
            var dir = Path.Combine(AppContext.BaseDirectory, "ScenarioResults", $"{timestamp}-{label}");
            Directory.CreateDirectory(dir);

            if (phases != null)
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
