using System;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// The fork's members of a subscription: the publish debounce window, the token refresh retry and the full token TTL.
    /// The fields keep the _ prefix because they are members of an upstream class, so the call sites in
    /// CentrifugeSubscription stay one line each.
    /// </summary>
    public partial class CentrifugeSubscription
    {
        /// <summary>The publish debounce window of the server in milliseconds, from the last subscribe. Zero is off.</summary>
        private int _publishDebounceMillis;


        /// <summary>The publish debounce, created by the first debounced publish.</summary>
        private PublishDebounce? _publishDebounce;


        /// <summary>The retry step of the token refresh and the token the server refused with 109.</summary>
        private readonly SubscriptionRefreshRetry _refreshRetry;


        /// <summary>True when the server gave a publish debounce window. Written on the loop, read from any thread.</summary>
        private bool PublishDebounceOn => Volatile.Read(ref _publishDebounceMillis) > 0;


        /// <summary>
        /// Sends the publish through the debounce window when one is on (the window comes from the subscribe reply).
        /// Returns true when the publish went through the window, and false when the window is off by the time the
        /// step runs: the caller then sends the publish the upstream way.
        /// </summary>
        private async Task<bool> PublishDebouncedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Task? debounced = await RoutePublishAsync(data, cancellationToken).ConfigureAwait(false);
            if (debounced == null)
            {
                return false;
            }
            await debounced.ConfigureAwait(false);
            return true;
        }


        /// <summary>
        /// Routes the publish to the debounce on the loop. Returns the task of that publish, or null when no window is on.
        /// The step hands the task out.
        /// </summary>
        private Task<Task?> RoutePublishAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            var route = new TaskCompletionSource<Task?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Invoke(() =>
            {
                if (_publishDebounceMillis <= 0)
                {
                    route.TrySetResult(null);
                    return;
                }
                try
                {
                    route.TrySetResult(PublishDebounceFor().Publish(data.ToArray(), _publishDebounceMillis, cancellationToken));
                }
                catch (Exception ex)
                {
                    route.TrySetException(ex);
                }
            });
            return route.Task;
        }


        /// <summary>Arms the next token refresh retry after the retry delay of the connection's backoff.</summary>
        private void ScheduleRefreshRetry() => ScheduleRefresh(_refreshRetry.NextDelayMillis());


        /// <summary>The full token TTL in milliseconds, capped at int.MaxValue (the refresh is due at the TTL, not before).</summary>
        internal static int TtlToFullMilliseconds(uint ttl)
        {
            long ms = (long)ttl * 1000;
            return (int)Math.Min(ms, int.MaxValue);
        }


        /// <summary>The publish debounce of this subscription, created on first use.</summary>
        private PublishDebounce PublishDebounceFor()
        {
            if (_publishDebounce == null)
            {
                _publishDebounce = new PublishDebounce(Loop, _client.Host.Timers, (data, cancellationToken) => _client.PublishAsync(Channel, data, cancellationToken));
            }
            return _publishDebounce;
        }


        /// <summary>Stops the publish debounce window and drops its pending publish.</summary>
        private void CancelPublishDebounce() => _publishDebounce?.Cancel();
    }
}
