using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Rotates across mining "turns" — see the "Mining participation" and
    // "Mining pools" notes in README.md. Deliberately knows nothing about
    // solo vs. pooled mining, roles, or hash power: every entry in
    // NodeNetwork's miner roster is just an IMiner, and whether it's a
    // SoloMiner or a PoolMiner (and, for a pool, who's currently in it) was
    // already decided back in NodeNetwork.AddNodeAsync. The roster is
    // re-derived fresh every iteration (so a node that just joined is
    // included immediately), but its ORDER is not just insertion order —
    // it's sorted by OrderKeys, which only gets reshuffled (cleared) when a
    // new block appears (detected by watching the network's tip hash
    // change). Otherwise whichever miner happened to be created earliest
    // would always go first in every round, giving it first crack at every
    // height.
    // ------------------------------------------------------------------
    public static class MiningScheduler
    {
        // A turn that finds nothing (the common case, since MineBlock is
        // bounded by HashPower — see the "Mining" note in README.md)
        // returns almost instantly, so without a pause here this loop
        // would spin a CPU core at ~100% doing essentially nothing. This delay
        // paces turns to something an operator can actually watch, and bounds
        // how often Chain.Snapshot() (an O(chain length) copy) gets taken.
        private const int MiningTurnDelayMs = 25;

        private static readonly Random Rng = new();

        // Persistent random sort key per mining participant (IMiner.Label — a
        // node's Id for a SoloMiner, a pool's name for a PoolMiner), used to
        // order turns. Cleared — so everyone draws a fresh key — every time a
        // new block appears; a participant not yet in here (including one
        // that just joined mid-height) draws its key the first time it's
        // looked up, landing it at a random position rather than always at
        // the end.
        private static readonly Dictionary<string, double> OrderKeys = new();

        private static double OrderKeyFor(string participantLabel)
        {
            if (!OrderKeys.TryGetValue(participantLabel, out var key))
            {
                key = Rng.NextDouble();
                OrderKeys[participantLabel] = key;
            }
            return key;
        }

        public static async Task RunAsync(NodeNetwork network, CancellationToken token)
        {
            int index = 0;
            string? lastTipHash = null;

            while (!token.IsCancellationRequested)
            {
                var currentTipHash = network.CurrentTipHash();
                if (currentTipHash == null)
                {
                    try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                    continue;
                }

                if (currentTipHash != lastTipHash)
                {
                    OrderKeys.Clear();
                    lastTipHash = currentTipHash;
                    index = 0;
                }

                var ordered = network.SnapshotMiners().OrderBy(m => OrderKeyFor(m.Label)).ToList();
                if (ordered.Count == 0)
                {
                    try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                index %= ordered.Count;

                try { await ordered[index].MineOneRoundAsync(token); }
                catch (OperationCanceledException) { break; }

                index++;
                try { await Task.Delay(MiningTurnDelayMs, token); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
