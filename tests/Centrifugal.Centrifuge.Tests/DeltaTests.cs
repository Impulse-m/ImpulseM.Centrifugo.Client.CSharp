using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Xunit;

// Delta publications (Fossil) of a subscription. The source, delta and expected text are the first Fossil vector of
// FossilTests.cs.
namespace Centrifugal.Centrifuge.Tests {


    [Collection("Integration")]
    public class DeltaTests : IAsyncLifetime {
        private static readonly TimeSpan wait = TimeSpan.FromSeconds(5);

        private const string Source =
            "{\"asks\":[[\"609590\",\"3792.6\"],[\"609600\",\"11507.8\"],[\"609640\",\"663.11\"]," +
            "[\"609690\",\"302.71\"],[\"609700\",\"744.52\"],[\"609730\",\"209.94\"]," +
            "[\"609750\",\"18.59\"],[\"609790\",\"156\"],[\"609800\",\"859.03\"],[\"609830\"," +
            "\"216.98\"],[\"609860\",\"217.42\"],[\"609870\",\"60\"],[\"609880\",\"384.06\"]," +
            "[\"609890\",\"4615.87\"],[\"609900\",\"25.98\"],[\"609940\",\"63.95\"],[\"609950\"," +
            "\"242.6\"],[\"609960\",\"2000\"],[\"609970\",\"1573\"],[\"609980\",\"47.56\"],[\"609990\"," +
            "\"582.26\"],[\"610000\",\"42899.13\"],[\"610020\",\"24.46\"],[\"610110\",\"150\"]]," +
            "\"bids\":[[\"609520\",\"2010.12\"],[\"609510\",\"5080.7\"],[\"609500\",\"297.5\"]," +
            "[\"609490\",\"1238.52\"],[\"609480\",\"896.37\"],[\"609470\",\"1234.91\"],[\"609460\"," +
            "\"451.36\"],[\"609250\",\"58.45\"],[\"609220\",\"786.48\"],[\"609200\",\"101.64\"]," +
            "[\"609190\",\"41.03\"],[\"609160\",\"650.49\"],[\"609100\",\"6932.07\"],[\"609070\"," +
            "\"16.59\"],[\"609050\",\"149.22\"],[\"609040\",\"52.53\"],[\"609030\",\"11.52\"]," +
            "[\"609020\",\"1038.35\"],[\"609010\",\"334.83\"],[\"609000\",\"3453.95\"],[\"608900\"," +
            "\"850.81\"],[\"608880\",\"57\"],[\"608850\",\"5.47\"],[\"608840\",\"41.23\"]]," +
            "\"lastTradePrice\":\"609520\",\"lastUpdate\":1727632299611}";

        private const string Delta = "Fm\n7i@0,6:1852.77s@7o,7:303488}3~2wv0;";

        private const string Expected =
            "{\"asks\":[[\"609590\",\"3792.6\"],[\"609600\",\"11507.8\"],[\"609640\",\"663.11\"],[\"609690\"," +
            "\"302.71\"],[\"609700\",\"744.52\"],[\"609730\",\"209.94\"],[\"609750\",\"18.59\"],[\"609790\"," +
            "\"156\"],[\"609800\",\"859.03\"],[\"609830\",\"216.98\"],[\"609860\",\"217.42\"],[\"609870\"," +
            "\"60\"],[\"609880\",\"384.06\"],[\"609890\",\"4615.87\"],[\"609900\",\"25.98\"],[\"609940\"," +
            "\"63.95\"],[\"609950\",\"242.6\"],[\"609960\",\"2000\"],[\"609970\",\"1573\"],[\"609980\"," +
            "\"47.56\"],[\"609990\",\"582.26\"],[\"610000\",\"42899.13\"],[\"610020\",\"24.46\"]," +
            "[\"610110\",\"150\"]],\"bids\":[[\"609520\",\"1852.72\"],[\"609510\",\"5080.7\"],[\"609500\"," +
            "\"297.5\"],[\"609490\",\"1238.52\"],[\"609480\",\"896.37\"],[\"609470\",\"1234.91\"]," +
            "[\"609460\",\"451.36\"],[\"609250\",\"58.45\"],[\"609220\",\"786.48\"],[\"609200\",\"101.64\"]," +
            "[\"609190\",\"41.03\"],[\"609160\",\"650.49\"],[\"609100\",\"6932.07\"],[\"609070\",\"16.59\"]," +
            "[\"609050\",\"149.22\"],[\"609040\",\"52.53\"],[\"609030\",\"11.52\"],[\"609020\",\"1038.35\"]," +
            "[\"609010\",\"334.83\"],[\"609000\",\"3453.95\"],[\"608900\",\"850.81\"],[\"608880\",\"57\"]," +
            "[\"608850\",\"5.47\"],[\"608840\",\"41.23\"]],\"lastTradePrice\":\"609520\"," +
            "\"lastUpdate\":1727632303488}";

