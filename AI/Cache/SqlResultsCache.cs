using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScrapperGranular.Utils;
using ScrapperGranular.Database;

namespace ScrapperGranular.AI.Cache
{
    /// <summary>
    /// Cache de resultados de queries SQL (Nível 3)
    /// Guarda: Hash(SQL) → Resultados do banco
    /// Benefício: Evita re-execução de SQLs idênticas geradas por perguntas diferentes
    /// </summary>
    public class SqlResultsCache
    {
        private readonly string _connectionString;
        private readonly int _ttlHours;

        // Cache em memória para resultados mais acessados
        private readonly Dictionary<string, CachedSqlResult> _memoryCache;
        private readonly LinkedList<string> _memoryCacheOrder;
        private readonly object _memoryCacheLock = new();
        private const int MAX_MEMORY_CACHE_SIZE = 50;

        public SqlResultsCache(string dbPath, int ttlHours = 24)
        {
            _connectionString = $"Data Source={dbPath}";
            _ttlHours = ttlHours;
            _memoryCache = new Dictionary<string, CachedSqlResult>(MAX_MEMORY_CACHE_SIZE);
            _memoryCacheOrder = new LinkedList<string>();

            // Configurar WAL mode para melhor concorrência
            SqliteHelper.ConfigureWalMode(dbPath);

            EnsureCacheTableExists();
        }

        /// <summary>
        /// Cria tabela de cache de resultados SQL
        /// </summary>
        private void EnsureCacheTableExists()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS sql_results_cache (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        sql_hash TEXT NOT NULL UNIQUE,
                        sql_query TEXT NOT NULL,
                        result_json TEXT NOT NULL,
                        row_count INTEGER NOT NULL,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        expires_at DATETIME NOT NULL,
                        hit_count INTEGER DEFAULT 0,
                        last_accessed DATETIME DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX IF NOT EXISTS idx_sqlres_hash ON sql_results_cache(sql_hash);
                    CREATE INDEX IF NOT EXISTS idx_sqlres_expires ON sql_results_cache(expires_at);
                    CREATE INDEX IF NOT EXISTS idx_sqlres_accessed ON sql_results_cache(last_accessed DESC);
                ";

                using var command = new SqliteCommand(createTableSql, connection);
                command.ExecuteNonQuery();

                Logger.Debug("Tabela sql_results_cache verificada/criada");
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao criar tabela sql_results_cache", ex);
                throw;
            }
        }

