using Microsoft.Data.SqlClient;

namespace MadeeAutomation.E2E.Infrastructure;

/// <summary>
/// Creates a fresh MadeeAutomation_E2E database for every run (your real MadeeAutomation database
/// is never touched) by running the same scripts in /database you run by hand, with @Commit = 1.
/// Also gives tests simple read helpers.
/// </summary>
public sealed class TestDatabase
{
    private readonly E2ESettings _settings;

    public TestDatabase(E2ESettings settings) => _settings = settings;

    public string ConnectionString => _settings.AppConnectionString;

    public async Task RecreateAsync()
    {
        var name = _settings.DatabaseName;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]+$") || !name.Contains("E2E", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to drop database '{name}' - the E2E database name must contain 'E2E'.");

        await using (var master = new SqlConnection(_settings.SqlServer))
        {
            await master.OpenAsync();
            await ExecAsync(master, $@"
                IF DB_ID(N'{name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                CREATE DATABASE [{name}];");
        }
        SqlConnection.ClearAllPools();

        var scripts = Directory.GetFiles(Path.Combine(E2ESettings.SolutionRoot(), "database"), "*.sql").OrderBy(f => f).ToList();
        if (scripts.Count == 0) throw new InvalidOperationException("No scripts found in /database.");

        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        foreach (var script in scripts)
        {
            var sql = await File.ReadAllTextAsync(script);
            const string dryRun = "DECLARE @Commit bit = 0;";
            if (!sql.Contains(dryRun)) throw new InvalidOperationException($"{Path.GetFileName(script)} does not contain '{dryRun}'.");
            await ExecAsync(db, sql.Replace(dryRun, "DECLARE @Commit bit = 1;"));
        }
    }

    public async Task ExecAsync(string sql, params (string name, object? value)[] parameters)
    {
        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        await ExecAsync(db, sql, parameters);
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string name, object? value)[] parameters)
    {
        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        await using var cmd = new SqlCommand(sql, db);
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return result is null || result is DBNull ? default : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    /// <summary>Rows as dictionaries (column name -> value). Fine for the handful of rows tests read.</summary>
    public async Task<List<Dictionary<string, object?>>> RowsAsync(string sql, params (string name, object? value)[] parameters)
    {
        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        await using var cmd = new SqlCommand(sql, db);
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