        private readonly FakeCentrifugoServer server = new FakeCentrifugoServer();
        private CentrifugeClient? client;


        public async Task InitializeAsync() {
            await server.StartAsync();
        }


        public async Task DisposeAsync() {
            if (client != null) {
                await client.DisposeAsync();
            }
            await server.DisposeAsync();
        }


        private async Task<CentrifugeSubscription> SubscribeDeltaAsync(string channel) {
            server.OnSubscribe = (_, _) => new SubscribeResult { Delta = true };
            client = new CentrifugeClient(server.Url, new CentrifugeClientOptions());
            client.Connect();
            await client.ReadyAsync().WaitAsync(wait);
            TaskCompletionSource<bool> subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CentrifugeSubscription sub = client.NewSubscription(channel, new CentrifugeSubscriptionOptions { Delta = "fossil" });
            sub.Subscribed += (_, _) => subscribed.TrySetResult(true);
            sub.Subscribe();
            await subscribed.Task.WaitAsync(wait);
            return sub;
        }


        [Fact]
        // Java: testDeltaWithoutBaseStopsTheClient
        public async Task DeltaWithoutBase_DisconnectsWithCode2() {
            CentrifugeSubscription sub = await SubscribeDeltaAsync("ch");
            TaskCompletionSource<int> disconnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> errorType = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            client!.Disconnected += (_, e) => disconnected.TrySetResult(e.Code);
            sub.Error += (_, e) => errorType.TrySetResult(e.Type);
            int delivered = 0;
            sub.Publication += (_, _) => Interlocked.Increment(ref delivered);

            await server.SendPushAsync(new Push {
                Channel = "ch",
                Pub = new Publication { Data = ByteString.CopyFromUtf8("x"), Delta = true }
            });

            Assert.Equal(CentrifugeDisconnectedCodes.BadProtocol, await disconnected.Task.WaitAsync(wait));
            Assert.Equal("publicationDecode", await errorType.Task.WaitAsync(wait));
            Assert.Equal(0, Volatile.Read(ref delivered));
        }


        [Fact]
        // Java: testDeliveredDataIsNotTheBase
        public async Task DeliveredData_IsNotTheBase() {
            CentrifugeSubscription sub = await SubscribeDeltaAsync("ch");
            TaskCompletionSource<string> second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int count = 0;
            sub.Publication += (_, e) => {
                count++;
                if (count == 1) {
                    // The app changes the data it got: the base of the next delta must not change with it.
                    if (MemoryMarshal.TryGetArray(e.Data, out ArraySegment<byte> segment)) {
                        Array.Clear(segment.Array!, segment.Offset, segment.Count);
                    }
                    return;
                }
                second.TrySetResult(Encoding.UTF8.GetString(e.Data.Span));
            };

            await server.SendPushAsync(new Push {
                Channel = "ch",
                Pub = new Publication { Data = ByteString.CopyFromUtf8(Source) }
            });
            await server.SendPushAsync(new Push {
                Channel = "ch",
                Pub = new Publication { Data = ByteString.CopyFromUtf8(Delta), Delta = true }
            });

            Assert.Equal(Expected, await second.Task.WaitAsync(wait));
        }
    }
}
