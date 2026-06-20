using System;
using System.Collections.Generic;

public class CardCharger
{
    private static readonly List<ChargeAttempt> _log = new();

    public bool ChargeCard(string cardNumber, decimal amount, string cardholderName)
    {
        var attempt = new ChargeAttempt
        {
            Timestamp = DateTime.UtcNow,
            CardNumber = MaskCardNumber(cardNumber),
            Amount = amount,
            CardholderName = cardholderName,
            Status = "Pending"
        };

        try
        {
            // Simulate card processing
            if (string.IsNullOrEmpty(cardNumber) || cardNumber.Length < 13)
            {
                throw new ArgumentException("Invalid card number");
            }

            if (amount <= 0)
            {
                throw new ArgumentException("Amount must be positive");
            }

            // Placeholder for actual card processing logic
            ProcessPayment(cardNumber, amount);

            attempt.Status = "Success";
            _log.Add(attempt);
            return true;
        }
        catch (Exception ex)
        {
            attempt.Status = "Failed";
            attempt.ErrorMessage = ex.Message;
            _log.Add(attempt);
            return false;
        }
    }

    private void ProcessPayment(string cardNumber, decimal amount)
    {
        // Simulated payment processing - replace with actual payment gateway
        if (cardNumber.EndsWith("0000"))
        {
            throw new InvalidOperationException("Card declined by processor");
        }
    }

    private string MaskCardNumber(string cardNumber)
    {
        if (cardNumber.Length < 4)
            return "****";
        return cardNumber[..^4] + "****";
    }

    public IReadOnlyList<ChargeAttempt> GetAttemptLog() => _log.AsReadOnly();
}

public class ChargeAttempt
{
    public DateTime Timestamp { get; set; }
    public string CardNumber { get; set; }
    public decimal Amount { get; set; }
    public string CardholderName { get; set; }
    public string Status { get; set; }
    public string ErrorMessage { get; set; }
}
