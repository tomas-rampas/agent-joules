using System.Globalization;
using System.Net;
using System.Net.ServerSentEvents;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentJoules.Dashboard;

/// <summary>
/// The loopback server of the live dashboard (REQ-032, EDGE-018, design 3.10): a Kestrel endpoint on <c>127.0.0.1</c> only that serves the page at <c>GET /</c> and the live events at
/// <c>GET /events</c> as server-sent events, and never reaches the run: a start that fails returns null with <see cref="FailureReason"/>, and nothing after the start throws into the caller.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryStartAsync"/> renders the page once, refuses it when the host's redactor would change it (a registered key in the configured link would be published), and tries the preferred
/// port and then the next ones up to <see cref="PortAttempts"/> in all and never above 65535 (<see cref="PortRange"/>); a port that is refused (an <see cref="AddressInUseException"/> or a
/// <see cref="SocketException"/> of <see cref="SocketError.AddressAlreadyInUse"/> or <see cref="SocketError.AccessDenied"/>, a Windows reserved port, anywhere in the chain) moves on to the next one
/// after the failed application was disposed; any other failure, the construction of the application included, ends the start with <c>start failed (&lt;type&gt;)</c>. A started application must
/// list exactly <c>http://127.0.0.1:&lt;port&gt;</c> as its address, or it is stopped and the start ends with <c>start failed (address)</c>. The whole start is bounded by <see cref="StartTimeout"/>
/// on the system clock (socket I/O). The printed URI is built with <see cref="UriBuilder"/>, so no URL literal appears in the code.
/// </para>
/// <para>
/// The application is a slim builder, built inside the attempt's guard because it reads the <c>DOTNET_*</c> and <c>ASPNETCORE_*</c> host settings when it is constructed. Its configuration
/// sources are then cleared and replaced by one value, <c>preferHostingUrls=false</c>, so no <c>appsettings.json</c>, <c>URLS</c> or <c>PREFERHOSTINGURLS</c> value (prefixed or not) can rebind it.
/// It has a no-op <see cref="IHostLifetime"/> (the CLI's token stays the only cancellation source), a shutdown timeout of <see cref="StopTimeout"/>, and one logging provider that forwards Warning
/// and above of the dashboard's own categories and every other category at Debug, so Kestrel's report of a refused port is never printed; the server's own start failures are logged at Debug
/// too (the command prints the one warning line), and a failure of an event stream after the start is logged once at Warning with its type only. No compression, caching or buffering
/// middleware is registered.
/// </para>
/// <para>
/// Every response carries the hash-pinned <see cref="ContentSecurityPolicy"/> of the page and <c>X-Content-Type-Options: nosniff</c>, a 404, 403, 421 or 503 included. A request whose
/// <c>Host</c> is not <c>127.0.0.1:&lt;port&gt;</c> or <c>localhost:&lt;port&gt;</c> (any case) gets 421 with no body (DNS rebinding); one with an <c>Origin</c> other than the server's own
/// loopback origin, or a <c>Sec-Fetch-Site</c> of <c>cross-site</c>, gets 403 with no body; no <c>Access-Control-*</c> header is ever sent. <c>GET /events</c> admits at most
/// <see cref="MaxClients"/> streams at once (503 with no body beyond it, logged once at Warning); each stream sends the snapshot of its <see cref="DashboardConnection"/> first and then the
/// connection's payloads, each as one <c>event: aj</c> item whose data line is the envelope as raw JSON, until the client leaves, the sink ends or the server stops.
/// </para>
/// </remarks>
/// <param name="sink">The sink whose connections the event streams read; nothing is bound until <see cref="TryStartAsync"/>.</param>
/// <param name="loggerFactory">The logger factory of the command: the server's own records and the forwarded records of the web host go to it.</param>
/// <exception cref="ArgumentNullException">An argument is null.</exception>
public sealed partial class DashboardServer(DashboardSink sink, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    /// <summary>The preferred port when <c>--dashboard-port</c> is not given.</summary>
    public const int DefaultPort = 5178;

    /// <summary>The number of consecutive ports tried, the preferred one included.</summary>
    public const int PortAttempts = 20;

    /// <summary>The most <c>/events</c> streams served at once.</summary>
    public const int MaxClients = 16;

    /// <summary>The reason of a start that ran out of <see cref="StartTimeout"/>.</summary>
    public const string StartTimedOutMessage = "start timed out after 5 s";

    /// <summary>The reason of a page that would publish a value the host's redactor masks; it names neither the URL nor the value.</summary>
    public const string SecretInPageMessage = "the page link holds a registered secret";

    private const int HighestPort = 65535;
    private const string EventType = "aj";
    private const string HtmlContentType = "text/html; charset=utf-8";
    private const string OwnCategoryPrefix = "AgentJoules.Dashboard";

    /// <summary>The bound of the whole start, every attempt included.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the command keeps the server up after the run, so the page renders the final state.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>The bound of the stop in <see cref="DisposeAsync"/>.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly DashboardSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly ILogger _logger = loggerFactory.CreateLogger<DashboardServer>();
    private WebApplication? _app;
    private int _started;
    private int _disposed;
    private int _clients;
    private int _capLogged;
    private int _streamFaultLogged;

    /// <summary>The URI the server listens on (<c>http://127.0.0.1:&lt;port&gt;/</c>), or null before a successful start.</summary>
    public Uri? Uri { get; private set; }

    /// <summary>Why the start returned null: <see cref="NoFreePortMessage"/>, <see cref="StartTimedOutMessage"/>, <see cref="SecretInPageMessage"/> or <c>start failed (&lt;type&gt;)</c>; null otherwise.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>The ports a start tries: the preferred one and the next ones, <see cref="PortAttempts"/> in all, never above 65535 and never wrapping.</summary>
    /// <param name="preferredPort">The preferred port, 1 to 65535.</param>
    /// <returns>The first and the last port.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preferredPort"/> is outside 1 to 65535.</exception>
    public static (int First, int Last) PortRange(int preferredPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(preferredPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(preferredPort, HighestPort);

        return (preferredPort, Math.Min(preferredPort + PortAttempts - 1, HighestPort));
    }

    /// <summary>The reason of a start whose every port was taken: <c>no free dashboard port in &lt;First&gt;-&lt;Last&gt;</c>.</summary>
    /// <param name="preferredPort">The preferred port, 1 to 65535.</param>
    /// <returns>The message.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preferredPort"/> is outside 1 to 65535.</exception>
    public static string NoFreePortMessage(int preferredPort)
    {
        (int first, int last) = PortRange(preferredPort);
        return string.Create(CultureInfo.InvariantCulture, $"no free dashboard port in {first}-{last}");
    }

    /// <summary>
    /// The policy every response carries: nothing loads or runs but the one inline script and the one inline style of <paramref name="page"/>, each pinned by the base64 SHA-256 of its UTF-8
    /// text between the tags, and the event stream of the server's own origin.
    /// </summary>
    /// <param name="page">The page as served.</param>
    /// <returns>The value of the <c>Content-Security-Policy</c> header.</returns>
    /// <exception cref="ArgumentException">The page does not hold exactly one inline <c>script</c> and one <c>style</c> element.</exception>
    public static string ContentSecurityPolicy(string page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return "default-src 'none'; script-src 'sha256-" + HashOf(ElementText(page, "script")) + "'; style-src 'sha256-" + HashOf(ElementText(page, "style"))
            + "'; connect-src 'self'; img-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }

    /// <summary>
    /// Renders the page and starts the server on the first free port of <see cref="PortRange"/>, within <see cref="StartTimeout"/>. Returns the URI, or null with <see cref="FailureReason"/>
    /// when no port was free, the time ran out, the page would publish a registered secret, or Kestrel failed otherwise; nothing stays bound after a null.
    /// </summary>
    /// <param name="preferredPort">The preferred port, 1 to 65535.</param>
    /// <param name="repositoryUrl">The repository link of the page (<c>Report:RepositoryUrl</c>), or null for the not-configured sentence.</param>
    /// <param name="cancellationToken">The command's token: a cancellation ends the start with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>The URI the server listens on, or null.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preferredPort"/> is outside 1 to 65535.</exception>
    /// <exception cref="ArgumentException"><paramref name="repositoryUrl"/> is refused by <see cref="DashboardPage.Render"/>.</exception>
    /// <exception cref="InvalidOperationException">The server was started before.</exception>
    /// <exception cref="ObjectDisposedException">The server was disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<Uri?> TryStartAsync(int preferredPort, Uri? repositoryUrl, CancellationToken cancellationToken)
    {
        (int first, int last) = PortRange(preferredPort);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The dashboard server was started before; a server starts once.");
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        // The page is rendered once; a registered key in the link would be published in the href, so such a page is never served (the reason names neither the URL nor the key).
        string page = DashboardPage.Render(repositoryUrl);
        if (!string.Equals(_sink.Redactor.Redact(page), page, StringComparison.Ordinal))
        {
            FailureReason = SecretInPageMessage;
            return null;
        }

        string policy = ContentSecurityPolicy(page);
        using var timeout = new CancellationTokenSource(StartTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        for (int port = first; port <= last && !linked.IsCancellationRequested; port++)
        {
            WebApplication? app = null;
            Exception? failure = null;
            try
            {
                // Built inside the guard: the slim builder reads the host settings of the process when it is constructed, and a value it refuses must end the start, never the run.
                app = Build(port, page, policy);
                await app.StartAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception caught)
            {
                failure = caught;
            }

            if (failure is null && app is not null)
            {
                return await PublishAsync(app, port).ConfigureAwait(false);
            }

            // A failed attempt releases its application before the next port.
            if (app is not null)
            {
                await StopAndDisposeAsync(app, stop: false).ConfigureAwait(false);
            }

            if (linked.IsCancellationRequested)
            {
                break;
            }

            if (failure is not null && !IsPortRefused(failure))
            {
                return Fail(failure.GetType().Name);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        FailureReason = timeout.IsCancellationRequested ? StartTimedOutMessage : NoFreePortMessage(preferredPort);
        return null;
    }

    /// <summary>
    /// The check after a start: the application lists exactly <c>http://127.0.0.1:&lt;port&gt;</c> as its address. A host setting that rebound it (another address, a second one) fails it.
    /// </summary>
    /// <param name="addresses">The addresses the server reports.</param>
    /// <param name="port">The port of the attempt.</param>
    /// <returns>True when the server listens on the loopback address of the port and nowhere else.</returns>
    internal static bool ListensOnlyOnLoopback(IEnumerable<string> addresses, int port)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        string expected = Uri.UriSchemeHttp + Uri.SchemeDelimiter + IPAddress.Loopback.ToString() + ":" + port.ToString(CultureInfo.InvariantCulture);
        return addresses.ToArray() is [{ } only] && string.Equals(only, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The classification of a failed attempt: true when the port was refused (an <see cref="AddressInUseException"/>, or a <see cref="SocketException"/> of
    /// <see cref="SocketError.AddressAlreadyInUse"/> or <see cref="SocketError.AccessDenied"/>, bare or anywhere in the inner or aggregated chain), so the next port is tried; false for any other failure.
    /// </summary>
    /// <param name="failure">The failure of the attempt.</param>
    /// <returns>True to move on to the next port.</returns>
    internal static bool IsPortRefused(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied })
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsPortRefused))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Stops the server within <see cref="StopTimeout"/> (open streams end) and disposes it; safe to call twice and before a start. Never throws.</summary>
    /// <returns>A task that completes when the server is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _app, null) is { } app)
        {
            await StopAndDisposeAsync(app, stop: true).ConfigureAwait(false);
        }
    }

    // A started application: checked for its one loopback address, then published before the disposal is checked again, so a DisposeAsync that lands meanwhile either finds the
    // application or is seen here; whichever takes it stops it.
    private async Task<Uri?> PublishAsync(WebApplication app, int port)
    {
        if (!ListensOnlyOnLoopback(app.Urls, port))
        {
            await StopAndDisposeAsync(app, stop: true).ConfigureAwait(false);
            return Fail("address");
        }

        _ = Interlocked.Exchange(ref _app, app);
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (Interlocked.Exchange(ref _app, null) is { } overtaken)
            {
                await StopAndDisposeAsync(overtaken, stop: true).ConfigureAwait(false);
            }

            throw new ObjectDisposedException(nameof(DashboardServer));
        }

        Uri = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), port).Uri;
        return Uri;
    }

    // A start that ends without a port: logged at Debug (the command prints the one warning line) and reported as start failed (<what>).
    private Uri? Fail(string what)
    {
        StartFailed(_logger, what);
        FailureReason = "start failed (" + what + ")";
        return null;
    }

    // The application of one attempt: loopback only, nothing configurable from outside, the headers and the request rules first, then the two routes.
    private WebApplication Build(int port, string page, string policy)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();

        // The slim builder captured the hosting settings in its constructor: preferHostingUrls=false keeps Listen(Loopback) the only binding whatever URLS and PREFERHOSTINGURLS say.
        _ = builder.Configuration.AddInMemoryCollection([new(WebHostDefaults.PreferHostingUrlsKey, "false")]);
        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddProvider(new ForwardingLoggerProvider(_loggerFactory));
        _ = builder.Services.RemoveAll<IHostLifetime>();
        _ = builder.Services.AddSingleton<IHostLifetime, NoOpHostLifetime>();
        _ = builder.Services.Configure<HostOptions>(static options => options.ShutdownTimeout = StopTimeout);
        _ = builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port);
        });

        WebApplication app = builder.Build();
        string numeric = IPAddress.Loopback.ToString() + ":" + port.ToString(CultureInfo.InvariantCulture);
        string named = "localhost:" + port.ToString(CultureInfo.InvariantCulture);
        string[] origins = [Uri.UriSchemeHttp + Uri.SchemeDelimiter + numeric, Uri.UriSchemeHttp + Uri.SchemeDelimiter + named];
        _ = app.Use(async (context, next) =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            headers.ContentSecurityPolicy = policy;
            headers.XContentTypeOptions = "nosniff";

            string host = context.Request.Host.Value ?? string.Empty;
            if (!string.Equals(host, numeric, StringComparison.OrdinalIgnoreCase) && !string.Equals(host, named, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            if (IsForeignRequest(context.Request.Headers, origins))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        _ = app.MapGet("/", () => Results.Content(page, HtmlContentType));
        _ = app.MapGet("/events", (HttpContext context) => Events(context));
        return app;
    }

    // A browser sends Origin on a cross-origin request and Sec-Fetch-Site on every request; an Origin that is not the server's own, or a Sec-Fetch-Site other than same-origin (the page's own
    // EventSource) or none (a typed URL), refuses the request, so a page on another loopback port (same-site) cannot open streams either. No CORS header is ever sent.
    private static bool IsForeignRequest(IHeaderDictionary headers, string[] origins)
    {
        string? origin = headers.Origin;
        string? site = headers["Sec-Fetch-Site"];
        return (origin is not null && !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            || (site is not null && !string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase) && !string.Equals(site, "none", StringComparison.OrdinalIgnoreCase));
    }

    // One event stream: a place under the cap, the connection of the sink and a token that ends with the request or the server, all released when the request ends.
    private IResult Events(HttpContext context)
    {
        if (Interlocked.Increment(ref _clients) > MaxClients)
        {
            _ = Interlocked.Decrement(ref _clients);
            if (Interlocked.Exchange(ref _capLogged, 1) == 0)
            {
                ClientCapReached(_logger, MaxClients);
            }

            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        context.Response.RegisterForDispose(new ClientPlace(this));
        DashboardConnection connection = _sink.Connect();
        context.Response.RegisterForDispose(connection);
        IHostApplicationLifetime lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        var ending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        context.Response.RegisterForDispose(ending);
        return TypedResults.ServerSentEvents(StreamAsync(connection, ending.Token));
    }

    // The snapshot first, then every payload of the connection as it arrives; the connection is disposed when the stream ends, whatever ended it.
    private async IAsyncEnumerable<SseItem<string>> StreamAsync(DashboardConnection connection, [EnumeratorCancellation] CancellationToken ending)
    {
        try
        {
            yield return new SseItem<string>(connection.Snapshot, EventType);
            ChannelReader<string> events = connection.Events;
            while (await WaitToReadAsync(events, ending).ConfigureAwait(false))
            {
                while (events.TryRead(out string? payload))
                {
                    yield return new SseItem<string>(payload, EventType);
                }
            }
        }
        finally
        {
            connection.Dispose();
        }
    }

    // A client that left or a server that stops ends the stream quietly; any other failure ends it too and is logged once per server at Warning with its type only (design 3.10).
    private async ValueTask<bool> WaitToReadAsync(ChannelReader<string> events, CancellationToken ending)
    {
        try
        {
            return await events.WaitToReadAsync(ending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception failure)
        {
            if (Interlocked.Exchange(ref _streamFaultLogged, 1) == 0)
            {
                string type = failure.GetType().Name;
                LogStreamFailedSafely(_logger, type);
            }

            return false;
        }
    }

    // A logger that throws must not reach the stream or the run.
    private static void LogStreamFailedSafely(ILogger logger, string exceptionType)
    {
        try
        {
            StreamFailed(logger, exceptionType);
        }
        catch (Exception)
        {
            // Dropped on purpose (EDGE-019).
        }
    }

    // Stops (when it started) within StopTimeout and disposes; a failure is logged at Debug with its type only and never thrown.
    private async Task StopAndDisposeAsync(WebApplication app, bool stop)
    {
        try
        {
            if (stop)
            {
                using var bound = new CancellationTokenSource(StopTimeout);
                await app.StopAsync(bound.Token).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            string type = failure.GetType().Name;
            StopFailed(_logger, type);
        }

        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            string type = failure.GetType().Name;
            StopFailed(_logger, type);
        }
    }

    private static string HashOf(string text) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // The text between the tags of the one element of this name; the page must hold it exactly once, without attributes.
    private static string ElementText(string page, string name)
    {
        string open = "<" + name + ">";
        string close = "</" + name + ">";
        int start = page.IndexOf(open, StringComparison.Ordinal);
        int end = start < 0 ? -1 : page.IndexOf(close, start + open.Length, StringComparison.Ordinal);
        return start >= 0 && end >= 0 && page.IndexOf("<" + name, StringComparison.Ordinal) == start && page.IndexOf("<" + name, end + close.Length, StringComparison.Ordinal) < 0
            ? page[(start + open.Length)..end]
            : throw new ArgumentException($"The page must hold exactly one inline {name} element.", nameof(page));
    }

    [LoggerMessage(EventId = 311, Level = LogLevel.Warning, Message = "The dashboard refused an event stream because {MaxClients} streams are open; later refusals are not logged")]
    private static partial void ClientCapReached(ILogger logger, int maxClients);

    [LoggerMessage(EventId = 312, Level = LogLevel.Debug, Message = "The dashboard did not start ({Reason})")]
    private static partial void StartFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 314, Level = LogLevel.Warning, Message = "The dashboard ended an event stream that failed ({ExceptionType}); later failures are not logged")]
    private static partial void StreamFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 313, Level = LogLevel.Debug, Message = "The dashboard did not stop cleanly ({ExceptionType})")]
    private static partial void StopFailed(ILogger logger, string exceptionType);

    // The place of one stream under the cap, given back when the request ends.
    private sealed class ClientPlace(DashboardServer server) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ = Interlocked.Decrement(ref server._clients);
            }
        }
    }

    // The web host installs no signal handler of its own: the CLI's token is the only cancellation source (design-review D-11).
    private sealed class NoOpHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // The only logging provider of the web host: Warning and above, at their own level for the dashboard's categories and at Debug for every other one (Kestrel's refused port, the
    // host's start failure), into the command's logger factory. A logger that throws is dropped.
    private sealed class ForwardingLoggerProvider(ILoggerFactory target) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            new ForwardingLogger(target.CreateLogger(categoryName), categoryName.StartsWith(OwnCategoryPrefix, StringComparison.Ordinal));

        public void Dispose()
        {
        }
    }

    private sealed class ForwardingLogger(ILogger target, bool own) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None && target.IsEnabled(Forwarded(logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            LogLevel level = Forwarded(logLevel);
            try
            {
                target.Log(level, eventId, state, exception, formatter);
            }
            catch (Exception)
            {
                // A broken log provider must not reach the server or the run.
            }
        }

        private LogLevel Forwarded(LogLevel logLevel) => own ? logLevel : LogLevel.Debug;
    }
}