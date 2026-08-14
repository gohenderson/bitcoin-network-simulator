using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // A scenario *file*'s root shape — see "Scenarios" in README.md. Phases
    // is the run's timeline (see Scenario below); NodeRules is a flat,
    // named library of ConsensusRules a NodeGroup picks from by name (see
    // ScenarioNodeGroup.RulesName) rather than repeating the same block on
    // every group that happens to share it. Loaded once at startup, see
    // ScenarioLoader.LoadAsync — which also resolves every NodeGroup's
    // RulesName against this list before returning, so nothing downstream
    // ever has to look NodeRules up itself.
    // ------------------------------------------------------------------
    public class ScenarioFile
    {
        public List<Scenario> Phases { get; set; } = new();
        public List<NamedConsensusRules> NodeRules { get; set; } = new();
    }

    // A ConsensusRules with a Name, purely so a ScenarioNodeGroup can refer
    // to it from ScenarioFile.NodeRules instead of embedding the same 8
    // fields inline on every group that uses it. The Name exists only for
    // scenario-file authoring — see ScenarioLoader.LoadAsync's resolution
    // pass — and is never itself part of a Block's hashed payload; what
    // actually reaches NodeMetadata.Rules/Block.Rules is a plain
    // (unnamed) ConsensusRules, the fully-resolved values this pointed to
    // at load time.
    public class NamedConsensusRules : ConsensusRules
    {
        public string Name { get; set; } = "";
    }

    // ------------------------------------------------------------------
    // Declarative description of one phase of a run — see "Scenarios" in
    // README.md. ScenarioFile.Phases is a list of these, applied in
    // order: phase 0's settings and NodeGroups take effect immediately,
    // and each later phase's settings/NodeGroups take over once the
    // previous phase's DurationSeconds elapses — letting a single run
    // model a network changing over time (e.g. a slow-growth early era
    // followed by a high-growth, pool-dominated later one) instead of
    // being fixed for its whole duration. Any field a phase leaves null
    // inherits whatever the previous phase had in effect (or NodeNetwork's
    // built-in default, for phase 0) — a phase only needs to state what's
    // actually changing. Program is responsible for turning this into
    // actual persisted node metadata and runtime behavior — see
    // NodeMetadataStore.LoadOrCreateFromGroupAsync. Deliberately a plain
    // data model with no dependency on Program's internals, so this file
    // can be read in isolation to understand the whole format.
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
        // Shorthand for "this group follows one named ruleset for its whole
        // life" — sugar for RuleSchedule below with a single
        // { FromHeight: 0, RulesName } entry. Name of an entry in the
        // scenario file's top-level NodeRules list. See ConsensusRules' own
        // comment in Blockchain.cs for why this lives per-group (and,
        // ultimately, per-node-schedule) rather than as a single
        // scenario-wide setting. Null/omitted (and RuleSchedule also empty)
        // means ConsensusRules' own field defaults (real Bitcoin's own
        // numbers, except InitialDifficultyShift — see its comment in
        // Blockchain.cs for why that one deliberately isn't). Referencing a
        // name that isn't defined in NodeRules is a scenario-authoring
        // mistake (logged as a warning by ScenarioLoader.LoadAsync, then
        // treated the same as null). Ignored (with a warning) if
        // RuleSchedule is also set — use one or the other, not both.
        public string? RulesName { get; set; } = null;

        // This group's full timeline of which named ruleset is active at
        // which block height — e.g. real-bitcoin from height 0, switching to
        // a differently-named ruleset from height 6 on. See RuleSchedule's
        // own comment in Blockchain.cs for what this means for consensus:
        // another group (or another scenario file entirely) whose own
        // schedule agrees at a given height stays in sync with this one;
        // one that doesn't diverges — a real, simulated fork. Takes
        // precedence over RulesName if both are set. Each entry's RulesName
        // is resolved the same way RulesName above is.
        public List<ScenarioRuleScheduleEntry> RuleSchedule { get; set; } = new();

        // Populated by ScenarioLoader.LoadAsync's resolution pass, from
        // RulesName/RuleSchedule looked up against the scenario file's
        // NodeRules list — never itself part of the YAML shape (deliberately
        // not named "Rules" or "RuleSchedule": leftover fields from a
        // not-yet-migrated file would otherwise deserialize straight into a
        // same-named property here, silently bypassing resolution entirely
        // instead of just being dropped by IgnoreUnmatchedProperties like
        // any other stale field). This is what NodeNetwork.AddNodeAsync
        // actually reads.
        public List<RuleScheduleEntry> ResolvedRuleSchedule { get; set; } = new();
    }

    // One entry in a ScenarioNodeGroup.RuleSchedule — see
    // RuleScheduleEntry (Blockchain.cs) for the resolved, name-free runtime
    // equivalent this becomes after ScenarioLoader.LoadAsync's resolution
    // pass.
    public class ScenarioRuleScheduleEntry
    {
        public int FromHeight { get; set; } = 0;
        public string? RulesName { get; set; } = null;
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
        // mapping with a top-level Phases key (and, optionally, NodeRules)
        // — see ScenarioFile — not a bare list; a bare top-level list is
        // what pre-NodeRules scenario files looked like, common enough as a
        // migration mistake to detect and call out specifically rather than
        // surfacing YamlDotNet's generic deserialization error for it.
        //
        // Also resolves every NodeGroup's RulesName against NodeRules
        // before returning (see ScenarioNodeGroup.ResolvedRules), so nothing
        // downstream ever has to do that lookup itself.
        public static async Task<ScenarioFile?> LoadAsync(string path)
        {
            if (!File.Exists(path)) return null;

            try
            {
                var yaml = await File.ReadAllTextAsync(path);

                if (Deserializer.Deserialize<object?>(yaml) is List<object>)
                {
                    Console.WriteLine($"[scenario] {path} is a bare list of phases, but a scenario file must be a mapping with a top-level 'Phases:' key (see \"Scenarios\" in README.md); ignoring, starting normally");
                    return null;
                }

                var scenarioFile = Deserializer.Deserialize<ScenarioFile>(yaml);
                if (scenarioFile == null || scenarioFile.Phases.Count == 0)
                {
                    Console.WriteLine($"[scenario] {path} parsed to no phases; ignoring, starting normally");
                    return null;
                }

                ResolveNodeRules(path, scenarioFile);
                return scenarioFile;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[scenario] failed to read {path}: {ex.Message}; ignoring, starting normally");
                return null;
            }
        }

        // Builds a Name -> ConsensusRules lookup from scenarioFile.NodeRules
        // (last one wins on a duplicate Name, logged), then resolves every
        // phase's every NodeGroup's RuleSchedule/RulesName against it into
        // ResolvedRuleSchedule. RuleSchedule wins if both are set (logged).
        // A RulesName that isn't defined in NodeRules is a scenario-authoring
        // mistake, so it's logged rather than silently falling back; the
        // fallback for that entry (or for a group with neither field set)
        // is a plain `new ConsensusRules()` (real Bitcoin's own defaults).
        private static void ResolveNodeRules(string path, ScenarioFile scenarioFile)
        {
            var byName = new Dictionary<string, ConsensusRules>();
            foreach (var rules in scenarioFile.NodeRules)
            {
                if (string.IsNullOrWhiteSpace(rules.Name))
                {
                    Console.WriteLine($"[scenario] {path} has a NodeRules entry with no Name; ignoring it");
                    continue;
                }
                if (byName.ContainsKey(rules.Name))
                    Console.WriteLine($"[scenario] {path} defines NodeRules '{rules.Name}' more than once; the last one wins");
                byName[rules.Name] = rules;
            }

            ConsensusRules ResolveOne(string? rulesName)
            {
                if (rulesName == null) return new ConsensusRules();
                if (byName.TryGetValue(rulesName, out var rules)) return rules;
                Console.WriteLine($"[scenario] {path} references RulesName '{rulesName}', which isn't defined in NodeRules; using defaults");
                return new ConsensusRules();
            }

            foreach (var phase in scenarioFile.Phases)
            {
                foreach (var group in phase.NodeGroups)
                {
                    if (group.RuleSchedule.Count > 0)
                    {
                        if (group.RulesName != null)
                            Console.WriteLine($"[scenario] {path} has a NodeGroup with both RulesName and RuleSchedule set; RuleSchedule wins");

                        group.ResolvedRuleSchedule = group.RuleSchedule
                            .Select(entry => new RuleScheduleEntry { FromHeight = entry.FromHeight, Rules = ResolveOne(entry.RulesName) })
                            .ToList();
                    }
                    else
                    {
                        group.ResolvedRuleSchedule = new List<RuleScheduleEntry>
                        {
                            new RuleScheduleEntry { FromHeight = 0, Rules = ResolveOne(group.RulesName) }
                        };
                    }
                }
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
        public static string DetermineRunRootDir(string scenarioPath, ScenarioFile? scenarioFile)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            var label = scenarioFile != null
                ? SanitizeForFileName(Path.GetFileNameWithoutExtension(scenarioPath))
                : "no-scenario";
            var dir = Path.Combine(AppContext.BaseDirectory, "ScenarioResults", $"{timestamp}-{label}");
            Directory.CreateDirectory(dir);

            if (scenarioFile != null)
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
