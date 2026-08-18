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
    /// recomputed from a live <c>/chain</c> snapshot each round, and a sender never gets
    /// asked to send more than they currently have.
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
                    var chainJson = await http.GetStringAsync($"http://localhost:{port}/{queryId}/chain", token);
                    var chain = JsonSerializer.Deserialize<List<Block>>(chainJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<Block>();
                    var balances = Ledger.ComputeBalances(chain);

                    var spenders = nodeIds.Where(n => balances.GetValueOrDefault(n) > 0m).ToList();
                    if (spenders.Count == 0) { if (!await DelayOrCancelled(1500, token)) break; continue; }

                    var from = spenders[Rng.Next(spenders.Count)];
                    string to;
                    do { to = nodeIds[Rng.Next(nodeIds.Count)]; } while (to == from && nodeIds.Count > 1);

                    var amount = Math.Min(balances[from], (decimal)Rng.Next(1, 100));
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
