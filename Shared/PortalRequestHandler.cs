using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

internal sealed class PortalRequestHandler(HttpMessageHandler transport, Action<string>? diagnostic = null) : DelegatingHandler(transport)
{
    private static long _nextRequest;
    private void Note(string message) { try { diagnostic?.Invoke(message); } catch { } }

    // Form bodies, query values, cookies and authorization headers are never recorded.
    private static string Address(Uri? uri) => uri?.GetLeftPart(UriPartial.Path) ?? "(no URI)";
    internal static bool IsPortalAddress(Uri? uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "http" or "https", UserInfo.Length: 0 }
        && string.Equals(uri.Host, PortalClient.PortalHost, StringComparison.OrdinalIgnoreCase);

    internal static bool CanSendForm(Uri? uri, Uri origin) =>
        IsPortalAddress(uri) && uri!.Scheme == origin.Scheme && uri.Port == origin.Port;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var current = request;
        var redirects = new List<HttpRequestMessage>();
        try
        {
            for (int count = 0; ; count++)
            {
                if (!IsPortalAddress(current.RequestUri))
                    throw new HttpRequestException("门户请求地址超出校园网认证范围");

                long started = Stopwatch.GetTimestamp();
                long requestId = Interlocked.Increment(ref _nextRequest);
                Note($"[{requestId}] HTTP -> {current.Method} {Address(current.RequestUri)}");
                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(current, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Note($"[{requestId}] HTTP !! {current.Method} {Address(current.RequestUri)} {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms\n{ex}");
                    throw;
                }
                Note($"[{requestId}] HTTP <- {(int)response.StatusCode} {current.Method} {Address(current.RequestUri)} " +
                    $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms content-type={response.Content.Headers.ContentType} bytes={response.Content.Headers.ContentLength?.ToString() ?? "unknown"}");
                response.RequestMessage ??= current;
                var code = (int)response.StatusCode;
                if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                    return response;

                using (response)
                {
                    var target = new Uri(current.RequestUri!, location);
                    Note($"HTTP redirect {code} -> {Address(target)}");
                    var method = current.Method;
                    var content = current.Content;
                    if ((code is 301 or 302 && method == HttpMethod.Post) || (code == 303 && method != HttpMethod.Head))
                    {
                        method = HttpMethod.Get;
                        content = null;
                    }

                    if (count >= 8 || !IsPortalAddress(target)
                        || (content is not null && !CanSendForm(target, current.RequestUri!)))
                        throw new HttpRequestException("门户重定向超出允许范围");

                    var next = new HttpRequestMessage(method, target) { Content = content };
                    foreach (var header in current.Headers)
                        next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    redirects.Add(next);
                    current = next;
                }
            }
        }
        finally
        {
            foreach (var redirect in redirects)
            {
                redirect.Content = null;
                redirect.Dispose();
            }
        }
    }
}
