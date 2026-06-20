using System;

public class RefundProcessor
{
    /// <summary>
    /// Calculate and apply a 30% refund to an order total.
    /// </summary>
    /// <param name="orderTotalMinorUnits">Order total in minor units (e.g., cents), never float</param>
    /// <param name="currencyCode">ISO 4217 currency code (e.g., "USD", "EUR")</param>
    /// <param name="idempotencyKey">Unique key to ensure this refund is applied exactly once, even if retried</param>
    /// <param name="orderId">Order identifier for transaction logging and concurrency control</param>
    /// <returns>Refund amount in minor units, or null if refund cannot be applied</returns>
    public RefundResult CalculateAndApplyRefund(
        long orderTotalMinorUnits,
        string currencyCode,
        string idempotencyKey,
        string orderId)
    {
        // greybeard: Money - validate input is positive integer minor-units, currency code is known
        if (orderTotalMinorUnits <= 0)
            throw new ArgumentException("Order total must be positive minor units", nameof(orderTotalMinorUnits));

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Currency code must be valid ISO 4217 code", nameof(currencyCode));

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key required for refund safety", nameof(idempotencyKey));

        // greybeard: Money - calculate 30% refund using integer arithmetic (banker's rounding to nearest minor unit)
        // Never use float: 30% of $100.01 = 3000 minor units / 10000 * 1 = 0.3, rounds to 0 as float (data loss!)
        long refundMinorUnits = (orderTotalMinorUnits * 30 + 5000) / 10000; // +5000 implements round-half-up for symmetry

        // greybeard: Mutation + Concurrency - check idempotency store before proceeding
        // A retried refund request must return the same result without double-crediting the customer
        var existingRefund = CheckIdempotencyStore(idempotencyKey);
        if (existingRefund != null)
        {
            // Refund was already processed; return the cached result
            return existingRefund;
        }

        // greybeard: Concurrency - acquire row-level lock on order to prevent simultaneous refund races
        // Pessimistic lock ensures only one refund calculation proceeds at a time for this order
        using (var txn = BeginOrderTransaction(orderId))
        {
            // greybeard: Concurrency - re-validate order state under lock (could have changed between check and lock)
            var currentOrder = txn.GetOrder(orderId);
            if (currentOrder.Status != OrderStatus.Completed && currentOrder.Status != OrderStatus.PartiallyRefunded)
            {
                return RefundResult.Failure($"Order {orderId} cannot be refunded in state {currentOrder.Status}");
            }

            // greybeard: Partial failure - track refund as "pending" before calling external payment processor
            // Enables compensating action if processor call fails mid-way
            var refundRecord = new RefundRecord
            {
                OrderId = orderId,
                IdempotencyKey = idempotencyKey,
                RefundMinorUnits = refundMinorUnits,
                CurrencyCode = currencyCode,
                Status = RefundStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            txn.InsertRefundRecord(refundRecord);
            txn.Commit();
        }

        // greybeard: External call - call payment processor with timeout and retry strategy
        // Do not block indefinitely; a timeout must be treated as a retriable failure
        var processorResult = CallPaymentProcessor(
            orderId: orderId,
            refundMinorUnits: refundMinorUnits,
            currencyCode: currencyCode,
            idempotencyKey: idempotencyKey,
            timeoutMs: 5000);

        if (!processorResult.Success)
        {
            // greybeard: Partial failure - update refund record to failed state; caller can retry
            UpdateRefundRecord(idempotencyKey, RefundStatus.Failed, processorResult.ErrorCode);
            return RefundResult.Failure($"Payment processor declined refund: {processorResult.ErrorCode}");
        }

        // greybeard: Mutation - mark refund complete and store result in idempotency store
        // Future retries of this idempotencyKey will return this cached result, not re-call processor
        using (var txn = BeginOrderTransaction(orderId))
        {
            txn.UpdateRefundRecord(idempotencyKey, RefundStatus.Completed);
            txn.UpdateOrderStatus(orderId, OrderStatus.PartiallyRefunded);
            txn.Commit();
        }

        var result = new RefundResult
        {
            Success = true,
            RefundMinorUnits = refundMinorUnits,
            CurrencyCode = currencyCode,
            IdempotencyKey = idempotencyKey,
            Message = $"Refund of {refundMinorUnits} {currencyCode} processed successfully"
        };

        // greybeard: Observable - log the successful refund with all context for audit and debugging
        LogRefund(result);
        return result;
    }

    private RefundResult CheckIdempotencyStore(string idempotencyKey)
    {
        // Retrieve cached refund result for this idempotency key
        // Returns null if not yet processed
        throw new NotImplementedException();
    }

    private IOrderTransaction BeginOrderTransaction(string orderId)
    {
        // Begin a transaction with row lock on the order
        throw new NotImplementedException();
    }

    private void UpdateRefundRecord(string idempotencyKey, RefundStatus status, string errorCode = null)
    {
        // Update the refund record status in the database
        throw new NotImplementedException();
    }

    private (bool Success, string ErrorCode) CallPaymentProcessor(
        string orderId,
        long refundMinorUnits,
        string currencyCode,
        string idempotencyKey,
        int timeoutMs)
    {
        // Call external payment processor with timeout and error handling
        // Use exponential backoff with jitter for transient failures
        throw new NotImplementedException();
    }

    private void LogRefund(RefundResult result)
    {
        // Log the refund event for audit trail
        // Never log sensitive payment data or customer PII
        throw new NotImplementedException();
    }
}

public class RefundResult
{
    public bool Success { get; set; }
    public long RefundMinorUnits { get; set; }
    public string CurrencyCode { get; set; }
    public string IdempotencyKey { get; set; }
    public string Message { get; set; }

    public static RefundResult Failure(string message) => new() { Success = false, Message = message };
}

public class RefundRecord
{
    public string OrderId { get; set; }
    public string IdempotencyKey { get; set; }
    public long RefundMinorUnits { get; set; }
    public string CurrencyCode { get; set; }
    public RefundStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum RefundStatus
{
    Pending,
    Completed,
    Failed
}

public enum OrderStatus
{
    Completed,
    PartiallyRefunded
}

public interface IOrderTransaction : IDisposable
{
    dynamic GetOrder(string orderId);
    void InsertRefundRecord(RefundRecord record);
    void UpdateRefundRecord(string idempotencyKey, RefundStatus status);
    void UpdateOrderStatus(string orderId, OrderStatus status);
    void Commit();
}
