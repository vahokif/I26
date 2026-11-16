using System.Net;
using System.Net.Sockets;
using LiteNetLib.Layers;
using LiteNetLib.Utils;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// LiteNetLib's Connect sends the connect request before it files the new connection, and a datagram read in between is taken as
/// from nobody. A host on the same machine can answer in that moment when the connecting thread is held up: the client then
/// missed a refusal (it asked again and took the answer to a repeat; a password's "too many tries") or answered the host's first
/// messages "who are you?" (the host dropped it; a lobby's host closed its own lobby). The client's socket now reads nothing
/// until its connection is filed (<c>AnetLayer.ConnectFiled</c>).
/// </summary>
public class ConnectFiledTests
{
    private const byte ConnectRequest = 6, Disconnect = 8;   // LiteNetLib's PacketProperty, the low 5 bits of the first byte

    /// <summary>
    /// Under the client: answers its connect request with a refusal from a host of its own before the request is even sent,
    /// and holds the connecting thread until the client's socket has read it (or half a second).
    /// </summary>
    private sealed class AnswerAtOnce(UdpClient host, string why) : PacketLayerBase(0)
    {
        public MeleeClient? Client;
        public readonly ManualResetEventSlim Read = new();
        private bool Answered;

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length) => Read.Set();

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            // the first request only: its repeats (every half second) go unanswered
            if (Client is null || Answered || length < 13 || (data[offset] & 0x1f) != ConnectRequest) return;
            Answered = true;
            // the refusal LiteNetLib's host sends: the request's connection number in bits 5-6, its time after, the reason
            var reason = new NetDataWriter();
            reason.Put(why);
            var reject = new byte[9 + reason.Length];
            reject[0] = (byte)(Disconnect | (data[offset] & 0x60));
            Buffer.BlockCopy(data, offset + 5, reject, 1, 8);
            Buffer.BlockCopy(reason.Data, 0, reject, 9, reason.Length);
            host.Send(reject, reject.Length, new IPEndPoint(IPAddress.Loopback, Client.LocalPort));
            // read: give the socket's thread time to finish with it before the connection is filed
            if (Read.Wait(500)) Thread.Sleep(100);
        }
    }

    [Fact]
    public void AnAnswerBeforeTheConnectionIsFiledIsNotLost()
    {
        using var host = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var layer = new AnswerAtOnce(host, "refused at once");
        using var client = new MeleeClient(layer);
        layer.Client = client;
        client.Connect("127.0.0.1", ((IPEndPoint)host.Client.LocalEndPoint!).Port, "Groover", "vcgcour1.vcf");
        // the host never answers the request's repeats: the first answer is the only one
        for (int t = 0; t < 150 && client.State == ClientState.Connecting; t++) { client.Poll(); Thread.Sleep(20); }
        Assert.Equal((ClientState.Rejected, "refused at once"), (client.State, client.Reason));
    }

    [Fact]
    public void OverlappingConnectsHoldTheReadingUntilTheLastIsFiled()
    {
        var layer = new I76.Net.Anet.AnetLayer();
        using var firstIn = new ManualResetEventSlim();
        using var secondIn = new ManualResetEventSlim();
        using var firstGo = new ManualResetEventSlim();
        using var secondGo = new ManualResetEventSlim();
        var first = Task.Run(() => layer.ConnectFiled(() => { firstIn.Set(); return firstGo.Wait(5000); }));
        Assert.True(firstIn.Wait(5000));
        var second = Task.Run(() => layer.ConnectFiled(() => { secondIn.Set(); return secondGo.Wait(5000); }));
        Assert.True(secondIn.Wait(5000));
        Assert.True(layer.InboundHeld);
        // the second connect is filed first: the first is still under way, so the reading stays held
        secondGo.Set();
        Assert.True(second.Wait(5000) && second.Result);
        Assert.True(layer.InboundHeld);
        firstGo.Set();
        Assert.True(first.Wait(5000) && first.Result);
        Assert.False(layer.InboundHeld);
    }

    [Fact]
    public void AConnectThatThrowsStillLetsTheReadingResume()
    {
        var layer = new I76.Net.Anet.AnetLayer();
        Assert.Throws<InvalidOperationException>(() => layer.ConnectFiled<int>(() => throw new InvalidOperationException()));
        Assert.False(layer.InboundHeld);
    }
}