        /// <summary>
        /// Busca resultado de SQL em cache
        /// </summary>
        public async Task<CachedSqlResult?> GetCachedResultAsync(string sqlQuery)
        {
            try
            {
                var sqlHash = GenerateSqlHash(sqlQuery);

                // Verificar cache em memória primeiro
                lock (_memoryCacheLock)
                {
                    if (_memoryCache.TryGetValue(sqlHash, out var memCached))
                    {
                        Logger.Debug("SQL result hit em memória (LRU)");
                        return memCached;
                    }
                }

                // Buscar no banco
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT sql_query, result_json, row_count, expires_at
                    FROM sql_results_cache
                    WHERE sql_hash = @hash AND expires_at > @now
                ";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@hash", sqlHash);
                command.Parameters.AddWithValue("@now", DateTime.UtcNow);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var result = new CachedSqlResult
                    {
                        SqlQuery = reader.GetString(0),
                        ResultJson = reader.GetString(1),
                        RowCount = reader.GetInt32(2),
                        ExpiresAt = reader.GetDateTime(3)
                    };

                    // Incrementar hit count
                    await IncrementHitCountAsync(sqlHash);

                    // Adicionar ao cache em memória
                    AddToMemoryCache(sqlHash, result);

                    return result;
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao buscar SQL result em cache", ex);
                return null;
            }
        }

        /// <summary>
        /// Salva resultado de SQL no cache
        /// </summary>
        public async Task SaveResultAsync(string sqlQuery, string resultJson, int rowCount)
        {
            try
            {
                var sqlHash = GenerateSqlHash(sqlQuery);
                var expiresAt = DateTime.UtcNow.AddHours(_ttlHours);

                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var insertSql = @"
                    INSERT INTO sql_results_cache
                    (sql_hash, sql_query, result_json, row_count, created_at, expires_at, last_accessed)
                    VALUES (@hash, @query, @result, @rowCount, @created, @expires, @accessed)
                    ON CONFLICT(sql_hash) DO UPDATE SET
                        result_json = @result,
                        row_count = @rowCount,
                        expires_at = @expires,
                        last_accessed = @accessed,
                        hit_count = hit_count + 1
                ";

                using var command = new SqliteCommand(insertSql, connection);
                command.Parameters.AddWithValue("@hash", sqlHash);
                command.Parameters.AddWithValue("@query", sqlQuery);
                command.Parameters.AddWithValue("@result", resultJson);
                command.Parameters.AddWithValue("@rowCount", rowCount);
                command.Parameters.AddWithValue("@created", DateTime.UtcNow);
                command.Parameters.AddWithValue("@expires", expiresAt);
                command.Parameters.AddWithValue("@accessed", DateTime.UtcNow);

                await command.ExecuteNonQueryAsync();

                Logger.Debug($"Resultado SQL salvo em cache (expira em {_ttlHours}h)");

                // Adicionar ao cache em memória
                var cached = new CachedSqlResult
                {
                    SqlQuery = sqlQuery,
                    ResultJson = resultJson,
                    RowCount = rowCount,
                    ExpiresAt = expiresAt
                };
                AddToMemoryCache(sqlHash, cached);
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao salvar SQL result em cache", ex);
            }
        }

        /// <summary>
        /// Gera hash SHA256 de uma query SQL (normalizada)
        /// </summary>
        private string GenerateSqlHash(string sqlQuery)
        {
            // Normalizar SQL: minúsculo, sem espaços extras, sem comentários
            var normalized = sqlQuery.ToLowerInvariant()
                .Trim()
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\t", " ");

            // Remover espaços múltiplos
            while (normalized.Contains("  "))
                normalized = normalized.Replace("  ", " ");

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToBase64String(hashBytes);
        }

        /// <summary>
        /// Incrementa contador de hits
        /// </summary>
        private async Task IncrementHitCountAsync(string sqlHash)
        {
            try
            {
                await SqliteHelper.ExecuteWithRetryAsync(async () =>
                {
                    using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE sql_results_cache
                        SET hit_count = hit_count + 1,
                            last_accessed = @now
                        WHERE sql_hash = @hash
                    ";

                    using var command = new SqliteCommand(sql, connection);
                    command.Parameters.AddWithValue("@hash", sqlHash);
                    command.Parameters.AddWithValue("@now", DateTime.UtcNow);

                    await command.ExecuteNonQueryAsync();
                }, "Incrementar hit count SQL results");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Erro ao incrementar hit count SQL: {ex.Message}");
            }
        }

        /// <summary>
        /// Adiciona ao cache LRU em memória
        /// </summary>
        private void AddToMemoryCache(string key, CachedSqlResult result)
        {
            lock (_memoryCacheLock)
            {
                if (_memoryCache.ContainsKey(key))
                {
                    _memoryCacheOrder.Remove(key);
                }

                _memoryCache[key] = result;
                _memoryCacheOrder.AddFirst(key);

                while (_memoryCacheOrder.Count > MAX_MEMORY_CACHE_SIZE)
                {
                    var oldest = _memoryCacheOrder.Last!.Value;
                    _memoryCacheOrder.RemoveLast();
                    _memoryCache.Remove(oldest);
                }
            }
        }

        /// <summary>
        /// Remove entradas expiradas
        /// </summary>
        public async Task CleanupExpiredAsync()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = "DELETE FROM sql_results_cache WHERE expires_at < @now";
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@now", DateTime.UtcNow);

                var deleted = await command.ExecuteNonQueryAsync();

                if (deleted > 0)
                {
                    Logger.Info($"SQL results cache: {deleted} entradas expiradas removidas");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao limpar SQL results cache", ex);
            }
        }

        /// <summary>
        /// Limpa todo o cache
        /// </summary>
        public async Task ClearAllAsync()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqliteCommand("DELETE FROM sql_results_cache", connection);
                var deleted = await command.ExecuteNonQueryAsync();

                lock (_memoryCacheLock)
                {
                    _memoryCache.Clear();
                    _memoryCacheOrder.Clear();
                }

                Logger.Success($"SQL results cache limpo: {deleted} entradas removidas");
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao limpar SQL results cache", ex);
                throw;
            }
        }

        /// <summary>
        /// Obtém estatísticas do cache
        /// </summary>
        public async Task<Dictionary<string, object>> GetStatsAsync()
        {
            var stats = new Dictionary<string, object>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // Total de entradas
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM sql_results_cache WHERE expires_at > datetime('now')", connection))
                {
                    stats["total_entries"] = (long?)await cmd.ExecuteScalarAsync() ?? 0;
                }

                // Total de hits
                using (var cmd = new SqliteCommand("SELECT SUM(hit_count) FROM sql_results_cache WHERE expires_at > datetime('now')", connection))
                {
                    stats["total_hits"] = (long?)await cmd.ExecuteScalarAsync() ?? 0;
                }

                // Entradas expiradas
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM sql_results_cache WHERE expires_at <= datetime('now')", connection))
                {
                    stats["expired_entries"] = (long?)await cmd.ExecuteScalarAsync() ?? 0;
                }

                // Top 5 queries mais usadas
                var topQueries = new List<Dictionary<string, object>>();
                using (var cmd = new SqliteCommand(
                    @"SELECT sql_query, hit_count, row_count
                      FROM sql_results_cache
                      WHERE expires_at > datetime('now')
                      ORDER BY hit_count DESC
                      LIMIT 5",
                    connection))
                {
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        topQueries.Add(new Dictionary<string, object>
                        {
                            ["sql"] = reader.GetString(0).Length > 100
                                ? reader.GetString(0).Substring(0, 100) + "..."
                                : reader.GetString(0),
                            ["hits"] = reader.GetInt32(1),
                            ["rows"] = reader.GetInt32(2)
                        });
                    }
                }
                stats["top_queries"] = topQueries;

                // Cache em memória
                lock (_memoryCacheLock)
                {
                    stats["memory_cache_size"] = _memoryCache.Count;
                    stats["memory_cache_max"] = MAX_MEMORY_CACHE_SIZE;
                }

                stats["ttl_hours"] = _ttlHours;

                return stats;
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao obter stats SQL results cache", ex);
                return new Dictionary<string, object> { ["error"] = ex.Message };
            }
        }
    }

    /// <summary>
    /// Resultado de SQL em cache
    /// </summary>
    public record CachedSqlResult
    {
        public string SqlQuery { get; init; } = "";
        public string ResultJson { get; init; } = "";
        public int RowCount { get; init; }
        public DateTime ExpiresAt { get; init; }
    }
}
