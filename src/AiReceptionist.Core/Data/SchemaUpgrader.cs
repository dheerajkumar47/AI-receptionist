using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AiReceptionist.Core.Data;

/// <summary>
/// Keeps an existing SQLite database in step with the model without migrations: <c>EnsureCreated</c> only builds a
/// brand-new database, so columns added in later versions are appended here with <c>ALTER TABLE … ADD COLUMN</c>.
/// Defaults come from the entity's own property initialisers, so existing rows get the same values a new row would.
/// </summary>
public static class SchemaUpgrader
{
    public static async Task<IReadOnlyList<string>> AddMissingColumnsAsync(ReceptionistDbContext db, CancellationToken ct = default)
    {
        var added = new List<string>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        try
        {
            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (table is null) continue;
                var existing = await GetColumnsAsync(connection, table, ct);
                if (existing.Count == 0) continue; // table not created yet; EnsureCreated handles new databases

                var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
                object? sample = entity.ClrType.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(entity.ClrType) : null;

                foreach (var property in entity.GetProperties())
                {
                    var column = property.GetColumnName(store);
                    if (column is null || existing.Contains(column)) continue;

                    var mapping = property.GetRelationalTypeMapping();
                    var value = sample is not null && property.PropertyInfo is { } pi ? pi.GetValue(sample) : null;
                    if (value is null && !property.IsNullable)
                        value = property.ClrType.IsValueType ? Activator.CreateInstance(property.ClrType) : "";
                    if (value is not null && property.GetValueConverter() is { } converter)
                        value = converter.ConvertToProvider(value);

                    var sql = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {property.GetColumnType()}" +
                              (property.IsNullable ? "" : " NOT NULL") +
                              (value is null ? "" : " DEFAULT " + mapping.GenerateSqlLiteral(value));
                    // Plain command: defaults such as "Hi {name}" must not be treated as format placeholders.
                    await using (var command = connection.CreateCommand())
                    {
                        command.CommandText = sql;
                        await command.ExecuteNonQueryAsync(ct);
                    }
                    added.Add($"{table}.{column}");
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        return added;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(System.Data.Common.DbConnection connection, string table, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        return columns;
    }
}
