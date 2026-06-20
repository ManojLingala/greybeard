using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Greybeard.Payment
{
    public class CardChargeService
    {
        private readonly IPaymentProcessor _processor;
        private readonly ILogger<CardChargeService> _logger;
        private readonly IIdempotencyKeyStore _keyStore;

        public CardChargeService(
            IPaymentProcessor processor,
            ILogger<CardChargeService> logger,
            IIdempotencyKeyStore keyStore)
        {
            _processor = processor;
            _logger = logger;
            _keyStore = keyStore;
        }

        public async Task<ChargeResult> ChargeCardAsync(
            string cardToken,
            long amountMinorUnits,
            string currencyCode,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            // greybeard: money - validate amount is positive integer in minor units, never float
            if (amountMinorUnits <= 0)
            {
                _logger.LogError("Charge attempt with invalid amount: {Amount} {Currency}",
                    amountMinorUnits, currencyCode);
                return ChargeResult.Failure("Invalid amount");
            }

            // greybeard: mutation - check idempotency key to prevent duplicate charges
            var existingResult = await _keyStore.GetResultAsync(idempotencyKey, cancellationToken);
            if (existingResult != null)
            {
                _logger.LogInformation(
                    "Idempotent retry detected. Key={IdempotencyKey} PreviousTransactionId={TransactionId}",
                    idempotencyKey, existingResult.TransactionId);
                return existingResult;
            }

            var stopwatch = Stopwatch.StartNew();
            string transactionId = null;

            try
            {
                // greybeard: external call - set timeout, never trust processor won't hang
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(30));

                    // greybeard: trust boundary - do not log card token, ever
                    _logger.LogInformation(
                        "Charging card. Amount={Amount} {Currency} IdempotencyKey={Key}",
                        amountMinorUnits, currencyCode, idempotencyKey);

                    transactionId = await _processor.ChargeAsync(
                        cardToken,
                        amountMinorUnits,
                        currencyCode,
                        idempotencyKey,
                        cts.Token);
                }

                stopwatch.Stop();
                _logger.LogInformation(
                    "Charge succeeded. TransactionId={TransactionId} Amount={Amount} {Currency} DurationMs={Duration}",
                    transactionId, amountMinorUnits, currencyCode, stopwatch.ElapsedMilliseconds);

                var result = ChargeResult.Success(transactionId, amountMinorUnits, currencyCode);

                // greybeard: store result for idempotency before returning to caller
                await _keyStore.StoreResultAsync(idempotencyKey, result, cancellationToken);

                return result;
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
            {
                // greybeard: external call failure - log timeout without sensitive data
                _logger.LogError(ex,
                    "Charge request cancelled by caller. Amount={Amount} {Currency} IdempotencyKey={Key}",
                    amountMinorUnits, currencyCode, idempotencyKey);

                return ChargeResult.Failure("Request cancelled");
            }
            catch (TimeoutException ex)
            {
                // greybeard: half-way failure - processor timeout means charge state is unknown
                // Do NOT retry immediately; must check processor state before retry
                _logger.LogError(ex,
                    "Charge timeout (processor state unknown). Amount={Amount} {Currency} IdempotencyKey={Key} DurationMs={Duration}",
                    amountMinorUnits, currencyCode, idempotencyKey, stopwatch.ElapsedMilliseconds);

                return ChargeResult.Failure("Charge timeout - verify with processor");
            }
            catch (PaymentProcessorException ex)
            {
                stopwatch.Stop();

                // greybeard: external call failure - distinguish retryable vs permanent
                if (ex.IsRetryable)
                {
                    _logger.LogWarning(ex,
                        "Charge failed (retryable). Amount={Amount} {Currency} IdempotencyKey={Key} ErrorCode={ErrorCode} DurationMs={Duration}",
                        amountMinorUnits, currencyCode, idempotencyKey, ex.ErrorCode, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogError(ex,
                        "Charge failed (permanent). Amount={Amount} {Currency} IdempotencyKey={Key} ErrorCode={ErrorCode} DurationMs={Duration}",
                        amountMinorUnits, currencyCode, idempotencyKey, ex.ErrorCode, stopwatch.ElapsedMilliseconds);
                }

                return ChargeResult.Failure(ex.Message);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // greybeard: unknown failure - log without assuming state
                _logger.LogCritical(ex,
                    "Charge failed with unexpected error. Amount={Amount} {Currency} IdempotencyKey={Key} DurationMs={Duration}",
                    amountMinorUnits, currencyCode, idempotencyKey, stopwatch.ElapsedMilliseconds);

                return ChargeResult.Failure("Unexpected error");
            }
        }
    }

    public class ChargeResult
    {
        public bool Success { get; }
        public string TransactionId { get; }
        public long AmountMinorUnits { get; }
        public string CurrencyCode { get; }
        public string ErrorMessage { get; }

        private ChargeResult(bool success, string transactionId, long amountMinorUnits,
            string currencyCode, string errorMessage)
        {
            Success = success;
            TransactionId = transactionId;
            AmountMinorUnits = amountMinorUnits;
            CurrencyCode = currencyCode;
            ErrorMessage = errorMessage;
        }

        public static ChargeResult Success(string transactionId, long amountMinorUnits, string currencyCode)
            => new(true, transactionId, amountMinorUnits, currencyCode, null);

        public static ChargeResult Failure(string errorMessage)
            => new(false, null, 0, null, errorMessage);
    }

    // Interfaces for external dependencies
    public interface IPaymentProcessor
    {
        Task<string> ChargeAsync(string cardToken, long amountMinorUnits,
            string currencyCode, string idempotencyKey, CancellationToken cancellationToken);
    }

    public interface IIdempotencyKeyStore
    {
        Task<ChargeResult> GetResultAsync(string idempotencyKey, CancellationToken cancellationToken);
        Task StoreResultAsync(string idempotencyKey, ChargeResult result, CancellationToken cancellationToken);
    }

    public class PaymentProcessorException : Exception
    {
        public string ErrorCode { get; }
        public bool IsRetryable { get; }

        public PaymentProcessorException(string message, string errorCode, bool isRetryable)
            : base(message)
        {
            ErrorCode = errorCode;
            IsRetryable = isRetryable;
        }
    }
}
