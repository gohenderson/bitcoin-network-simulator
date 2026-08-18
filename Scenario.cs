using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace BitcoinNetworkSimulator
{
    /// <summary>
    /// A scenario file's root shape. <see cref="Phases"/> is the run's timeline;
    /// <see cref="NodeRules"/> is a flat, named library of <see cref="ConsensusRules"/> a
    /// node group picks from by name rather than repeating the same block on every group
    /// that shares it.
    /// </summary>
    public class ScenarioFile
    {
        public List<Scenario> Phases { get; set; } = new();
        public List<NamedConsensusRules> NodeRules { get; set; } = new();

        /// <summary>
        /// What <see cref="RuleScheduleEntry"/> list a brand-new organically-grown node gets
        /// — one never authored by any node group. Whole-run, not per-phase. Empty (the
        /// default) means every organically-grown node gets <see cref="ConsensusRules"/>'
        /// own defaults.
        /// </summary>
        public List<ScenarioDefaultRuleScheduleEntry> DefaultRuleSchedule { get; set; } = new();

        /// <summary>
        /// Populated by <see cref="ScenarioLoader.LoadAsync"/>'s resolution pass, from
        /// <see cref="DefaultRuleSchedule"/>'s name pointers looked up against
        /// <see cref="NodeRules"/>. This is what <see cref="NodeNetwork"/> actually reads.
        /// </summary>
        public List<ResolvedDefaultRuleScheduleEntry> ResolvedDefaultRuleSchedule { get; set; } = new();

        /// <summary>
        /// Whole-run, file-wide $ debasement rate, compounded per block. Lives at the file
        /// root since every node's $ comparisons only make sense if they all share one
        /// currency. Default 0 disables it entirely.
        /// </summary>
        public decimal DebasementRatePerBlock { get; set; } = 0m;
    }

    /// <summary>
    /// One tranche in <see cref="ScenarioFile.DefaultRuleSchedule"/>: from
    /// <see cref="FromHeight"/> on, <see cref="RuleSchedules"/> is the full distribution a
    /// brand-new organically-grown node's own single, lifelong <see cref="ConsensusRules"/>
    /// is drawn from. A later tranche replaces the previous one outright once the network
    /// reaches it, rather than blending with it.
    /// </summary>
    public class ScenarioDefaultRuleScheduleEntry
    {
        public int FromHeight { get; set; } = 0;
        public List<ScenarioDefaultRuleScheduleOption> RuleSchedules { get; set; } = new();
    }

    /// <summary>
    /// One weighted option within a <see cref="ScenarioDefaultRuleScheduleEntry"/> tranche.
    /// <see cref="Percent"/> (0-100, not a 0-1 fraction) is the chance a brand-new
    /// organically-grown node gets <see cref="RulesName"/> as its own, single, lifelong
    /// ruleset, once its tranche's <c>FromHeight</c> has been reached. A tranche's percents
    /// are summed against a 100-point pool, in list order; whatever's left unclaimed falls
    /// back to hardcoded <see cref="ConsensusRules"/> defaults.
    /// </summary>
    public class ScenarioDefaultRuleScheduleOption
    {
        public double Percent { get; set; } = 0;
        public string? RulesName { get; set; } = null;
    }

    /// <summary>The resolved, name-free runtime equivalent of <see cref="ScenarioDefaultRuleScheduleEntry"/>.</summary>
    public class ResolvedDefaultRuleScheduleEntry
    {
        public int FromHeight { get; set; } = 0;
        public List<ResolvedDefaultRuleScheduleOption> RuleSchedules { get; set; } = new();
    }

    /// <summary>The resolved, name-free runtime equivalent of <see cref="ScenarioDefaultRuleScheduleOption"/>.</summary>
    public class ResolvedDefaultRuleScheduleOption
    {
        public double Percent { get; set; } = 0;
        public ConsensusRules Rules { get; set; } = new();
    }

    /// <summary>
    /// A <see cref="ConsensusRules"/> with a <see cref="Name"/>, so a node group can refer to
    /// it from <see cref="ScenarioFile.NodeRules"/> instead of embedding the same fields
    /// inline on every group that uses it. The name exists only for scenario-file authoring;
    /// what actually reaches runtime state is a plain, unnamed <see cref="ConsensusRules"/>.
    /// </summary>
    public class NamedConsensusRules : ConsensusRules
    {
        public string Name { get; set; } = "";
        /// <summary>
        /// This ruleset's $-reference value over height. Only consulted by a value-seeking
        /// node group's profitability comparison; omitted means this ruleset is worth $0 at
        /// every height.
        /// </summary>
        public List<PriceScheduleEntry> PriceSchedule { get; set; } = new();
    }

    /// <summary>
    /// Declarative description of one phase of a run. Phase 0's settings and node groups take
    /// effect immediately; each later phase's settings/node groups take over once the
    /// previous phase's <see cref="DurationSeconds"/> elapses. Any field a phase leaves null
    /// inherits whatever the previous phase had in effect (or a built-in default, for phase
    /// 0) — a phase only needs to state what's actually changing.
    /// </summary>
    public class Scenario
    {
        public string? Description { get; set; }

        /// <summary>
        /// How long this phase lasts before the next one in the array takes over. For the
        /// last phase, this instead means how long the whole run lasts before automatically
        /// shutting down. Null/omitted on the last phase means no automatic stop. Null/omitted
        /// on any earlier phase means that phase never ends on its own.
        /// </summary>
        public int? DurationSeconds { get; set; }

        public bool? AutoGrowth { get; set; }

        public int? GrowthIntervalSeconds { get; set; }

        /// <summary>
        /// Multiplier applied to the current node count each growth tick. 2.0 (the default)
        /// doubles the network every tick; 1.5 adds 50% more nodes per tick. A value at or
        /// below 1.0 stalls growth entirely.
        /// </summary>
        public double? GrowthRate { get; set; }

        public double? GrowthJitterSeconds { get; set; }

        /// <summary>
        /// Floor the network tops up to — one node per tick, ignoring
        /// <see cref="GrowthRate"/> — before exponential growth-rate scaling takes over.
        /// </summary>
        public int? GrowthMinSeedNodes { get; set; }

        public int? MaxNodes { get; set; }

        public int? OutboundPeerCount { get; set; }

        /// <summary>
        /// Fraction of newly-created nodes assigned a malicious role instead of honest,
        /// cycling through the four malicious types in order. Only affects nodes with no
        /// <c>metadata.json</c> yet; node-group-authored nodes always use their own role.
        /// </summary>
        public double? GrowthMaliciousFraction { get; set; }

        /// <summary>Fraction of newly-created nodes assigned wallet-only instead of mining-capable.</summary>
        public double? GrowthWalletOnlyFraction { get; set; }

        /// <summary>
        /// Node churn — nodes leaving the live network, the counterpart to organic growth.
        /// Independent of <see cref="AutoGrowth"/>: churn runs whenever
        /// <see cref="ChurnRate"/> is above 0.
        /// </summary>
        public int? ChurnIntervalSeconds { get; set; }

        public double? ChurnRate { get; set; }

        public int? ChurnMinNodes { get; set; }

        /// <summary>
        /// Each entry describes <c>Count</c> identically-configured nodes to add when this
        /// phase begins, applied in the order listed and added on top of whatever nodes
        /// already exist from earlier phases. An empty list means no explicit nodes this
        /// phase; for phase 0 specifically, an empty list also means the default single-node
        /// start.
        /// </summary>
        public List<ScenarioNodeGroup> NodeGroups { get; set; } = new();
    }

    /// <summary>
    /// One group of <see cref="Count"/> nodes sharing a starting configuration — the same
    /// fields <see cref="NodeMetadata"/> carries, minus <c>Id</c> and <c>SigningKey</c>.
    /// </summary>
    public class ScenarioNodeGroup
    {
        public int Count { get; set; } = 1;
        public NodeRole Role { get; set; } = NodeRole.Honest;
        public int HashPower { get; set; } = 1;
        /// <summary>
        /// $ cost of a single mining attempt. Only consulted for a value-seeking group: each
        /// turn, if this group's best candidate's value doesn't clear
        /// <c>CostPerAttempt x HashPower</c>, it sits idle rather than mining at a guaranteed
        /// loss. Default 0 means mining is free.
        /// </summary>
        public decimal CostPerAttempt { get; set; } = 0m;
        /// <summary>
        /// $ fixed cost this group's node owes every turn, regardless of whether it mines.
        /// Only meaningful for a value-seeking group. Default 0 means no living cost.
        /// </summary>
        public decimal CostOfLiving { get; set; } = 0m;
        /// <summary>
        /// $ runway this group's node starts with, on top of its on-chain balance's market
        /// value, before <see cref="CostOfLiving"/> can push it into insolvency.
        /// </summary>
        public decimal StartingCapital { get; set; } = 0m;
        /// <summary>
        /// $ cost to buy +1 HashPower, drawn from this node's own earned profit. Only
        /// meaningful for a value-seeking group. Default 0 disables reinvestment entirely.
        /// </summary>
        public decimal HashPowerCost { get; set; } = 0m;
        /// <summary>Upper bound on how much <see cref="HashPowerCost"/>-driven reinvestment can grow this group's HashPower. 0 means uncapped.</summary>
        public int MaxHashPower { get; set; } = 0;
        public bool CanMine { get; set; } = true;
        public string? Pool { get; set; } = null;
        /// <summary>
        /// Names of pools this group reconsiders joining every turn. A name that doesn't
        /// match any existing pool is valid — it's simply empty until someone joins. Empty
        /// (default) disables reconsideration entirely.
        /// </summary>
        public List<string> PoolCandidates { get; set; } = new();
        /// <summary>
        /// Own solo win-probability cutoff below which this group optimizes for realization
        /// instead of expected value. Only meaningful when <see cref="PoolCandidates"/> is
        /// non-empty.
        /// </summary>
        public decimal PoolAdoptionThreshold { get; set; } = 0.5m;
        public int EconomicWeight { get; set; } = 1;
        /// <summary>
        /// Shorthand for "this group follows one named ruleset for its whole life" — sugar
        /// for <see cref="RuleSchedule"/> with a single <c>{ FromHeight: 0, RulesName }</c>
        /// entry. Ignored (with a warning) if <see cref="RuleSchedule"/> is also set.
        /// </summary>
        public string? RulesName { get; set; } = null;

        /// <summary>
        /// This group's full timeline of which named ruleset is active at which block
        /// height. Takes precedence over <see cref="RulesName"/> if both are set.
        /// </summary>
        public List<ScenarioRuleScheduleEntry> RuleSchedule { get; set; } = new();

        /// <summary>
        /// Populated by <see cref="ScenarioLoader.LoadAsync"/>'s resolution pass, from
        /// <see cref="RulesName"/>/<see cref="RuleSchedule"/> looked up against
        /// <see cref="ScenarioFile.NodeRules"/>. This is what
        /// <see cref="NodeNetwork.AddNodeAsync"/> actually reads.
        /// </summary>
        public List<RuleScheduleEntry> ResolvedRuleSchedule { get; set; } = new();

        /// <summary>
        /// Whether this group dynamically picks its ruleset each height by live
        /// profitability instead of following a fixed <see cref="RulesName"/>/
        /// <see cref="RuleSchedule"/>. Orthogonal to role/mining participation.
        /// </summary>
        public bool ValueSeeking { get; set; } = false;

        /// <summary>
        /// The explicit set of <see cref="ScenarioFile.NodeRules"/> entries (by name) this
        /// group compares when <see cref="ValueSeeking"/> is true. Takes precedence over
        /// <see cref="RulesName"/>/<see cref="RuleSchedule"/> when it resolves to at least
        /// one valid candidate.
        /// </summary>
        public List<string> ValueSeekingCandidates { get; set; } = new();

        /// <summary>
        /// Populated by <see cref="ScenarioLoader.LoadAsync"/>'s resolution pass, from
        /// <see cref="ValueSeekingCandidates"/> looked up against
        /// <see cref="ScenarioFile.NodeRules"/>. This is what
        /// <see cref="NodeMetadataStore.LoadOrCreateFromGroupAsync"/> actually reads.
        /// </summary>
        public List<ValueSeekingCandidate> ResolvedValueSeekingCandidates { get; set; } = new();
    }

    /// <summary>
    /// One entry in a <see cref="ScenarioNodeGroup.RuleSchedule"/> — see
    /// <see cref="RuleScheduleEntry"/> for the resolved, name-free runtime equivalent this
    /// becomes.
    /// </summary>
    public class ScenarioRuleScheduleEntry
    {
        public int FromHeight { get; set; } = 0;
        public string? RulesName { get; set; } = null;
    }

    public static class ScenarioLoader
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        /// <summary>
        /// Returns null if <paramref name="path"/> doesn't exist, the file is empty/parses
        /// to no phases, or it fails to parse — all logged, then treated the same as absent.
        /// Also resolves every node group's rule references against
        /// <see cref="ScenarioFile.NodeRules"/> before returning, so nothing downstream ever
        /// has to do that lookup itself.
        /// </summary>
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

        /// <summary>
        /// Builds a name-to-<see cref="NamedConsensusRules"/> lookup from
        /// <c>scenarioFile.NodeRules</c> (last one wins on a duplicate name, logged), then
        /// resolves every phase's every node group's rule references against it. A name that
        /// isn't defined in <c>NodeRules</c> is a scenario-authoring mistake, so it's logged
        /// rather than silently falling back; the fallback is a plain
        /// <c>new ConsensusRules()</c>.
        /// </summary>
        private static void ResolveNodeRules(string path, ScenarioFile scenarioFile)
        {
            var byName = new Dictionary<string, NamedConsensusRules>();
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

            List<RuleScheduleEntry> ResolveSchedule(List<ScenarioRuleScheduleEntry> schedule) =>
                schedule.Select(entry => new RuleScheduleEntry { FromHeight = entry.FromHeight, Rules = ResolveOne(entry.RulesName) }).ToList();

            List<ValueSeekingCandidate> ResolveValueSeekingCandidates(List<string> names)
            {
                var result = new List<ValueSeekingCandidate>();
                foreach (var name in names)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (byName.TryGetValue(name, out var namedRules))
                        result.Add(new ValueSeekingCandidate { Rules = namedRules, PriceSchedule = namedRules.PriceSchedule });
                    else
                        Console.WriteLine($"[scenario] {path} references ValueSeekingCandidates '{name}', which isn't defined in NodeRules; skipping it");
                }
                return result;
            }

            foreach (var phase in scenarioFile.Phases)
            {
                foreach (var group in phase.NodeGroups)
                {
                    if (group.ValueSeeking)
                    {
                        var candidates = ResolveValueSeekingCandidates(group.ValueSeekingCandidates);
                        if (candidates.Count > 0)
                        {
                            if (group.RulesName != null || group.RuleSchedule.Count > 0)
                                Console.WriteLine($"[scenario] {path} has a NodeGroup with both ValueSeeking and RulesName/RuleSchedule set; ValueSeeking wins");
                            group.ResolvedValueSeekingCandidates = candidates;
                            group.ResolvedRuleSchedule = new List<RuleScheduleEntry>();
                            continue;
                        }
                        Console.WriteLine($"[scenario] {path} has ValueSeeking: true but ValueSeekingCandidates resolved to no valid entries; falling back to RulesName/RuleSchedule");
                    }

                    if (group.RuleSchedule.Count > 0)
                    {
                        if (group.RulesName != null)
                            Console.WriteLine($"[scenario] {path} has a NodeGroup with both RulesName and RuleSchedule set; RuleSchedule wins");

                        group.ResolvedRuleSchedule = ResolveSchedule(group.RuleSchedule);
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

            scenarioFile.ResolvedDefaultRuleSchedule = scenarioFile.DefaultRuleSchedule
                .Select(entry => new ResolvedDefaultRuleScheduleEntry
                {
                    FromHeight = entry.FromHeight,
                    RuleSchedules = entry.RuleSchedules
                        .Select(option => new ResolvedDefaultRuleScheduleOption
                        {
                            Percent = Math.Max(0, option.Percent),
                            Rules = ResolveOne(option.RulesName)
                        })
                        .ToList()
                })
                .ToList();
        }

        private static string SanitizeForFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        /// <summary>
        /// Root directory for this run's node folders and <c>watcher.db</c> —
        /// <c>ScenarioResults/&lt;timestamp&gt;-&lt;scenario name, or "no-scenario"&gt;/</c>,
        /// computed once at startup. Also copies the exact scenario file that was executed
        /// into the new result folder when one was used, so the folder is a self-contained
        /// record of both what happened and exactly what configuration produced it.
        /// </summary>
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
