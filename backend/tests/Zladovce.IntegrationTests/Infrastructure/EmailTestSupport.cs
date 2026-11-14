using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Zladovce.Application.Common.Paging;
using Zladovce.Application.Inventory;
using Zladovce.Application.Notifications;
using Zladovce.Domain.Inventory;
using Zladovce.Lambda;

namespace Zladovce.IntegrationTests.Infrastructure;

/// <summary>A real Mailpit SMTP server (same image as compose) whose REST API shows what was delivered.</summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    public const string Image = "axllent/mailpit:v1.32.0";
    private const ushort SmtpPort = 1025;
    private const ushort HttpPort = 8025;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(HttpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/livez")))
        .Build();

    private HttpClient _http = null!;

    public string Host => _container.Hostname;

    public int Smtp => _container.GetMappedPublicPort(SmtpPort);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}") };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        await _container.DisposeAsync();
    }

    public sealed record Message(string Id, string Subject, string Html, string Text, string From, IReadOnlyList<string> ReplyTo);

    /// <summary>Messages delivered to <paramref name="recipient"/>, newest first.</summary>
    public async Task<IReadOnlyList<string>> IdsForAsync(string recipient)
    {
        var json = await _http.GetStringAsync($"/api/v1/search?limit=200&query={Uri.EscapeDataString($"to:\"{recipient}\"")}");
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("ID").GetString()!)];
    }

    public async Task<Message> GetAsync(string id)
    {
        var json = await _http.GetStringAsync($"/api/v1/message/{id}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new Message(
            id,
            root.GetProperty("Subject").GetString()!,
            root.GetProperty("HTML").GetString()!,
            root.GetProperty("Text").GetString()!,
            root.GetProperty("From").GetProperty("Address").GetString()!,
            [.. root.GetProperty("ReplyTo").EnumerateArray().Select(r => r.GetProperty("Address").GetString()!)]);
    }

    public async Task<IReadOnlyList<Message>> MessagesForAsync(string recipient)
    {
        var messages = new List<Message>();
        foreach (var id in await IdsForAsync(recipient))
        {
            messages.Add(await GetAsync(id));
        }

        return messages;
    }

    /// <summary>The tracking code (grouped) from the link <c>#kod=…</c> of an email.</summary>
    public static string CodeFrom(Message message) =>
        Regex.Match(message.Html, "#kod=([0-9A-Z]{5}-[0-9A-Z]{5}-[0-9A-Z]{5}-[0-9A-Z]{5}-[0-9A-Z]{6})\"").Groups[1].Value is { Length: > 0 } code
            ? code
            : throw new InvalidOperationException("No tracking link in the email.");
}

/// <summary>
/// An SMTP server that refuses every session with <c>554</c> (a mail provider that is down or rejecting). Real sockets, so the
/// whole send path (MailKit) runs and fails as it would against a broken server.
/// </summary>
public sealed class RejectingSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public RejectingSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Sessions;

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref Sessions);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("554 5.3.2 Test server: service unavailable\r\n"), _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}

/// <summary>The real outbox worker Lambda handler against the test database, with a controllable clock.</summary>
public sealed class OutboxWorker
{
    private readonly OutboxWorkerFunction _function;

    public OutboxWorker(string connectionString, string smtpHost, int smtpPort, FakeTimeProvider? clock = null, int batchSize = 100_000)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["Database:ConnectionString"] = connectionString,
                ["Email:Enabled"] = "true",
                ["Email:Provider"] = "Smtp",
                ["Email:FromAddress"] = "prodavnica@zladovce.test",
                ["Email:FromName"] = "Zladovce",
                ["Email:Smtp:Host"] = smtpHost,
                ["Email:Smtp:Port"] = smtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Email:Dispatcher:BatchSize"] = batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Email:Dispatcher:SendTimeoutSeconds"] = "10",
                ["Storefront:BaseUrl"] = "http://localhost:4200",

                // The key of appsettings.Development.json, which the API hosts of the tests use to encrypt.
                ["Tracking:TokenEncryptionKey"] = TrackingOptions.DevelopmentKey,
            })
            .Build();

        Clock = clock;
        _function = new OutboxWorkerFunction(configuration, services =>
        {
            if (clock is not null)
            {
                services.AddSingleton<TimeProvider>(clock);
            }
        });
    }

    public FakeTimeProvider? Clock { get; }

    public static OutboxWorker For(PostgresFixture postgres, MailpitFixture mailpit, FakeTimeProvider? clock = null) =>
        new(postgres.ConnectionString, mailpit.Host, mailpit.Smtp, clock);

    public Task<OutboxWorkerResponse> RunAsync() => _function.HandleAsync(null, new FakeLambdaContext());

    /// <summary>Runs until a run claims nothing (older rows of other tests are sent along).</summary>
    public async Task<int> DrainAsync()
    {
        var total = 0;
        for (var i = 0; i < 20; i++)
        {
            var result = await RunAsync();
            total += result.Claimed;
            if (result.Claimed == 0)
            {
                break;
            }
        }

        return total;
    }
}

/// <summary>Inventory whose stock deduction fails: proves that checkout rolls everything back, outbox row included.</summary>
public sealed class FailingDeductionInventory(IInventoryService inner) : IInventoryService
{
    public void RegisterVariant(Guid variantId, int initialStock, int lowStockThreshold) => inner.RegisterVariant(variantId, initialStock, lowStockThreshold);

    public Task<IReadOnlyDictionary<Guid, StockLevel>> GetStockAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        inner.GetStockAsync(variantIds, cancellationToken);

    public Task DeductAsync(IReadOnlyCollection<StockLine> lines, Guid? orderId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated failure after the outbox row was added.");

    public Task ReturnAsync(IReadOnlyCollection<StockLine> lines, InventoryMovementType movementType, Guid? orderId, CancellationToken cancellationToken) =>
        inner.ReturnAsync(lines, movementType, orderId, cancellationToken);

    public Task<PagedResult<InventoryItemResponse>> ListAsync(InventoryListCriteria criteria, CancellationToken cancellationToken) =>
        inner.ListAsync(criteria, cancellationToken);

    public Task<InventoryItemResponse> AdjustAsync(Guid variantId, AdjustStockRequest request, CancellationToken cancellationToken) =>
        inner.AdjustAsync(variantId, request, cancellationToken);

    public Task<InventoryItemResponse> SetLowStockThresholdAsync(Guid variantId, SetLowStockThresholdRequest request, CancellationToken cancellationToken) =>
        inner.SetLowStockThresholdAsync(variantId, request, cancellationToken);

    public Task<PagedResult<InventoryMovementResponse>> GetMovementsAsync(Guid variantId, Application.Common.PageRequest page, CancellationToken cancellationToken) =>
        inner.GetMovementsAsync(variantId, page, cancellationToken);

    public Task<LowStockResponse> GetLowStockAsync(int limit, CancellationToken cancellationToken) => inner.GetLowStockAsync(limit, cancellationToken);
}
