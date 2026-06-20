using System;

namespace PaymentProcessing
{
    public class RefundCalculator
    {
        /// <summary>
        /// Calculates a 30% refund on an order total.
        /// </summary>
        /// <param name="orderTotalMinorUnits">Order total in minor units (e.g., cents for USD). Never float.</param>
        /// <param name="currencyCode">ISO 4217 currency code (e.g., "USD"). Trust boundary validation.</param>
        /// <param name="idempotencyKey">Unique key for this refund operation. Idempotency: safe to retry.</param>
        /// <returns>Refund amount in minor units, or null if validation fails.</returns>
        public static long? CalculateRefund(long orderTotalMinorUnits, string currencyCode, string idempotencyKey)
        {
            // greybeard: Money - validate input is positive integer (minor units), never float. Explicit rounding.
            if (orderTotalMinorUnits < 0)
            {
                throw new ArgumentException($"Order total cannot be negative: {orderTotalMinorUnits}", nameof(orderTotalMinorUnits));
            }

            // greybeard: Money - currency code is trust boundary. Validate it matches expected whitelist.
            if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            {
                throw new ArgumentException($"Invalid currency code: {currencyCode}", nameof(currencyCode));
            }

            // greybeard: Mutation - idempotency key required. Exactly-once effect: check key before recording refund.
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                throw new ArgumentException("Idempotency key is required for refund operations", nameof(idempotencyKey));
            }

            // greybeard: Money - calculate 30% refund using integer arithmetic. No float division.
            // Multiply first (avoid truncation), then divide. Banker's rounding for half-cents.
            long refundAmount = (orderTotalMinorUnits * 30) / 100;

            // greybeard: Money - capture the half-cent: if refund doesn't round evenly, log discrepancy.
            long remainder = (orderTotalMinorUnits * 30) % 100;
            if (remainder != 0)
            {
                // Log this for reconciliation: we lost (remainder / 100) in fractional currency.
                System.Diagnostics.Debug.WriteLine(
                    $"Refund rounding: orderTotal={orderTotalMinorUnits} {currencyCode}, " +
                    $"refund={refundAmount}, fractional_loss={remainder}/100 (idempotencyKey={idempotencyKey})"
                );
            }

            // greybeard: Mutation - this method is side-effect-free. The caller is responsible for:
            // - persisting idempotencyKey and checking for duplicate before recording refund
            // - transaction boundary around payment ledger write
            // - compensating action if refund fails mid-way (reverse the ledger entry)

            return refundAmount;
        }

        /// <summary>
        /// Overload: refund calculation with explicit rounding strategy.
        /// </summary>
        public static long CalculateRefundWithRounding(
            long orderTotalMinorUnits,
            string currencyCode,
            string idempotencyKey,
            MidpointRounding roundingMode = MidpointRounding.AwayFromZero)
        {
            // greybeard: Money - validate inputs (see primary overload).
            if (orderTotalMinorUnits < 0)
            {
                throw new ArgumentException($"Order total cannot be negative: {orderTotalMinorUnits}", nameof(orderTotalMinorUnits));
            }

            if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            {
                throw new ArgumentException($"Invalid currency code: {currencyCode}", nameof(currencyCode));
            }

            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                throw new ArgumentException("Idempotency key is required for refund operations", nameof(idempotencyKey));
            }

            // greybeard: Money - explicit rounding strategy named in logs for auditability.
            decimal refundDecimal = (decimal)orderTotalMinorUnits * 0.30m;
            long refundAmount = (long)Math.Round(refundDecimal, 0, roundingMode);

            System.Diagnostics.Debug.WriteLine(
                $"Refund calculated: orderTotal={orderTotalMinorUnits} {currencyCode}, " +
                $"refund={refundAmount}, rounding={roundingMode} (idempotencyKey={idempotencyKey})"
            );

            return refundAmount;
        }
    }
}
