using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// A Pluggy that answers over a real socket on this machine (loopback only), for what an in-memory handler cannot
/// show: what the HTTP handler the application registers does with a redirect. POST /auth hands out a key; every
/// GET /items/... answers 302 to <see cref="ElsewherePath"/>, which records what arrives there.
/// </summary>
internal sealed class RedirectingPluggyServer : IAsyncDisposable
{
    public const string ElsewherePath = "/elsewhere";
    public const string ApiKey = "fake-api-key-loopback";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public RedirectingPluggyServer()
    {
        _listener.Start();
        Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = Task.Run(AcceptAsync);
    }

    public string Address { get; }

    /// <summary>"METHOD path" of every request, in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Headers and body of every POST /auth, as they arrived on the socket.</summary>
    public ConcurrentQueue<AuthRequest> AuthRequests { get; } = new();

    /// <summary>The X-API-KEY header of every request that reached <see cref="ElsewherePath"/>.</summary>
    public ConcurrentQueue<string?> KeysSentElsewhere { get; } = new();

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(client));
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is not var (head, body)) return;

                var requestLine = head.Split("\r\n")[0].Split(' ');
                var method = requestLine[0];
                var path = requestLine[1];
                var headers = HeadersOf(head);
                Requests.Enqueue($"{method} {path}");

                string response;
                if (method == "POST" && path == "/auth")
                {
                    AuthRequests.Enqueue(new AuthRequest(headers, body));
                    response = Ok("{\"apiKey\":\"" + ApiKey + "\"}");
                }
                else if (path.StartsWith(ElsewherePath, StringComparison.Ordinal))
                {
                    KeysSentElsewhere.Enqueue(headers.TryGetValue("X-API-KEY", out var key) ? key : null);
                    response = Ok("{\"id\":\"a1b2c3d4-0000-4000-8000-000000000001\",\"status\":\"UPDATED\",\"connector\":{\"name\":\"Banco Exemplo\"}}");
                }
                else if (method == "GET" && path.StartsWith("/items/", StringComparison.Ordinal))
                {
                    response = $"HTTP/1.1 302 Found\r\nLocation: {Address}{ElsewherePath}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                }
                else
                {
                    response = Ok("{\"total\":0,\"totalPages\":0,\"page\":1,\"results\":[]}");
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The caller hung up: nothing to answer.
            }
        }
    }

    /// <summary>
    /// The request line and headers, and the body read to its end (by Content-Length, or to the last chunk) before
    /// anything is answered: the connection is never closed on a caller that is still sending.
    /// </summary>
    private static async Task<(string Head, string Body)?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read));
            if (count == 0) return null;
            read += count;
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;

            var head = text[..end];
            var body = text[(end + 4)..];
            var headers = HeadersOf(head);
            if (headers.TryGetValue("Content-Length", out var length))
            {
                if (body.Length < int.Parse(length, CultureInfo.InvariantCulture)) continue;
            }
            else if (headers.ContainsKey("Transfer-Encoding") && !body.EndsWith("0\r\n\r\n", StringComparison.Ordinal))
            {
                continue;
            }

            return (head, body);
        }

        return null;
    }

    private static Dictionary<string, string> HeadersOf(string head)
        => head.Split("\r\n").Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static string Ok(string json)
        => $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}

internal sealed record AuthRequest(IReadOnlyDictionary<string, string> Headers, string Body);
