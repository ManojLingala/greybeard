using System;
using System.IO;
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

    public StripeWebhookController(IAccountService accountService, IConfiguration config)
    {
        _accountService = accountService;
        _webhookSecret = config["Stripe:WebhookSecret"];
    }

    [HttpPost("stripe")]
    public async Task<IActionResult> HandleStripeWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

        try
        {
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret,
                throwOnApiVersionMismatch: false
            );

            if (stripeEvent.Type == Events.ChargeSucceeded || stripeEvent.Type == "charge.succeeded")
            {
                var charge = stripeEvent.Data.Object as Charge;
                if (charge != null)
                {
                    await HandleChargeSucceeded(charge);
                }
            }

            return Ok();
        }
        catch (StripeException ex)
        {
            return BadRequest($"Stripe verification failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Webhook processing error: {ex.Message}");
        }
    }

    private async Task HandleChargeSucceeded(Charge charge)
    {
        if (string.IsNullOrEmpty(charge.CustomerId))
        {
            return;
        }

        var customerId = charge.CustomerId;
        var amount = charge.Amount / 100m;
        var currency = charge.Currency;

        var idempotencyKey = $"{charge.Id}-{charge.Created:yyyyMMddHHmmss}";

        await _accountService.CreditAccountAsync(customerId, amount, currency, idempotencyKey);
    }
}

public interface IAccountService
{
    Task CreditAccountAsync(string customerId, decimal amount, string currency, string idempotencyKey);
}

public class AccountService : IAccountService
{
    private readonly ILogger<AccountService> _logger;
    private readonly IAccountRepository _repository;

    public AccountService(IAccountRepository repository, ILogger<AccountService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task CreditAccountAsync(string customerId, decimal amount, string currency, string idempotencyKey)
    {
        var account = await _repository.GetAccountByStripeCustomerIdAsync(customerId);
        if (account == null)
        {
            _logger.LogWarning($"Account not found for Stripe customer: {customerId}");
            return;
        }

        var existingCredit = await _repository.GetCreditByIdempotencyKeyAsync(idempotencyKey);
        if (existingCredit != null)
        {
            _logger.LogInformation($"Credit already processed with key: {idempotencyKey}");
            return;
        }

        account.Balance += amount;
        var credit = new AccountCredit
        {
            AccountId = account.Id,
            Amount = amount,
            Currency = currency,
            Reason = "Stripe payment succeeded",
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddCreditAsync(credit);
        await _repository.UpdateAccountAsync(account);

        _logger.LogInformation($"Credited account {account.Id} with {amount} {currency}");
    }
}
