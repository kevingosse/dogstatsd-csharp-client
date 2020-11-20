using System;
using System.Threading.Tasks;
using StatsdClient.Statistic;
using StatsdClient.Worker;

namespace StatsdClient.Bufferize
{
    /// <summary>
    /// StatsBufferize bufferizes metrics before sending them.
    /// </summary>
    internal class StatsBufferize : IDisposable
    {
        private readonly AsynchronousWorker<Stats> _worker;

        public StatsBufferize(
            StatsRouter statsRouter,
            int workerMaxItemCount,
            TimeSpan? blockingQueueTimeout,
            TimeSpan maxIdleWaitBeforeSending)
        {
            var handler = new WorkerHandler(statsRouter, maxIdleWaitBeforeSending);

            // `handler` (and also `statsRouter`) do not need to be thread safe as long as `workerThreadCount` is 1.
            this._worker = new AsynchronousWorker<Stats>(
                handler,
                new Waiter(),
                workerThreadCount: 1,
                workerMaxItemCount,
                blockingQueueTimeout);
        }

        public bool Send(Stats serializedMetric)
        {
            if (!this._worker.TryEnqueue(serializedMetric))
            {
                serializedMetric.Dispose();
                return false;
            }

            return true;
        }

        public void Flush()
        {
            this._worker.Flush();
        }

        public void Dispose()
        {
            this._worker.Dispose();
        }

        private class WorkerHandler : IAsynchronousWorkerHandler<Stats>
        {
            private readonly StatsRouter _statsRouter;
            private readonly TimeSpan _maxIdleWaitBeforeSending;
            private System.Diagnostics.Stopwatch _stopwatch;

            public WorkerHandler(StatsRouter statsRouter, TimeSpan maxIdleWaitBeforeSending)
            {
                _statsRouter = statsRouter;
                _maxIdleWaitBeforeSending = maxIdleWaitBeforeSending;
            }

            public void OnNewValue(Stats stats)
            {
                using (stats)
                {
                    _statsRouter.Route(stats);
                    _stopwatch = null;
                }
            }

            public bool OnIdle()
            {
                if (_stopwatch == null)
                {
                    _stopwatch = System.Diagnostics.Stopwatch.StartNew();
                }

                if (_stopwatch.ElapsedMilliseconds > _maxIdleWaitBeforeSending.TotalMilliseconds)
                {
                    this._statsRouter.OnIdle();

                    return true;
                }

                return true;
            }

            public void Flush()
            {
                this._statsRouter.Flush();
            }
        }
    }
}
