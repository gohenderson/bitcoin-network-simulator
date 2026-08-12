using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // Elastic worker pool: a bounded set of async worker loops pulling from a
    // shared queue. Starts with `minWorkers` running. If the backlog grows past
    // `scaleUpQueueThreshold`, it spins up another worker (up to `maxWorkers`).
    // Idle workers beyond the minimum retire themselves after a timeout, so the
    // pool grows under load and shrinks back down at rest.
    // ------------------------------------------------------------------

    public class ElasticTaskPool
    {
        private readonly string _ownerId;
        private readonly int _minWorkers;
        private readonly int _maxWorkers;
        private readonly int _scaleUpQueueThreshold;
        private readonly TimeSpan _idleRetireAfter;

        private readonly ConcurrentQueue<Func<Task>> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly object _scaleLock = new();
        private int _currentWorkers = 0;

        public ElasticTaskPool(string ownerId, int minWorkers = 2, int maxWorkers = 32,
            int scaleUpQueueThreshold = 4, TimeSpan? idleRetireAfter = null)
        {
            _ownerId = ownerId;
            _minWorkers = minWorkers;
            _maxWorkers = maxWorkers;
            _scaleUpQueueThreshold = scaleUpQueueThreshold;
            _idleRetireAfter = idleRetireAfter ?? TimeSpan.FromSeconds(10);

            for (int i = 0; i < _minWorkers; i++)
                SpawnWorker(isCoreWorker: true);
        }

        public void Enqueue(Func<Task> work)
        {
            _queue.Enqueue(work);
            _signal.Release();

            lock (_scaleLock)
            {
                if (_queue.Count > _scaleUpQueueThreshold && _currentWorkers < _maxWorkers)
                    SpawnWorker(isCoreWorker: false);
            }
        }

        private void SpawnWorker(bool isCoreWorker)
        {
            lock (_scaleLock)
            {
                if (_currentWorkers >= _maxWorkers) return;
                _currentWorkers++;
                Console.WriteLine($"[{_ownerId}] worker pool scaled up to {_currentWorkers} (queue depth {_queue.Count})");
            }
            _ = Task.Run(() => WorkerLoop(isCoreWorker));
        }

        private async Task WorkerLoop(bool isCoreWorker)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    bool signaled;
                    try
                    {
                        signaled = await _signal.WaitAsync(_idleRetireAfter, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!signaled)
                    {
                        if (!isCoreWorker)
                            break;
                        continue;
                    }

                    if (_queue.TryDequeue(out var work))
                    {
                        try { await work(); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{_ownerId}] worker error: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                lock (_scaleLock)
                {
                    _currentWorkers--;
                }
            }
        }

        public void Stop() => _cts.Cancel();
    }
}
