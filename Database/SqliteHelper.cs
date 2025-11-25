using Microsoft.Data.Sqlite;
using System;
using System.Threading.Tasks;

namespace ScrapperGranular.Database;

/// <summary>
/// Helper para configurar SQLite com WAL mode e retry logic
/// Resolve problemas de "database is locked" (Error 5)
/// </summary>
public static class SqliteHelper
{
    private const int MAX_RETRIES = 5;
    private const int BASE_DELAY_MS = 50;

    /// <summary>
    /// Configura WAL mode e otimizações para um banco SQLite
    /// WAL (Write-Ahead Logging) permite leituras durante escritas
    /// </summary>
    public static void ConfigureWalMode(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        // Habilitar WAL mode para melhor concorrência
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            var result = cmd.ExecuteScalar()?.ToString();
            Console.WriteLine($"[SQLite] WAL mode configurado: {result}");
        }

        // Configurar timeout de busy (30 segundos)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=30000;";
            cmd.ExecuteNonQuery();
        }

        // Otimizações adicionais
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA synchronous=NORMAL;
                PRAGMA cache_size=-64000;
                PRAGMA temp_store=MEMORY;
            ";
            cmd.ExecuteNonQuery();
        }

        Console.WriteLine($"[SQLite] Otimizações aplicadas: {dbPath}");
    }

    /// <summary>
    /// Executa uma operação com retry automático em caso de lock
    /// Usa exponential backoff: 50ms, 100ms, 200ms, 400ms, 800ms
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName = "Database operation")
    {
        int attempt = 0;
        Exception lastException = null!;

        while (attempt < MAX_RETRIES)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                attempt++;
                lastException = ex;

                if (attempt >= MAX_RETRIES)
                {
                    Console.WriteLine($"[SQLite] ❌ {operationName} falhou após {MAX_RETRIES} tentativas");
                    break;
                }

                var delayMs = BASE_DELAY_MS * (int)Math.Pow(2, attempt - 1);
                Console.WriteLine($"[SQLite] ⏳ Database locked, retry {attempt}/{MAX_RETRIES} em {delayMs}ms...");

                await Task.Delay(delayMs);
            }
        }

        throw new Exception($"{operationName} falhou após {MAX_RETRIES} tentativas", lastException);
    }

    /// <summary>
    /// Versão síncrona do ExecuteWithRetry
    /// </summary>
    public static T ExecuteWithRetry<T>(
        Func<T> operation,
        string operationName = "Database operation")
    {
        int attempt = 0;
        Exception lastException = null!;

        while (attempt < MAX_RETRIES)
        {
            try
            {
                return operation();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                attempt++;
                lastException = ex;

                if (attempt >= MAX_RETRIES)
                {
                    Console.WriteLine($"[SQLite] ❌ {operationName} falhou após {MAX_RETRIES} tentativas");
                    break;
                }

                var delayMs = BASE_DELAY_MS * (int)Math.Pow(2, attempt - 1);
                Console.WriteLine($"[SQLite] ⏳ Database locked, retry {attempt}/{MAX_RETRIES} em {delayMs}ms...");

                Task.Delay(delayMs).Wait();
            }
        }

        throw new Exception($"{operationName} falhou após {MAX_RETRIES} tentativas", lastException);
    }

    /// <summary>
    /// Versão void async do ExecuteWithRetry
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        string operationName = "Database operation")
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return 0; // dummy return
        }, operationName);
    }
}
