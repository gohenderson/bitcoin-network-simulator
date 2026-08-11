using System.Threading;
using System.Threading.Tasks;

namespace NaiveChain
{
    // ------------------------------------------------------------------
    // Common mining entry point implemented by both SoloMiner (an individual
    // node mining on its own — see Miner.cs) and PoolMiner (a named group of
    // SoloMiners mining as one combined entity — see PoolMiner.cs). The
    // round-robin scheduler (Program.RoundRobinMiningLoopAsync) works purely
    // in terms of IMiner and deliberately knows nothing about pools, roles,
    // or hash power: it just orders whatever IMiners currently exist and
    // gives each one a turn. All of that — whether a node mines solo or as
    // part of a pool, how a pool picks who coordinates its turn, how a pool
    // splits its reward — is decided when a miner is created (see
    // Program.AddNodeAsync) and, for pools, inside PoolMiner itself.
    // ------------------------------------------------------------------
    public interface IMiner
    {
        // Stable identity used to key this miner's spot in the scheduler's
        // per-block random turn order (Program.MiningOrderKeys) — a node's Id
        // for a SoloMiner, a pool's name for a PoolMiner.
        string Label { get; }

        // Perform one mining turn: try to find a valid block and broadcast
        // it, or return having found nothing — see SIMULATED HASH POWER at
        // the top of the file for what "one turn" means.
        Task MineOneRoundAsync(CancellationToken token);
    }
}
