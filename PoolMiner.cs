using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveChain
{
    // ------------------------------------------------------------------
    // A mining pool: a named group of SoloMiners (see Miner.cs) that mines as
    // one combined IMiner instead of each member getting its own separate
    // turn — see MINING POOLS at the top of the file. This is where all
    // pool-specific logic lives — combining member HashPower, picking who
    // coordinates a given turn, splitting the reward — so the round-robin
    // scheduler (Program.RoundRobinMiningLoopAsync) never has to know a pool
    // is anything other than one more IMiner.
    //
    // Membership starts with whatever's passed to the constructor and can
    // grow afterward via AddMember as new nodes join this pool over the
    // network's lifetime (see Program.AddNodeAsync) — the pool itself is the
    // one place that needs to track that, precisely so nothing else has to.
    // Reads and writes to the member list are locked because AddMember (from
    // the node-growth loop) and MineOneRoundAsync (from the mining loop) run
    // on independent, concurrently-executing loops.
    // ------------------------------------------------------------------
    public class PoolMiner : IMiner
    {
        public string Label { get; }

        private readonly object _lock = new();
        private readonly List<SoloMiner> _members;
        private readonly Random _rng;

        public PoolMiner(string poolName, IEnumerable<SoloMiner> initialMembers, Random rng)
        {
            Label = poolName;
            _members = new List<SoloMiner>(initialMembers);
            _rng = rng;
        }

        public void AddMember(SoloMiner member)
        {
            lock (_lock) { _members.Add(member); }
        }

        public async Task MineOneRoundAsync(CancellationToken token)
        {
            List<SoloMiner> members;
            lock (_lock) { members = new List<SoloMiner>(_members); }

            var totalHashPower = members.Sum(m => m.HashPower);
            var coordinator = WeightedRandomMember(members, totalHashPower, _rng);
            await coordinator.MineForPoolAsync(Label, totalHashPower, members, token);
        }

        // Picks one member at random, weighted by each member's own
        // HashPower — this is what determines who coordinates (builds, mines,
        // and broadcasts) this turn on the pool's behalf, and since the
        // coordinator ends up as the block's BuiltBy, it also gives
        // higher-HashPower members a proportionally larger share of that
        // narrative credit.
        private static SoloMiner WeightedRandomMember(List<SoloMiner> members, int totalHashPower, Random rng)
        {
            var roll = rng.Next(totalHashPower);
            var cumulative = 0;
            foreach (var m in members)
            {
                cumulative += m.HashPower;
                if (roll < cumulative) return m;
            }
            return members[^1];
        }
    }
}
