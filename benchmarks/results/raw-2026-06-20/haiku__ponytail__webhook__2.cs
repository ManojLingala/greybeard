using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using Stripe.Events;

[ApiController]
[Route("api/webhooks")]
public class StripeWebhookController : ControllerBase
{
    private readonly string _webhookSecret;
    private readonly IAccountService _accountService;

    public StripeWebhookController(IConfiguration config, IAccountService accountService)
    {
        _webhookSecret = config["Stripe:WebhookSecret"];
        _accountService = accountService;
    }

    [HttpPost("stripe")]
    public async Task<IActionResult> HandleStripeWebhook()
    {
        // Read the raw body to verify the signature
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

        // Get the Stripe signature from headers
        var stripeSignature = Request.Headers["Stripe-Signature"].ToString();

        if (string.IsNullOrEmpty(stripeSignature))
            return BadRequest("Missing Stripe-Signature header");

        try
        {
            // Verify the webhook signature
            var @event = EventUtility.ConstructEvent(json, stripeSignature, _webhookSecret);

            // Only handle payment_intent.succeeded events
            if (@event.Type != "charge.succeeded" && @event.Type != "payment_intent.succeeded")
            {
                return Ok();
            }

            // Handle based on event type
            dynamic charge = @event.Data.Object;
            var customerId = charge.Customer;
            var amount = (long)charge.Amount / 100m; // Convert from cents to dollars
            var transactionId = charge.Id;

            // Credit the customer's account
            // Idempotently credit by transaction ID to handle redeliveries
            await _accountService.CreditAccountAsync(
                customerId: customerId,
                amount: amount,
                transactionId: transactionId,
                description: $"Payment received: {transactionId}"
            );

            return Ok();
        }
        catch (StripeException)
        {
            return BadRequest("Invalid signature");
        }
        catch (Exception ex)
        {
            // Log the error but still return 200 to prevent redeliveries for non-recoverable errors
            // In production, implement proper error logging and monitoring
            return StatusCode(500);
        }
    }
}

public interface IAccountService
{
    Task CreditAccountAsync(string customerId, decimal amount, string transactionId, string description);
}

public class AccountService : IAccountService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AccountService> _logger;

    public AccountService(ApplicationDbContext db, ILogger<AccountService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task CreditAccountAsync(string customerId, decimal amount, string transactionId, string description)
    {
        // Check if this transaction has already been processed (idempotency)
        var existing = await _db.Transactions
            .FirstOrDefaultAsync(t => t.StripeTransactionId == transactionId);

        if (existing != null)
        {
            _logger.LogInformation($"Transaction {transactionId} already processed");
            return;
        }

        // Find the customer
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.StripeCustomerId == customerId);

        if (customer == null)
        {
            _logger.LogWarning($"Customer {customerId} not found");
            return;
        }

        // Create a transaction record
        var transaction = new CustomerTransaction
        {
            StripeTransactionId = transactionId,
            CustomerId = customer.Id,
            Amount = amount,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        // Credit the account balance
        customer.Balance += amount;

        _db.Transactions.Add(transaction);
        _db.Customers.Update(customer);

        await _db.SaveChangesAsync();

        _logger.LogInformation($"Credited ${amount} to customer {customerId} for transaction {transactionId}");
    }
}

// Domain models
public class Customer
{
    public int Id { get; set; }
    public string StripeCustomerId { get; set; }
    public string Email { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CustomerTransaction
{
    public int Id { get; set; }
    public string StripeTransactionId { get; set; }
    public int CustomerId { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public Customer Customer { get; set; }
}
