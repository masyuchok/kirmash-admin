using Npgsql;
var conn = new NpgsqlConnection("Host=localhost;Port=5432;Database=kirmashdb;Username=postgres;Password=workTime12369");
try {
  await conn.OpenAsync();
  await using var cmd = new NpgsqlCommand(@"
    SELECT COALESCE(SUM(qty), 0) FROM (
      SELECT i.""Quantity"" AS qty
      FROM ""VatReportRowItems"" i
      JOIN ""VatReportRows"" r ON r.""Id"" = i.""VatReportRowId""
      WHERE i.""ProductTitle"" ILIKE '%орнамент%'
        AND r.""OrderDateUtc"" < TIMESTAMPTZ '2025-02-01'
      UNION ALL
      SELECT c.""Quantity"" AS qty
      FROM ""VatReportCashSales"" c
      WHERE c.""ProductTitle"" ILIKE '%орнамент%'
        AND c.""CreatedAtUtc"" < TIMESTAMPTZ '2025-02-01'
    ) s", conn);
  var n = await cmd.ExecuteScalarAsync();
  Console.WriteLine("before_feb_2025=" + n);
} catch (Exception ex) { Console.WriteLine("ERR: " + ex.Message); }
