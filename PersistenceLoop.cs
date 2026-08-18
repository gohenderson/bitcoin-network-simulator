using System;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    /// <summary>
    /// Per-node persistence: restores a node's chain from its <c>blockchain.db</c> at startup
    /// (<see cref="ResumeFromDisk"/>), then periodically syncs the in-memory chain back to
    /// that same file for the rest of the run (<see cref="RunAsync"/>).
    /// </summary>
    public static class PersistenceLoop
    {
        private const int SyncIntervalMs = 3000;

        public static void ResumeFromDisk(Node node, BlockchainStore store)
        {
            try
            {
                var candidate = store.LoadAll();
                if (candidate == null || candidate.Count == 0)
                {
                    Console.WriteLine($"[{node.Id}] no saved chain found (blockchain.db); starting from genesis");
                    return;
                }

                var (loaded, reason) = node.Chain.TryLoadFrom(candidate);
                Console.WriteLine(loaded
                    ? $"[{node.Id}] {reason}"
                    : $"[{node.Id}] ignored saved chain ({reason}); starting from genesis");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{node.Id}] failed to read saved chain: {ex.Message}; starting from genesis");
            }
        }

        public static async Task RunAsync(Node node, BlockchainStore store, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = node.Chain.Snapshot();
                    store.Sync(snapshot);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[persistence:{node.Id}] failed to sync blockchain.db: {ex.Message}");
                }

                if (!await DelayOrCancelled(SyncIntervalMs, token)) break;
            }
        }

        private static async Task<bool> DelayOrCancelled(int milliseconds, CancellationToken token)
        {
            try { await Task.Delay(milliseconds, token); return true; }
            catch (OperationCanceledException) { return false; }
        }
    }
}
