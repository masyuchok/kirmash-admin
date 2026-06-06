using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class FinanceService
    {
        private readonly AppDbContext _db;
        private readonly VatReportFinanceSyncService _vatSync;

        public FinanceService( AppDbContext db, VatReportFinanceSyncService vatSync )
        {
            _db = db;
            _vatSync = vatSync;
        }

        public async Task<List<FinancePersonDto>> ListPersonsAsync()
        {
            await FinancePersonSeeder.EnsureDefaultAsync( _db );
            return await _db.FinancePersons
                .AsNoTracking()
                .OrderBy( x => x.SortOrder )
                .ThenBy( x => x.Name )
                .Select( x => ToPersonDto( x ) )
                .ToListAsync();
        }

        public async Task<FinancePersonDto> CreatePersonAsync( string name )
        {
            await FinancePersonSeeder.EnsureDefaultAsync( _db );
            int maxSort = await _db.FinancePersons.MaxAsync( x => (int?)x.SortOrder ) ?? -1;
            FinancePerson row = new()
            {
                Name = name,
                SortOrder = maxSort + 1,
                CreatedAtUtc = DateTime.UtcNow
            };
            _db.FinancePersons.Add( row );
            await _db.SaveChangesAsync();
            return ToPersonDto( row );
        }

        public async Task<FinancePersonDto?> UpdatePersonAsync( int id, string name )
        {
            FinancePerson? row = await _db.FinancePersons.FirstOrDefaultAsync( x => x.Id == id );
            if (row is null) return null;

            row.Name = name;
            await _db.SaveChangesAsync();
            return ToPersonDto( row );
        }

        public async Task<bool> DeletePersonAsync( int id )
        {
            FinancePerson? row = await _db.FinancePersons
                .Include( x => x.Movements )
                .Include( x => x.RecurringExpenses )
                .FirstOrDefaultAsync( x => x.Id == id );
            if (row is null) return false;

            _db.FinancePersons.Remove( row );
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<FinancePersonOverviewDto?> GetPersonOverviewAsync( int personId )
        {
            await FinancePersonSeeder.EnsureDefaultAsync( _db );
            FinancePerson? person = await _db.FinancePersons.AsNoTracking().FirstOrDefaultAsync( x => x.Id == personId );
            if (person is null) return null;

            await ApplyRecurringForCurrentMonthAsync( personId );

            List<FinanceMovement> movements = await _db.FinanceMovements
                .AsNoTracking()
                .Where( x => x.PersonId == personId )
                .OrderByDescending( x => x.MovementDate )
                .ThenByDescending( x => x.Id )
                .ToListAsync();

            List<FinanceRecurringExpense> recurring = await _db.FinanceRecurringExpenses
                .AsNoTracking()
                .Where( x => x.PersonId == personId )
                .OrderBy( x => x.DayOfMonth )
                .ThenBy( x => x.Id )
                .ToListAsync();

            List<int> movementIds = movements.Select( x => x.Id ).ToList();
            Dictionary<int, VatPeriodFinancePayment> vatLinks = await LoadVatLinksByMovementIdsAsync( movementIds );

            return new FinancePersonOverviewDto
            {
                Person = ToPersonDto( person ),
                Summary = BuildSummary( movements ),
                Movements = movements
                    .Select( x => ToMovementDto( x, vatLinks.GetValueOrDefault( x.Id ) ) )
                    .ToList(),
                RecurringExpenses = recurring.Select( ToRecurringDto ).ToList()
            };
        }

        public async Task<FinanceMovementDto?> SetVatPaymentAmountLockedAsync( int movementId, bool locked )
        {
            VatPeriodFinancePayment? link = await _db.VatPeriodFinancePayments
                .FirstOrDefaultAsync( x => x.FinanceMovementId == movementId );
            if (link is null)
            {
                return null;
            }

            link.IsAmountLocked = locked;
            await _db.SaveChangesAsync();

            if (!locked)
            {
                await _vatSync.SyncPeriodAsync( link.PeriodYear, link.PeriodMonth );
            }

            FinanceMovement? movement = await _db.FinanceMovements
                .AsNoTracking()
                .FirstOrDefaultAsync( x => x.Id == movementId );
            if (movement is null)
            {
                return null;
            }

            VatPeriodFinancePayment? vatLink = await _db.VatPeriodFinancePayments
                .AsNoTracking()
                .FirstOrDefaultAsync( x => x.FinanceMovementId == movementId );
            return ToMovementDto( movement, vatLink );
        }

        public async Task<FinanceMovementDto?> CreateMovementAsync( FinanceMovementCreateRequest request )
        {
            if (!TryParseKind( request.Kind, out FinanceMovementKind kind )) return null;
            if (!IsCreatableKind( kind )) return null;
            if (!TryParseDate( request.MovementDate, out DateOnly movementDate )) return null;
            if (!await PersonExistsAsync( request.PersonId )) return null;

            string description = (request.Description ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( description ))
            {
                return null;
            }

            decimal amount = NormalizeAmount( request.Amount );
            if (amount <= 0) return null;

            DateTime now = DateTime.UtcNow;
            FinanceMovement row = new()
            {
                PersonId = request.PersonId,
                Kind = kind,
                Amount = amount,
                Description = description,
                MovementDate = movementDate,
                IsFromRecurring = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.FinanceMovements.Add( row );
            await _db.SaveChangesAsync();
            return await ToMovementDtoAsync( row );
        }

        public async Task<FinanceMovementDto?> UpdateMovementAsync( int id, FinanceMovementUpdateRequest request )
        {
            if (!TryParseKind( request.Kind, out FinanceMovementKind kind )) return null;
            if (!IsCreatableKind( kind )) return null;
            if (!TryParseDate( request.MovementDate, out DateOnly movementDate )) return null;

            FinanceMovement? row = await _db.FinanceMovements.FirstOrDefaultAsync( x => x.Id == id );
            if (row is null) return null;

            string description = (request.Description ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( description )) return null;

            decimal amount = NormalizeAmount( request.Amount );
            if (amount <= 0) return null;

            row.Kind = kind;
            row.Amount = amount;
            row.Description = description;
            row.MovementDate = movementDate;
            row.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return await ToMovementDtoAsync( row );
        }

        public async Task<bool> DeleteMovementAsync( int id )
        {
            FinanceMovement? row = await _db.FinanceMovements
                .Include( x => x.RecurringExpense )
                .FirstOrDefaultAsync( x => x.Id == id );
            if (row is null) return false;

            if (row.RecurringExpenseId.HasValue)
            {
                FinanceRecurringApplication? application = await _db.FinanceRecurringApplications
                    .FirstOrDefaultAsync( x => x.MovementId == id );
                if (application is not null)
                {
                    _db.FinanceRecurringApplications.Remove( application );
                }
            }

            VatPeriodFinancePayment? vatLink = await _db.VatPeriodFinancePayments
                .FirstOrDefaultAsync( l => l.FinanceMovementId == id );
            if (vatLink is not null)
            {
                _db.VatPeriodFinancePayments.Remove( vatLink );
            }

            _db.FinanceMovements.Remove( row );
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<FinanceRecurringExpenseDto?> CreateRecurringAsync( FinanceRecurringExpenseCreateRequest request )
        {
            if (!TryParseKind( request.Kind, out FinanceMovementKind kind )) return null;
            if (!IsCreatableKind( kind )) return null;
            if (!await PersonExistsAsync( request.PersonId )) return null;

            string description = (request.Description ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( description )) return null;

            decimal amount = NormalizeAmount( request.Amount );
            if (amount <= 0) return null;

            int day = ClampDayOfMonth( request.DayOfMonth );

            FinanceRecurringExpense row = new()
            {
                PersonId = request.PersonId,
                Kind = kind,
                Amount = amount,
                Description = description,
                DayOfMonth = day,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            };
            _db.FinanceRecurringExpenses.Add( row );
            await _db.SaveChangesAsync();
            await ApplyRecurringForMonthAsync( row, DateTime.UtcNow.Year, DateTime.UtcNow.Month );
            return ToRecurringDto( row );
        }

        public async Task<FinanceRecurringExpenseDto?> UpdateRecurringAsync( int id, FinanceRecurringExpenseUpdateRequest request )
        {
            if (!TryParseKind( request.Kind, out FinanceMovementKind kind )) return null;
            if (!IsCreatableKind( kind )) return null;

            FinanceRecurringExpense? row = await _db.FinanceRecurringExpenses.FirstOrDefaultAsync( x => x.Id == id );
            if (row is null) return null;

            string description = (request.Description ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace( description )) return null;

            decimal amount = NormalizeAmount( request.Amount );
            if (amount <= 0) return null;

            row.Kind = kind;
            row.Amount = amount;
            row.Description = description;
            row.DayOfMonth = ClampDayOfMonth( request.DayOfMonth );
            row.IsActive = request.IsActive;
            await _db.SaveChangesAsync();
            return ToRecurringDto( row );
        }

        public async Task<bool> DeleteRecurringAsync( int id )
        {
            FinanceRecurringExpense? row = await _db.FinanceRecurringExpenses.FirstOrDefaultAsync( x => x.Id == id );
            if (row is null) return false;

            _db.FinanceRecurringExpenses.Remove( row );
            await _db.SaveChangesAsync();
            return true;
        }

        private async Task ApplyRecurringForCurrentMonthAsync( int personId )
        {
            DateTime now = DateTime.UtcNow;
            List<FinanceRecurringExpense> rows = await _db.FinanceRecurringExpenses
                .Where( x => x.PersonId == personId && x.IsActive )
                .ToListAsync();

            foreach (FinanceRecurringExpense recurring in rows)
            {
                await ApplyRecurringForMonthAsync( recurring, now.Year, now.Month );
            }
        }

        private async Task ApplyRecurringForMonthAsync( FinanceRecurringExpense recurring, int year, int month )
        {
            if (!recurring.IsActive) return;

            bool alreadyApplied = await _db.FinanceRecurringApplications.AnyAsync( x =>
                x.RecurringExpenseId == recurring.Id && x.Year == year && x.Month == month );
            if (alreadyApplied) return;

            int day = Math.Min( recurring.DayOfMonth, DateTime.DaysInMonth( year, month ) );
            DateOnly movementDate = new( year, month, day );
            DateTime now = DateTime.UtcNow;

            FinanceMovement movement = new()
            {
                PersonId = recurring.PersonId,
                Kind = recurring.Kind,
                Amount = recurring.Amount,
                Description = recurring.Description,
                MovementDate = movementDate,
                IsFromRecurring = true,
                RecurringExpenseId = recurring.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.FinanceMovements.Add( movement );
            await _db.SaveChangesAsync();

            _db.FinanceRecurringApplications.Add( new FinanceRecurringApplication
            {
                RecurringExpenseId = recurring.Id,
                Year = year,
                Month = month,
                MovementId = movement.Id,
                AppliedAtUtc = now
            } );
            await _db.SaveChangesAsync();
        }

        private async Task<bool> PersonExistsAsync( int personId ) =>
            await _db.FinancePersons.AnyAsync( x => x.Id == personId );

        private static FinanceSummaryDto BuildSummary( IEnumerable<FinanceMovement> movements )
        {
            decimal outgoing = 0;
            decimal incoming = 0;
            decimal payment = 0;
            decimal kirmaPayout = 0;
            decimal legacyDebtTo = 0;
            decimal legacyDebtFrom = 0;

            foreach (FinanceMovement m in movements)
            {
                switch (m.Kind)
                {
                    case FinanceMovementKind.OutgoingTransfer:
                        outgoing += m.Amount;
                        payment += m.Amount;
                        break;
                    case FinanceMovementKind.IncomingTransfer:
                        incoming += m.Amount;
                        kirmaPayout += m.Amount;
                        break;
                    case FinanceMovementKind.Payment:
                        payment += m.Amount;
                        break;
                    case FinanceMovementKind.KirmaPayout:
                        kirmaPayout += m.Amount;
                        break;
                    case FinanceMovementKind.DebtToKirma:
                        legacyDebtTo += m.Amount;
                        break;
                    case FinanceMovementKind.DebtFromKirma:
                        legacyDebtFrom += m.Amount;
                        break;
                }
            }

            decimal totalPaymentsByPerson = payment;
            decimal totalPayoutsByKirma = kirmaPayout;
            decimal balance = totalPaymentsByPerson + legacyDebtFrom - totalPayoutsByKirma - legacyDebtTo;
            decimal personOwesKirma = balance < 0 ? -balance : 0;
            decimal kirmaOwesPerson = balance > 0 ? balance : 0;

            return new FinanceSummaryDto
            {
                TotalOutgoingTransfer = outgoing,
                TotalIncomingTransfer = incoming,
                TotalPayment = totalPaymentsByPerson,
                TotalKirmaPayout = totalPayoutsByKirma,
                PersonOwesKirma = personOwesKirma,
                KirmaOwesPerson = kirmaOwesPerson
            };
        }

        public static bool TryParseKind( string? raw, out FinanceMovementKind kind )
        {
            kind = default;
            if (string.IsNullOrWhiteSpace( raw )) return false;

            string normalized = raw.Trim().ToLowerInvariant();
            return normalized switch
            {
                "outgoingtransfer" or "outgoing_transfer" or "out" => Assign( FinanceMovementKind.OutgoingTransfer, out kind ),
                "incomingtransfer" or "incoming_transfer" or "in" => Assign( FinanceMovementKind.IncomingTransfer, out kind ),
                "payment" or "аплата" => Assign( FinanceMovementKind.Payment, out kind ),
                "kirmapayout" or "kirma_payout" or "payout" or "выплата" => Assign( FinanceMovementKind.KirmaPayout, out kind ),
                "debttokirma" or "debt_to_kirma" => Assign( FinanceMovementKind.DebtToKirma, out kind ),
                "debtfromkirma" or "debt_from_kirma" => Assign( FinanceMovementKind.DebtFromKirma, out kind ),
                _ => Enum.TryParse( raw, true, out kind )
            };
        }

        private static bool Assign( FinanceMovementKind value, out FinanceMovementKind kind )
        {
            kind = value;
            return true;
        }

        public static string KindToApi( FinanceMovementKind kind ) => kind switch
        {
            FinanceMovementKind.OutgoingTransfer => "outgoingTransfer",
            FinanceMovementKind.IncomingTransfer => "incomingTransfer",
            FinanceMovementKind.Payment => "payment",
            FinanceMovementKind.KirmaPayout => "kirmaPayout",
            FinanceMovementKind.DebtToKirma => "debtToKirma",
            FinanceMovementKind.DebtFromKirma => "debtFromKirma",
            _ => kind.ToString()
        };

        private static bool IsCreatableKind( FinanceMovementKind kind ) =>
            kind is FinanceMovementKind.Payment or FinanceMovementKind.KirmaPayout;

        public static bool TryParseDate( string? raw, out DateOnly date )
        {
            date = default;
            if (string.IsNullOrWhiteSpace( raw )) return false;
            return DateOnly.TryParse( raw.Trim(), out date );
        }

        private static decimal NormalizeAmount( decimal amount ) =>
            Math.Round( amount, 2, MidpointRounding.AwayFromZero );

        private static int ClampDayOfMonth( int day ) => Math.Clamp( day, 1, 28 );

        private static FinancePersonDto ToPersonDto( FinancePerson row ) => new()
        {
            Id = row.Id,
            Name = row.Name,
            SortOrder = row.SortOrder
        };

        private async Task<Dictionary<int, VatPeriodFinancePayment>> LoadVatLinksByMovementIdsAsync(
            IReadOnlyCollection<int> movementIds
        )
        {
            if (movementIds.Count == 0)
            {
                return new Dictionary<int, VatPeriodFinancePayment>();
            }

            return await _db.VatPeriodFinancePayments
                .AsNoTracking()
                .Where( x => movementIds.Contains( x.FinanceMovementId ) )
                .ToDictionaryAsync( x => x.FinanceMovementId );
        }

        private async Task<FinanceMovementDto> ToMovementDtoAsync( FinanceMovement row )
        {
            VatPeriodFinancePayment? vatLink = await _db.VatPeriodFinancePayments
                .AsNoTracking()
                .FirstOrDefaultAsync( x => x.FinanceMovementId == row.Id );
            return ToMovementDto( row, vatLink );
        }

        private static FinanceMovementDto ToMovementDto(
            FinanceMovement row,
            VatPeriodFinancePayment? vatLink = null
        ) => new()
        {
            Id = row.Id,
            PersonId = row.PersonId,
            Kind = KindToApi( row.Kind ),
            Amount = row.Amount,
            Description = row.Description,
            MovementDate = row.MovementDate.ToString( "yyyy-MM-dd" ),
            IsFromRecurring = row.IsFromRecurring,
            RecurringExpenseId = row.RecurringExpenseId,
            IsVatAutoPayment = vatLink is not null,
            IsVatAmountLocked = vatLink?.IsAmountLocked ?? false
        };

        private static FinanceRecurringExpenseDto ToRecurringDto( FinanceRecurringExpense row ) => new()
        {
            Id = row.Id,
            PersonId = row.PersonId,
            Kind = KindToApi( row.Kind ),
            Amount = row.Amount,
            Description = row.Description,
            DayOfMonth = row.DayOfMonth,
            IsActive = row.IsActive
        };
    }
}
