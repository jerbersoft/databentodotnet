using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// A loopback HTTP/1.1 endpoint that answers every request with a canned body.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a byte pipe, not a gateway double.</b> It parses a request target and nothing else:
/// no method check, no query handling, no credential check, no status other than 200 and 404. The
/// repository already has <c>MockHistoricalGateway</c>, which models the real API properly, and
/// nothing here is trying to be a second one — the reason it is not used is that it is built on
/// ASP.NET Core, which is a large dependency to drag through an ILC compile for a program whose
/// only question is whether a JSON context survives trimming.
/// </para>
/// <para>
/// <b>Why an HTTP server at all.</b> The source-generated <c>JsonSerializerContext</c>s are
/// <see langword="internal"/> to their packages, so no program outside them can hand a
/// <c>JsonTypeInfo</c> to a serializer directly. The only way to reach them is to make the client
/// perform a real request, which means something has to answer one. Under Native AOT a context
/// that failed to survive trimming does not fail to compile — it throws
/// <see cref="NotSupportedException"/> at the first deserialize, which is exactly the class of
/// failure that only running catches.
/// </para>
/// </remarks>
internal sealed class LoopbackJsonServer : IAsyncDisposable
{
    private const string NewLine = "\r\n";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;
    private readonly Dictionary<string, byte[]> _bodies;
    private readonly List<string> _served = [];

    /// <summary>Starts listening on an ephemeral loopback port.</summary>
    /// <param name="bodies">Request target (e.g. <c>/v0/metadata.list_publishers</c>) to response body.</param>
    public LoopbackJsonServer(Dictionary<string, byte[]> bodies)
    {
        _bodies = bodies;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _serving = ServeAsync(_stopping.Token);
    }

    /// <summary>The base URL to point a client at.</summary>
    public Uri BaseUrl =>
        new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/", UriKind.Absolute);

    /// <summary>The request targets served so far, in order.</summary>
    public IReadOnlyList<string> Served => _served;

    /// <summary>What the serve loop threw, if anything. Read after the requests are done.</summary>
    public Exception? Fault { get; private set; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _serving.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: cancelling the accept is how this loop ends.
        }

        _stopping.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = connection.GetStream();

                var target = await ReadRequestTargetAsync(stream, cancellationToken).ConfigureAwait(false);
                if (target is null)
                {
                    continue;
                }

                _served.Add(target);
                await RespondAsync(stream, target, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
        catch (SocketException)
        {
            // The listener was stopped out from under the accept.
        }
        catch (IOException exception)
        {
            Fault = exception;
        }
    }

    /// <summary>Reads request bytes up to the blank line and returns the target from the request line.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var read = 0;

        // Requests here are GETs with no body, so the headers are the whole request and the blank
        // line is the end of it.
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                return null;
            }

            read += got;
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            if (!text.Contains(NewLine + NewLine, StringComparison.Ordinal))
            {
                continue;
            }

            var line = text[..text.IndexOf(NewLine, StringComparison.Ordinal)];
            var parts = line.Split(' ');
            return parts.Length >= 2 ? parts[1] : null;
        }

        return null;
    }

    private async Task RespondAsync(NetworkStream stream, string target, CancellationToken cancellationToken)
    {
        var path = target.Split('?')[0];
        var found = _bodies.TryGetValue(path, out var body);
        body ??= "{}"u8.ToArray();

        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}{NewLine}"
            + $"Content-Type: application/json{NewLine}"
            + $"Content-Length: {body.Length}{NewLine}"
            + $"Connection: close{NewLine}{NewLine}");

        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
