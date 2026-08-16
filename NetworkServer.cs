using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // The single, real HTTP listener shared by every node in the simulated
    // network — one OS-level port instead of one per node. A request's
    // destination node is the first path segment (e.g.
    // http://localhost:5000/000-alpha/chain addresses node "000-alpha"'s
    // /chain endpoint); NetworkServer resolves that segment to a live Node
    // via `resolveNode` (backed by NodeNetwork's registry) and hands the
    // remaining route to that node's own HandleRequestAsync (see Node.cs) —
    // so Node owns everything about what a request DOES, not how it
    // physically arrives. Incoming requests are handed to a bounded
    // ElasticTaskPool rather than an unbounded Task.Run each, so the number
    // of concurrent request handlers stays capped under load.
    // ------------------------------------------------------------------

    public class NetworkServer
    {
        private readonly int _port;
        private readonly Func<string, Node?> _resolveNode;
        private readonly Func<HttpListenerContext, string, Task>? _dashboardHandler;
        private readonly HttpListener _listener;
        private readonly ElasticTaskPool _requestPool;
        private volatile bool _running = true;

        // dashboardHandler, when given, intercepts every request whose first
        // path segment is "dashboard" — reserved out of node-id space since a
        // real node id is always NodeNetwork.NodeNameFor's zero-padded-index
        // + Greek-letter shape (e.g. "000-alpha"), never this literal word —
        // instead of routing it through resolveNode like an ordinary node
        // request. See Dashboard.cs and the "Watching a run" note in README.md.
        public NetworkServer(int port, Func<string, Node?> resolveNode, Func<HttpListenerContext, string, Task>? dashboardHandler = null)
        {
            _port = port;
            _resolveNode = resolveNode;
            _dashboardHandler = dashboardHandler;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _requestPool = new ElasticTaskPool("network-server", minWorkers: 4, maxWorkers: 64, scaleUpQueueThreshold: 8);
        }

        public void Start()
        {
            _listener.Start();
            Console.WriteLine($"[network] listening on http://localhost:{_port}/ — routes by node id, e.g. /000-alpha/chain");
            _ = Task.Run(ListenLoop);
        }

        public void Stop()
        {
            _running = false;
            _requestPool.Stop();
            try { _listener.Stop(); } catch { /* ignore on shutdown */ }
        }

        private async Task ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    if (!_running) return;
                    continue;
                }
                _requestPool.Enqueue(() => DispatchAsync(ctx));
            }
        }

        // Splits "/000-alpha/chain" into node id "000-alpha" and the
        // remaining route "/chain" (defaulting to "/" when nothing follows
        // the node id, e.g. a bare "/000-alpha"). A missing or unrecognized
        // node id is rejected here, before Node.HandleRequestAsync ever sees
        // the request.
        private async Task DispatchAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                var segments = path.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length == 0)
                {
                    await WriteJsonAsync(ctx, 404, "{\"error\":\"no node id in path — expected /<node-id>/<route>\"}");
                    return;
                }

                if (segments[0] == "dashboard" && _dashboardHandler != null)
                {
                    var dashboardRoute = segments.Length > 1 ? "/" + segments[1] : "/";
                    await _dashboardHandler(ctx, dashboardRoute);
                    return;
                }

                var node = _resolveNode(segments[0]);
                if (node == null)
                {
                    await WriteJsonAsync(ctx, 404, $"{{\"error\":\"unknown node id '{segments[0]}'\"}}");
                    return;
                }

                var route = segments.Length > 1 ? "/" + segments[1] : "/";
                await node.HandleRequestAsync(ctx, route);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[network] dispatch error: {ex.Message}");
            }
        }

        private static async Task WriteJsonAsync(HttpListenerContext ctx, int statusCode, string json)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            var buffer = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = buffer.Length;
            await ctx.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
