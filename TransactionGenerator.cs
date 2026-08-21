using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    /// <summary>
    /// Synthetic transaction traffic generator. Sends from real node IDs rather than
    /// made-up user names, since only node IDs ever actually receive coins. Balances are
    /// read from a live <c>/balances</c> snapshot each round, per account and asset, and a
    /// sender never gets asked to send more than they currently have of whichever asset was
    /// picked. A generated transaction carries no asset of its own, so if the picked asset
    /// isn't the one active at the block it eventually lands in, the target node's
    /// <c>/tx</c> endpoint simply rejects it, the same as any other unaffordable transaction.
    /// </summary>
    public static class TransactionGenerator
    {
        private static readonly Random Rng = new();

        public static async Task RunAsync(NodeNetwork network, int port, CancellationToken token)
        {
            using var http = new HttpClient();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var nodeIds = network.GetAllNodeIds();
                    if (nodeIds.Count == 0) { if (!await DelayOrCancelled(500, token)) break; continue; }

                    var queryId = nodeIds[Rng.Next(nodeIds.Count)];
                    var balancesJson = await http.GetStringAsync($"http://localhost:{port}/{queryId}/balances", token);
                    var balances = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, decimal>>>(balancesJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

                    var spenders = balances
                        .SelectMany(byAccount => byAccount.Value.Select(byAsset => (Account: byAccount.Key, Amount: byAsset.Value)))
                        .Where(s => s.Amount > 0m)
                        .ToList();
                    if (spenders.Count == 0) { if (!await DelayOrCancelled(1500, token)) break; continue; }

                    var spender = spenders[Rng.Next(spenders.Count)];
                    var from = spender.Account;
                    string to;
                    do { to = nodeIds[Rng.Next(nodeIds.Count)]; } while (to == from && nodeIds.Count > 1);

                    var amount = Math.Min(spender.Amount, (decimal)Rng.Next(1, 100));
                    var tx = new Transaction { From = from, To = to, Amount = amount };

                    var targetId = nodeIds[Rng.Next(nodeIds.Count)];
                    var json = JsonSerializer.Serialize(tx);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await http.PostAsync($"http://localhost:{port}/{targetId}/tx", content, token);
                }
                catch (OperationCanceledException) { break; }
                catch { }

                if (!await DelayOrCancelled(1500, token)) break;
            }
        }

        private static async Task<bool> DelayOrCancelled(int milliseconds, CancellationToken token)
        {
            try { await Task.Delay(milliseconds, token); return true; }
            catch (OperationCanceledException) { return false; }
        }
    }
}
