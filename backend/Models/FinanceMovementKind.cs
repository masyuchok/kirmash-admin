namespace backend.Models
{
    public enum FinanceMovementKind
    {
        OutgoingTransfer = 0,
        IncomingTransfer = 1,
        Payment = 2,
        /// <summary>Legacy manual entry; no longer creatable.</summary>
        DebtToKirma = 3,
        /// <summary>Legacy manual entry; no longer creatable.</summary>
        DebtFromKirma = 4,
        /// <summary>Kirma paid this person.</summary>
        KirmaPayout = 5,
    }
}
