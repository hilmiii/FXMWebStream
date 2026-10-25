using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FXM.WebStream;

internal static class Program
{
    private static Process _relay;
    private static int _port;
    private const string Token = "harness-token";

    private static int Main()
    {
        try
        {
            _port = GetFreePort();
            _relay = StartRelay();
            WaitForPort(_port, 5000);

            var sender = new WebSocketTransport();
            var receiver = new WebSocketTransport();
            byte[] frame = null;
            byte[] control = null;

            receiver.FrameReceived += segment =>
            {
                frame = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array, segment.Offset, frame, 0, segment.Count);
            };
            sender.ControlReceived += segment =>
            {
                control = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array, segment.Offset, control, 0, segment.Count);
            };

            string url = "ws://127.0.0.1:" + _port + "/fxm-relay";
            Check(sender.Start(TransportConfig.CreateRelay(url, "harness-room", Token, RelayPeerRole.Sender)), "sender start");
            Check(receiver.Start(TransportConfig.CreateRelay(url, "harness-room", Token, RelayPeerRole.Receiver)), "receiver start");
            WaitUntil(() => sender.PeerCount == 1 && receiver.PeerCount == 1, 8000, "initial relay connection");

            var payload = new byte[] { 0xFF, 0xD8, 1, 2, 3, 4, 5 };
            sender.Send(new ArraySegment<byte>(payload));
            WaitUntil(() => { receiver.Poll(); return frame != null; }, 5000, "frame through relay");
            Check(Equal(payload, frame), "frame content");

            byte[] pause = StreamControlProtocol.Create(StreamControlMessage.CommandMessage(StreamCommandType.Pause, 0));
            receiver.SendControl(new ArraySegment<byte>(pause));
            WaitUntil(() => { sender.Poll(); return control != null; }, 5000, "control through relay");
            Check(Equal(pause, control), "control content");

            Console.WriteLine("PASS initial relay transport");

            // Reconnect test: stop the relay, restart it, and verify both clients recover.
            StopRelay();
            WaitUntil(() => sender.PeerCount == 0 && receiver.PeerCount == 0, 8000, "clients notice relay stop");

            _relay = StartRelay();
            WaitForPort(_port, 5000);
            WaitUntil(() => sender.PeerCount == 1 && receiver.PeerCount == 1, 15000, "clients reconnect");

            frame = null;
            var payload2 = new byte[] { 0xFF, 0xD8, 9, 8, 7 };
            sender.Send(new ArraySegment<byte>(payload2));
            WaitUntil(() => { receiver.Poll(); return frame != null; }, 5000, "frame after reconnect");
            Check(Equal(payload2, frame), "frame content after reconnect");

            Console.WriteLine("PASS relay reconnect");
            sender.Dispose();
            receiver.Dispose();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
        finally
        {
            StopRelay();
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Assets", "FXMWebStream")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new Exception("Could not find the repository root.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Process StartRelay()
    {
        var info = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "server.js",
            WorkingDirectory = Path.Combine(FindRepoRoot(), "Tools", "RelayServer"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.EnvironmentVariables["PORT"] = _port.ToString();
        info.EnvironmentVariables["HOST"] = "127.0.0.1";
        info.EnvironmentVariables["FXM_RELAY_TOKEN"] = Token;
        return Process.Start(info);
    }

    private static void StopRelay()
    {
        if (_relay == null) return;
        try { if (!_relay.HasExited) { _relay.Kill(); _relay.WaitForExit(3000); } } catch { }
        _relay.Dispose();
        _relay = null;
        Thread.Sleep(300);
    }

    private static void WaitForPort(int port, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { using (var client = new TcpClient()) { client.Connect(IPAddress.Loopback, port); return; } }
            catch (SocketException) { Thread.Sleep(100); }
        }
        throw new Exception("relay port did not open");
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(20);
        }
        throw new Exception("timeout: " + what);
    }

    private static bool Equal(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new Exception("check failed: " + what);
    }
}
