using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace BitcoinNetworkSimulator
{
    /// <summary>
    /// Rotates across mining turns for every current <see cref="IMiner"/> — solo or pooled —
    /// in a randomized order that reshuffles whenever a new block appears.
    /// </summary>
    public static class MiningScheduler
    {
        private const int MiningTurnDelayMs = 25;

        private static readonly Random Rng = new();

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
