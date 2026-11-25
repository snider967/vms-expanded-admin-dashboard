using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;

internal enum ReadFailure
{
    Network, Timeout, Authentication, Authorization, RateLimited, MissingEndpoint, Redirect,
    DestinationDenied, Unavailable, InvalidResponse, ResponseLimit
}

internal sealed class IntegrationReadException(ReadFailure failure, TimeSpan? retryAfter = null) : Exception
{
    internal ReadFailure Failure { get; } = failure;
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class IntegrationHttp(HttpClient client)
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        MaxResponseHeadersLength = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = Connect
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static IntegrationHttp Shared { get; } = new(SharedClient);
    private static readonly IPAddress AlibabaMetadata = IPAddress.Parse("100.100.100.200");
    private static readonly IPAddress AzureWireServer = IPAddress.Parse("168.63.129.16");
    private static readonly IPAddress Ec2Metadata = IPAddress.Parse("fd00:ec2::254");

    internal async Task<JsonDocument> ReadJson(IntegrationConnection connection, string path, int maximumBytes,
        CancellationToken cancellationToken, bool authenticate = true, string? session = null)
    {
        using var request = CreateRead(connection.BaseUri, path);
        if (authenticate) ApplyAuthentication(request, connection, session);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await ReadResponse(request, connection, maximumBytes, (_, body) =>
            JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }), cancellationToken);
    }

    internal async Task<string> ReadText(IntegrationConnection connection, string path, CancellationToken cancellationToken,
        string? session = null)
    {
        using var request = CreateRead(connection.BaseUri, path);
        ApplyAuthentication(request, connection, session);
        return await ReadResponse(request, connection, 4096, (_, body) => Encoding.UTF8.GetString(body.Span).Trim(), cancellationToken);
    }

    internal async Task<string> LoginQBittorrent(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Authentication != Authentication.Session || connection.Username is null)
            throw new IntegrationReadException(ReadFailure.Authentication);
        using var request = CreateRead(connection.BaseUri, "api/v2/auth/login");
        request.Method = HttpMethod.Post;
        request.Headers.Referrer = connection.BaseUri;
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = connection.Username, ["password"] = connection.Credential
        });
        return await ReadResponse(request, connection, 4096, (response, body) =>
        {
            if (!string.Equals(Encoding.UTF8.GetString(body.Span).Trim(), "Ok.", StringComparison.Ordinal)
                || !response.Headers.TryGetValues("Set-Cookie", out var cookies))
                throw new IntegrationReadException(ReadFailure.Authentication);
            var attributes = cookies.Select(cookie => cookie.Split(';', StringSplitOptions.TrimEntries))
                .FirstOrDefault(parts => parts[0].StartsWith("SID=", StringComparison.Ordinal));
            if (attributes is null || connection.BaseUri.Scheme != Uri.UriSchemeHttps
                && attributes.Skip(1).Any(part => string.Equals(part, "Secure", StringComparison.OrdinalIgnoreCase)))
                throw new IntegrationReadException(ReadFailure.Authentication);
            var sid = attributes[0];
            return sid.Length is > 4 and <= 260
                && sid[4..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                ? sid : throw new IntegrationReadException(ReadFailure.Authentication);
        }, cancellationToken);
    }

    private static void ApplyAuthentication(HttpRequestMessage request, IntegrationConnection connection, string? session)
    {
        switch (connection.Authentication)
        {
            case Authentication.ApiKey:
                if (!request.Headers.TryAddWithoutValidation("X-Api-Key", connection.Credential))
                    throw new IntegrationReadException(ReadFailure.Authentication);
                break;
            case Authentication.Basic when connection.Username is not null:
                request.Headers.Authorization = new("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(connection.Username + ":" + connection.Credential)));
                break;
            case Authentication.Bearer:
                request.Headers.Authorization = new("Bearer", connection.Credential);
                break;
            case Authentication.Session when session is not null:
                request.Headers.TryAddWithoutValidation("Cookie", session);
                break;
            default:
                throw new IntegrationReadException(ReadFailure.Authentication);
        }
    }

    private async Task<T> ReadResponse<T>(HttpRequestMessage request, IntegrationConnection connection, int maximumBytes,
        Func<HttpResponseMessage, ReadOnlyMemory<byte>, T> parse, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(connection.Timeout);
        var ct = budget.Token;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status is < 200 or >= 300)
                throw new IntegrationReadException(status switch
                {
                    401 => ReadFailure.Authentication,
                    403 => ReadFailure.Authorization,
                    404 or 405 or 410 or 501 => ReadFailure.MissingEndpoint,
                    429 => ReadFailure.RateLimited,
                    >= 300 and < 400 => ReadFailure.Redirect,
                    >= 500 => ReadFailure.Unavailable,
                    _ => ReadFailure.InvalidResponse
                }, GetRetryAfter(response));

            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new IntegrationReadException(ReadFailure.ResponseLimit);

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await source.ReadAsync(buffer, ct);
                if (count == 0) break;
                if (body.Length + count > maximumBytes) throw new IntegrationReadException(ReadFailure.ResponseLimit);
                body.Write(buffer, 0, count);
            }
            return parse(response, body.GetBuffer().AsMemory(0, checked((int)body.Length)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IntegrationReadException(ReadFailure.Timeout);
        }
        catch (JsonException)
        {
            throw new IntegrationReadException(ReadFailure.InvalidResponse);
        }
        catch (HttpRequestException e)
        {
            var failure = e.InnerException is IntegrationReadException { Failure: ReadFailure.DestinationDenied }
                ? ReadFailure.DestinationDenied : ReadFailure.Network;
            throw new IntegrationReadException(failure);
        }
        catch (IOException)
        {
            throw new IntegrationReadException(ReadFailure.Network);
        }
    }

    private static HttpRequestMessage CreateRead(Uri baseUri, string relativePath)
    {
        // The caller supplies an adapter-owned path. Keep it within the configured origin and prefix.
        if (!Uri.TryCreate(baseUri, relativePath, out var endpoint)
            || !baseUri.IsBaseOf(endpoint) || !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new IntegrationReadException(ReadFailure.DestinationDenied);
        return new HttpRequestMessage(HttpMethod.Get, endpoint);
    }

    internal static bool AddressAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast
                && !address.Equals(Ec2Metadata) && address.ScopeId == 0;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var octets = address.GetAddressBytes();
        return octets[0] is > 0 and < 224 && !(octets[0] == 169 && octets[1] == 254)
            && !address.Equals(AlibabaMetadata) && !address.Equals(AzureWireServer);
    }

    internal static bool PrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var octets = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return (octets[0] & 0xfe) == 0xfc;
        return address.AddressFamily == AddressFamily.InterNetwork
            && (octets[0] == 10 || octets[0] == 172 && octets[1] is >= 16 and <= 31 || octets[0] == 192 && octets[1] == 168);
    }

    private static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var destination = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(destination.Host, cancellationToken);
        var secure = context.InitialRequestMessage.RequestUri?.Scheme == Uri.UriSchemeHttps;
        if (addresses.Length == 0 || addresses.Any(address => !AddressAllowed(address) || !secure && !PrivateAddress(address)))
            throw new IntegrationReadException(ReadFailure.DestinationDenied);

        foreach (var address in addresses.Take(8))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, destination.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new IntegrationReadException(ReadFailure.Network);
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var interval = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return interval > TimeSpan.Zero ? interval : null;
    }
}
