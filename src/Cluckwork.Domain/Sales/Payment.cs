namespace Cluckwork.Domain.Sales;

// A customer payment against a confirmed sales order (spec §10.11). Currency
// is copied from the ORDER at creation — a payment settles the denomination
// the order was invoiced in, whatever the account currency becomes later.
// Partial payments are the normal case; the handler enforces the no-overpay
// rule transactionally (Σ non-voided payments ≤ order total).
// Corrections follow the void pattern (#89): a mistaken payment is voided
// with a reason, never deleted — the row stays for the ledger.
public sealed class Payment : AggregateRoot<Guid>
{
    public const int MaxReferenceLength = 50;
    public const int MaxNoteLength = 500;

    public Guid SalesOrderId { get; private set; }
    public Guid CustomerId { get; private set; }
    public DateOnly PaymentDate { get; private set; }
    public long AmountMinorUnits { get; private set; }
    public string CurrencyCode { get; private set; } = string.Empty;
    public int CurrencyMinorUnit { get; private set; }
    public PaymentMethod Method { get; private set; }
    public string? ReferenceNumber { get; private set; }
    public string? Note { get; private set; }
    public bool Voided { get; private set; }
    public string? VoidReason { get; private set; }
    public int Version { get; private set; }

    private Payment() { }

    public static Payment Create(
        Guid id, Guid accountId, Guid salesOrderId, Guid customerId,
        DateOnly paymentDate, long amountMinorUnits,
        string currencyCode, int currencyMinorUnit, PaymentMethod method,
        string? referenceNumber = null, string? note = null)
    {
        if (amountMinorUnits <= 0)
            throw new ArgumentException("Payment amount must be greater than zero.", nameof(amountMinorUnits));
        if (string.IsNullOrWhiteSpace(currencyCode))
            throw new ArgumentException("Currency code is required.", nameof(currencyCode));
        if (referenceNumber is not null && referenceNumber.Trim().Length > MaxReferenceLength)
            throw new ArgumentException($"Reference cannot exceed {MaxReferenceLength} characters.", nameof(referenceNumber));
        if (note is not null && note.Trim().Length > MaxNoteLength)
            throw new ArgumentException($"Note cannot exceed {MaxNoteLength} characters.", nameof(note));

        return new Payment
        {
            Id = id, AccountId = accountId,
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            PaymentDate = paymentDate,
            AmountMinorUnits = amountMinorUnits,
            CurrencyCode = currencyCode,
            CurrencyMinorUnit = currencyMinorUnit,
            Method = method,
            ReferenceNumber = string.IsNullOrWhiteSpace(referenceNumber) ? null : referenceNumber.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
    }

    public Result Void(string reason)
    {
        if (Voided)
            return Result.Failure(Error.Domain(
                "Payment.AlreadyVoided", "This payment is already voided."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(
                "Payment.ReasonRequired", "A reason is required to void a payment."));
        if (reason.Trim().Length > MaxNoteLength)
            return Result.Failure(Error.Validation(
                "Payment.ReasonTooLong", $"Reason cannot exceed {MaxNoteLength} characters."));

        Voided = true;
        VoidReason = reason.Trim();
        Version++;
        return Result.Success();
    }
}

public enum PaymentMethod { Cash, Check, Card, BankTransfer, MobilePayment, Other }
