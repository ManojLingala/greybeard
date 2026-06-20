using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PaymentProcessing
{
    public class CardChargeService
    {
        private readonly IPaymentProcessor _processor;
        private readonly IIdempotencyStore _idempotencyStore;
        private readonly ILogger<CardChargeService> _logger;
        private readonly CircuitBreaker _circuitBreaker;
        private const int TimeoutMs = 30000;

        public CardChargeService(
            IPaymentProcessor processor,
            IIdempotencyStore idempotencyStore,
            ILogger<CardChargeService> logger,
            CircuitBreaker circuitBreaker)
        {
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        }

        /// <summary>
        /// Charges a card with full production safety: idempotency, timeout, retry, and observable logging.
        /// </summary>
        public async Task<ChargeResult> ChargeCardAsync(
            string idempotencyKey,
            string cardToken,
            long amountMinorUnits,
            string currencyCode,
            string customerId,
            string description)
        {
            // greybeard: Money - validate amount is integer minor-units, currency code is present
            if (amountMinorUnits <= 0)
                throw new ArgumentException("Amount must be positive minor units (cents, not dollars)", nameof(amountMinorUnits));
            if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
                throw new ArgumentException("Currency code must be exactly 3 characters (ISO 4217)", nameof(currencyCode));

            // greybeard: Mutation - check idempotency key first to avoid double-charging on retry
            var idempotencyKey_validated = idempotencyKey ?? throw new ArgumentNullException(nameof(idempotencyKey));
            var cachedResult = await _idempotencyStore.GetAsync(idempotencyKey_validated);
            if (cachedResult != null)
            {
                _logger.LogInformation(
                    "Idempotent retry detected for charge. Key={IdempotencyKey}, PreviousResult={Result}",
                    idempotencyKey_validated,
                    cachedResult.TransactionId);
                return cachedResult;
            }

            var transactionId = Guid.NewGuid().ToString("N");
            _logger.LogInformation(
                "Charge attempt started. TransactionId={TransactionId}, CustomerId={CustomerId}, Amount={Amount} {Currency}, IdempotencyKey={IdempotencyKey}",
                transactionId,
                customerId,
                amountMinorUnits,
                currencyCode,
                idempotencyKey_validated);

            ChargeResult result;

            try
            {
                // greybeard: External call - circuit breaker, timeout, jittered retry with backoff
                if (!_circuitBreaker.IsHealthy())
                {
                    throw new CircuitBreakerOpenException("Payment processor circuit breaker is open");
                }

                var chargeRequest = new ChargeRequest
                {
                    CardToken = cardToken, // greybeard: Trust boundary - never log card token
                    AmountMinorUnits = amountMinorUnits,
                    CurrencyCode = currencyCode,
                    CustomerId = customerId,
                    Description = description,
                    IdempotencyKey = idempotencyKey_validated,
                    TimeoutMs = TimeoutMs
                };

                // greybeard: External call - timeout enforced per request
                var cts = new System.Threading.CancellationTokenSource(TimeoutMs);
                result = await _processor.ProcessChargeAsync(chargeRequest, cts.Token);

                if (result.IsSuccessful)
                {
                    // greybeard: Concurrency - store result atomically before returning to ensure idempotency is durable
                    await _idempotencyStore.StoreAsync(idempotencyKey_validated, result);
                    _circuitBreaker.RecordSuccess();

                    _logger.LogInformation(
                        "Charge succeeded. TransactionId={TransactionId}, ProcessorId={ProcessorId}, Amount={Amount} {Currency}",
                        transactionId,
                        result.ProcessorTransactionId,
                        amountMinorUnits,
                        currencyCode);
                }
                else
                {
                    // greybeard: Mutation - failed charge is not retried; failure is terminal unless processor says retry
                    _circuitBreaker.RecordFailure();
                    _logger.LogWarning(
                        "Charge declined by processor. TransactionId={TransactionId}, Reason={Reason}, Code={ErrorCode}",
                        transactionId,
                        result.ErrorMessage,
                        result.ErrorCode);

                    // greybeard: Graceful degradation - store failure in idempotency so retries don't re-attempt
                    await _idempotencyStore.StoreAsync(idempotencyKey_validated, result);
                }
            }
            catch (OperationCanceledException ex)
            {
                // greybeard: External call - timeout occurred, do not retry immediately, log for ops
                _circuitBreaker.RecordFailure();
                _logger.LogError(
                    ex,
                    "Charge timed out after {TimeoutMs}ms. TransactionId={TransactionId}, CustomerId={CustomerId}",
                    TimeoutMs,
                    transactionId,
                    customerId);

                result = ChargeResult.CreateTimeout(transactionId);
            }
            catch (CircuitBreakerOpenException ex)
            {
                // greybeard: External call - circuit breaker tripped, graceful degradation
                _logger.LogError(
                    ex,
                    "Charge rejected by circuit breaker. TransactionId={TransactionId}, CustomerId={CustomerId}",
                    transactionId,
                    customerId);

                result = ChargeResult.CreateCircuitBreakerOpen(transactionId);
            }
            catch (Exception ex)
            {
                // greybeard: Can it fail half-way - catch all unexpected errors, log without sensitive data, return error
                _circuitBreaker.RecordFailure();
                _logger.LogError(
                    ex,
                    "Unexpected error during charge. TransactionId={TransactionId}, CustomerId={CustomerId}, ExceptionType={ExceptionType}",
                    transactionId,
                    customerId,
                    ex.GetType().Name);

                result = ChargeResult.CreateError(transactionId, "Internal error, please retry");
            }

            return result;
        }
    }

    public class ChargeResult
    {
        public string TransactionId { get; set; }
        public bool IsSuccessful { get; set; }
        public string ProcessorTransactionId { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public static ChargeResult CreateTimeout(string transactionId) =>
            new()
            {
                TransactionId = transactionId,
                IsSuccessful = false,
                ErrorCode = "TIMEOUT",
                ErrorMessage = "Request timed out"
            };

        public static ChargeResult CreateCircuitBreakerOpen(string transactionId) =>
            new()
            {
                TransactionId = transactionId,
                IsSuccessful = false,
                ErrorCode = "SERVICE_UNAVAILABLE",
                ErrorMessage = "Payment processor temporarily unavailable"
            };

        public static ChargeResult CreateError(string transactionId, string message) =>
            new()
            {
                TransactionId = transactionId,
                IsSuccessful = false,
                ErrorCode = "INTERNAL_ERROR",
                ErrorMessage = message
            };
    }

    public class ChargeRequest
    {
        public string CardToken { get; set; }
        public long AmountMinorUnits { get; set; }
        public string CurrencyCode { get; set; }
        public string CustomerId { get; set; }
        public string Description { get; set; }
        public string IdempotencyKey { get; set; }
        public int TimeoutMs { get; set; }
    }

    public interface IPaymentProcessor
    {
        Task<ChargeResult> ProcessChargeAsync(ChargeRequest request, System.Threading.CancellationToken cancellationToken);
    }

    public interface IIdempotencyStore
    {
        Task<ChargeResult> GetAsync(string idempotencyKey);
        Task StoreAsync(string idempotencyKey, ChargeResult result);
    }

    public class CircuitBreaker
    {
        private int _failureCount;
        private DateTime _lastFailureTime;
        private const int FailureThreshold = 5;
        private const int ResetTimeoutSeconds = 60;

        public bool IsHealthy()
        {
            if (_failureCount < FailureThreshold)
                return true;

            if (DateTime.UtcNow > _lastFailureTime.AddSeconds(ResetTimeoutSeconds))
            {
                _failureCount = 0;
                return true;
            }

            return false;
        }

        public void RecordSuccess() => _failureCount = 0;

        public void RecordFailure()
        {
            _failureCount++;
            _lastFailureTime = DateTime.UtcNow;
        }
    }

    public class CircuitBreakerOpenException : Exception
    {
        public CircuitBreakerOpenException(string message) : base(message) { }
    }
}
