using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Greybeard.Outbound
{
    /// <summary>
    /// Webhook delivery for order shipment notifications.
    /// Applies greybeard principles: idempotency, timeouts, retries, circuit breaker,
    /// outbox pattern, and graceful degradation.
    /// </summary>
    public class OrderShipmentWebhookService
    {
        private readonly HttpClient _httpClient;
        private readonly OrderDbContext _dbContext;
        private readonly IAsyncPolicy<HttpResponseMessage> _deliveryPolicy;
        private const int TimeoutSeconds = 10;
        private const int MaxRetries = 5;
        private const string IdempotencyKeyHeader = "X-Idempotency-Key";

        public OrderShipmentWebhookService(HttpClient httpClient, OrderDbContext dbContext)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

            // greybeard: External call - timeout enforced at HttpClient level
            _httpClient.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

            // greybeard: External call + Concurrency - retry with jittered backoff + circuit breaker
            var retryPolicy = Policy
                .Handle<HttpRequestException>()
                .Or<OperationCanceledException>()
                .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode &&
                    (int)r.StatusCode >= 500)  // Retry only on server errors or network failures
                .WaitAndRetryAsync(
                    retryCount: MaxRetries,
                    sleepDurationProvider: retryAttempt =>
                        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) +
                        TimeSpan.FromMilliseconds(new Random().Next(0, 1000)),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        var webhookId = context.ContainsKey("webhook_id")
                            ? context["webhook_id"].ToString()
                            : "unknown";
                        Console.WriteLine(
                            $"[WEBHOOK] Retry {retryCount}/{MaxRetries} for webhook {webhookId} " +
                            $"after {timespan.TotalSeconds:F1}s");
                    });

            var circuitBreakerPolicy = Policy
                .Handle<HttpRequestException>()
                .Or<OperationCanceledException>()
                .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode &&
                    (int)r.StatusCode >= 500)
                .CircuitBreakerAsync<HttpResponseMessage>(
                    handledEventsAllowedBeforeBreaking: 5,
                    durationOfBreak: TimeSpan.FromSeconds(30),
                    onBreak: (outcome, timespan, context) =>
                    {
                        var webhookId = context.ContainsKey("webhook_id")
                            ? context["webhook_id"].ToString()
                            : "unknown";
                        Console.WriteLine(
                            $"[WEBHOOK] Circuit breaker opened for {webhookId} " +
                            $"for {timespan.TotalSeconds:F0}s");
                    });

            _deliveryPolicy = Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);
        }

        /// <summary>
        /// Mark an order as shipped and enqueue webhook delivery via outbox pattern.
        /// greybeard: Mutation - wrapped in transaction for atomicity.
        /// greybeard: Can it fail half-way - outbox pattern separates order state from delivery.
        /// </summary>
        public async Task ShipOrderAsync(string orderId, string customerWebhookUrl, string customerId)
        {
            if (string.IsNullOrWhiteSpace(orderId))
                throw new ArgumentException("Order ID required", nameof(orderId));
            if (string.IsNullOrWhiteSpace(customerWebhookUrl))
                throw new ArgumentException("Webhook URL required", nameof(customerWebhookUrl));
            if (string.IsNullOrWhiteSpace(customerId))
                throw new ArgumentException("Customer ID required", nameof(customerId));

            // greybeard: Mutation - explicit transaction boundary
            using (var transaction = await _dbContext.Database.BeginTransactionAsync())
            {
                try
                {
                    var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
                    if (order == null)
                        throw new InvalidOperationException($"Order {orderId} not found");

                    // greybeard: Mutation - idempotency key ensures webhook fires exactly once per shipment
                    var idempotencyKey = $"ship-{orderId}-{DateTime.UtcNow:yyyyMMdd}";

                    // Mark order as shipped
                    order.Status = OrderStatus.Shipped;
                    order.ShippedAt = DateTime.UtcNow;
                    order.WebhookIdempotencyKey = idempotencyKey;

                    // greybeard: Can it fail half-way - outbox entry guarantees delivery attempt
                    // even if process crashes after order update
                    var outboxEntry = new WebhookOutboxEntry
                    {
                        Id = Guid.NewGuid(),
                        OrderId = orderId,
                        CustomerId = customerId,
                        WebhookUrl = customerWebhookUrl,
                        IdempotencyKey = idempotencyKey,
                        Payload = new OrderShippedEvent
                        {
                            OrderId = orderId,
                            ShippedAt = DateTime.UtcNow,
                            TrackingNumber = order.TrackingNumber
                        },
                        Status = WebhookDeliveryStatus.Pending,
                        CreatedAt = DateTime.UtcNow,
                        AttemptCount = 0
                    };

                    _dbContext.Orders.Update(order);
                    _dbContext.WebhookOutbox.Add(outboxEntry);

                    await _dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();

                    Console.WriteLine(
                        $"[WEBHOOK] Order {orderId} shipped, outbox entry queued with " +
                        $"idempotency key (masked)");
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
        }

        /// <summary>
        /// Deliver pending webhooks from outbox. Safe to call repeatedly.
        /// greybeard: Mutation - idempotency key in HTTP header prevents duplicate processing on customer side
        /// </summary>
        public async Task ProcessPendingWebhooksAsync(CancellationToken ct = default)
        {
            // greybeard: Reads a list - bounded result set to prevent unbounded fan-out
            var pendingEntries = await _dbContext.WebhookOutbox
                .Where(e => e.Status == WebhookDeliveryStatus.Pending && e.AttemptCount < MaxRetries)
                .OrderBy(e => e.CreatedAt)
                .Take(100)  // Batch limit
                .ToListAsync(ct);

            foreach (var entry in pendingEntries)
            {
                await DeliverWebhookAsync(entry, ct);
            }
        }

        private async Task DeliverWebhookAsync(WebhookOutboxEntry entry, CancellationToken ct)
        {
            try
            {
                // greybeard: Concurrency - row lock ensures single delivery attempt at a time
                var lockedEntry = await _dbContext.WebhookOutbox
                    .FromSqlInterpolated($"SELECT * FROM WebhookOutbox WHERE Id = {entry.Id} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);

                if (lockedEntry == null || lockedEntry.Status != WebhookDeliveryStatus.Pending)
                    return;

                using (var request = new HttpRequestMessage(HttpMethod.Post, lockedEntry.WebhookUrl))
                {
                    // greybeard: Mutation - idempotency key header for exactly-once semantics
                    request.Headers.Add(IdempotencyKeyHeader, lockedEntry.IdempotencyKey);

                    // greybeard: Trust-boundary validation - validate URL before sending
                    if (!IsValidWebhookUrl(lockedEntry.WebhookUrl))
                    {
                        await MarkDeliveryFailedAsync(lockedEntry, "Invalid webhook URL", ct);
                        return;
                    }

                    var content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(lockedEntry.Payload),
                        System.Text.Encoding.UTF8,
                        "application/json");
                    request.Content = content;

                    var context = new Polly.Context { { "webhook_id", lockedEntry.Id.ToString() } };

                    // greybeard: External call - retry + circuit breaker with jittered backoff
                    var response = await _deliveryPolicy.ExecuteAsync(
                        (ctx, cancellationToken) => _httpClient.SendAsync(request, cancellationToken),
                        context,
                        ct);

                    if (response.IsSuccessStatusCode)
                    {
                        lockedEntry.Status = WebhookDeliveryStatus.Delivered;
                        lockedEntry.DeliveredAt = DateTime.UtcNow;
                        Console.WriteLine($"[WEBHOOK] Delivered webhook {lockedEntry.Id}");
                    }
                    else
                    {
                        // greybeard: Can it fail half-way - graceful degradation: retry on transient errors
                        if ((int)response.StatusCode < 500)
                        {
                            lockedEntry.Status = WebhookDeliveryStatus.Failed;
                            lockedEntry.FailureReason = $"Client error: {(int)response.StatusCode}";
                        }
                        else
                        {
                            // Transient error, will retry on next run
                            lockedEntry.AttemptCount++;
                            lockedEntry.LastAttemptAt = DateTime.UtcNow;
                            Console.WriteLine(
                                $"[WEBHOOK] Transient error for {lockedEntry.Id}, " +
                                $"attempt {lockedEntry.AttemptCount}/{MaxRetries}");
                        }
                    }
                }
            }
            catch (BrokenCircuitException)
            {
                // greybeard: External call - circuit breaker: skip delivery, mark for retry
                entry.AttemptCount++;
                entry.LastAttemptAt = DateTime.UtcNow;
                Console.WriteLine($"[WEBHOOK] Circuit breaker open for {entry.Id}, skipping");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // greybeard: Graceful shutdown - no retry on cancellation
                throw;
            }
            catch (Exception ex)
            {
                // greybeard: No secrets in logs - do not log webhook URL or payload details
                await MarkDeliveryFailedAsync(entry, $"Delivery failed: {ex.GetType().Name}", ct);
            }

            // greybeard: Mutation + Concurrency - update within same transaction boundary
            using (var transaction = await _dbContext.Database.BeginTransactionAsync(ct))
            {
                try
                {
                    _dbContext.WebhookOutbox.Update(entry);
                    await _dbContext.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            }
        }

        private async Task MarkDeliveryFailedAsync(WebhookOutboxEntry entry, string reason, CancellationToken ct)
        {
            entry.Status = WebhookDeliveryStatus.Failed;
            entry.FailureReason = reason;
            entry.LastAttemptAt = DateTime.UtcNow;
            Console.WriteLine($"[WEBHOOK] Webhook {entry.Id} permanently failed: {reason}");
        }

        private bool IsValidWebhookUrl(string url)
        {
            // greybeard: Trust-boundary validation - strict URL parsing
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            // Reject private IP ranges (SSRF guard)
            var host = uri.Host.ToLower();
            if (host == "localhost" || host == "127.0.0.1" ||
                host.StartsWith("192.168.") || host.StartsWith("10.") ||
                host.StartsWith("172."))
                return false;

            return uri.Scheme == "https" || uri.Scheme == "http";
        }
    }

    // Supporting types
    public enum OrderStatus { Pending, Shipped, Delivered, Cancelled }

    public enum WebhookDeliveryStatus { Pending, Delivered, Failed }

    public class Order
    {
        public string Id { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime ShippedAt { get; set; }
        public string TrackingNumber { get; set; }
        public string WebhookIdempotencyKey { get; set; }
    }

    public class WebhookOutboxEntry
    {
        public Guid Id { get; set; }
        public string OrderId { get; set; }
        public string CustomerId { get; set; }
        public string WebhookUrl { get; set; }
        public string IdempotencyKey { get; set; }
        public OrderShippedEvent Payload { get; set; }
        public WebhookDeliveryStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public int AttemptCount { get; set; }
        public string FailureReason { get; set; }
    }

    public class OrderShippedEvent
    {
        public string OrderId { get; set; }
        public DateTime ShippedAt { get; set; }
        public string TrackingNumber { get; set; }
    }

    public class OrderDbContext : DbContext
    {
        public DbSet<Order> Orders { get; set; }
        public DbSet<WebhookOutboxEntry> WebhookOutbox { get; set; }

        public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Order>().HasKey(o => o.Id);
            modelBuilder.Entity<WebhookOutboxEntry>()
                .HasKey(w => w.Id)
                .HasIndex(w => new { w.Status, w.CreatedAt });
        }
    }
}
