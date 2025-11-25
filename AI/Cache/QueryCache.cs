using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScrapperGranular.Utils;
using ScrapperGranular.AI.Interfaces;
using ScrapperGranular.Database;

namespace ScrapperGranular.AI.Cache
{
    /// <summary>
    /// Sistema de cache inteligente com 3 níveis de matching para respostas de IA
    /// Level 1: Exact match (hash exato)
    /// Level 2: Normalized match (texto normalizado)
    /// Level 3: Semantic match (embeddings + similaridade) + LLM validation
    /// </summary>
    public class QueryCache
    {
        private readonly string _connectionString;
        private readonly EmbeddingService _embeddingService;
        private readonly CacheConfig _config;
        private readonly IAIProvider? _aiProvider;

        // Cache em memória para hits recentes (LRU)
        private readonly Dictionary<string, CachedResponse> _memoryCache;
        private readonly LinkedList<string> _memoryCacheOrder;
        private readonly object _memoryCacheLock = new();
        private const int MAX_MEMORY_CACHE_SIZE = 100;

        public QueryCache(string dbPath, string geminiApiKey, CacheConfig? config = null, IAIProvider? aiProvider = null)
        {
            _connectionString = $"Data Source={dbPath}";
            _embeddingService = new EmbeddingService(geminiApiKey);
            _config = config ?? new CacheConfig();
            _aiProvider = aiProvider;
            _memoryCache = new Dictionary<string, CachedResponse>(MAX_MEMORY_CACHE_SIZE);
            _memoryCacheOrder = new LinkedList<string>();

            // Configurar WAL mode para melhor concorrência
            SqliteHelper.ConfigureWalMode(dbPath);

            EnsureCacheTableExists();
        }

        /// <summary>
        /// Garante que a tabela de cache existe no banco
        /// </summary>
        private void EnsureCacheTableExists()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS query_cache (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        question_hash TEXT NOT NULL UNIQUE,
                        question_normalized TEXT NOT NULL,
                        question_original TEXT NOT NULL,
                        response TEXT NOT NULL,
                        embedding BLOB,
                        hit_count INTEGER DEFAULT 1,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        last_accessed DATETIME DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX IF NOT EXISTS idx_cache_hash ON query_cache(question_hash);
                    CREATE INDEX IF NOT EXISTS idx_cache_normalized ON query_cache(question_normalized);
                    CREATE INDEX IF NOT EXISTS idx_cache_accessed ON query_cache(last_accessed DESC);
                    CREATE INDEX IF NOT EXISTS idx_cache_hit_count ON query_cache(hit_count DESC);
                ";

                using var command = new SqliteCommand(createTableSql, connection);
                command.ExecuteNonQuery();

                Logger.Debug("Tabela de cache verificada/criada com sucesso");
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao criar tabela de cache", ex);
                throw;
            }
        }

        /// <summary>
        /// Busca uma resposta em cache usando 3 níveis progressivos
        /// </summary>
        public async Task<CachedResponse?> GetCachedResponseAsync(string question)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                // Level 1: Exact Match (hash exato)
                var exactHash = TextNormalizer.GenerateHash(question);
                var exactMatch = await CheckExactMatchAsync(exactHash);

                if (exactMatch != null)
                {
                    sw.Stop();
                    Logger.CacheHit("EXACT", 1.0f);
                    Logger.Metric("Tempo cache", $"{sw.ElapsedMilliseconds}ms");
                    await IncrementHitCountAsync(exactHash);
                    AddToMemoryCache(exactHash, exactMatch);
                    return exactMatch;
                }

                Logger.CacheMiss("exact");

                // Level 2: Semantic Match COM validação LLM OBRIGATÓRIA
                var normalized = TextNormalizer.Normalize(question);
                var semanticMatch = await CheckSemanticMatchAsync(question, normalized);

                if (semanticMatch != null)
                {
                    // Validação LLM é SEMPRE obrigatória (sem exceção)
                    if (_aiProvider == null)
                    {
                        Logger.Warning("AIProvider não configurado - semantic cache desabilitado");
                        sw.Stop();
                        return null;
                    }

                    var areEquivalent = await ValidateSemanticMatchWithLLM(question, semanticMatch.QuestionOriginal);
                    if (!areEquivalent)
                    {
                        Logger.Warning($"LLM rejeitou match semântico (similarity={semanticMatch.SimilarityScore:F2})");
                        Logger.Info($"  Pergunta atual: {question}");
                        Logger.Info($"  Pergunta cached: {semanticMatch.QuestionOriginal}");
                        sw.Stop();
                        return null;
                    }

                    sw.Stop();
                    Logger.Success($"LLM confirmou match semântico (similarity={semanticMatch.SimilarityScore:F2})");
                    Logger.CacheHit("SEMANTIC+LLM", semanticMatch.SimilarityScore ?? 0);
                    Logger.Metric("Tempo cache", $"{sw.ElapsedMilliseconds}ms");
                    Logger.Info($"Pergunta similar encontrada: \"{semanticMatch.QuestionOriginal}\"");

                    // Incrementar hit na entrada original
                    var semanticHash = TextNormalizer.GenerateHash(semanticMatch.QuestionOriginal);
                    await IncrementHitCountAsync(semanticHash);

                    return semanticMatch;
                }

                Logger.CacheMiss("semantic");

                sw.Stop();
                Logger.Debug($"Cache miss total - tempo gasto: {sw.ElapsedMilliseconds}ms");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao buscar em cache", ex);
                return null; // Falha silenciosa - continuar sem cache
            }
        }

        /// <summary>
        /// Salva uma resposta no cache com embeddings
        /// </summary>
        public async Task SaveAsync(string question, string response)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                // VALIDAÇÃO: Não salvar respostas vazias ou muito curtas
                if (string.IsNullOrWhiteSpace(response) || response.Length < 20)
                {
                    Logger.Warning($"Tentativa de salvar resposta inválida (length={response?.Length ?? 0}) - ignorando");
                    return;
                }

                // VALIDAÇÃO: Não salvar respostas com placeholders não substituídos
                if (response.Contains("{") && response.Contains("}"))
                {
                    var placeholderMatch = System.Text.RegularExpressions.Regex.Match(response, @"\{[\w_]+\}");
                    if (placeholderMatch.Success)
                    {
                        Logger.Warning($"Tentativa de salvar resposta com placeholders não substituídos: {placeholderMatch.Value} - ignorando");
                        return;
                    }
                }

                var exactHash = TextNormalizer.GenerateHash(question);
                var normalized = TextNormalizer.Normalize(question);
                var normalizedHash = TextNormalizer.GenerateHash(normalized);

                // Verificar se já existe (evitar duplicatas)
                var existing = await CheckExactMatchAsync(exactHash);
                if (existing != null)
                {
                    Logger.Debug("Entrada já existe em cache, pulando salvamento");
                    return;
                }

                // Gerar embedding se habilitado
                byte[]? embeddingBytes = null;
                if (_config.EnableSemanticCache)
                {
                    try
                    {
                        var embedding = await _embeddingService.GenerateEmbeddingAsync(normalized);
                        if (embedding != null)
                        {
                            embeddingBytes = EmbeddingService.SerializeEmbedding(embedding);
                            Logger.Debug($"Embedding gerado: {embedding.Length} dimensões");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Falha ao gerar embedding (salvando sem): {ex.Message}");
                        // Continuar sem embedding
                    }
                }

                // Salvar no banco
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var insertSql = @"
                    INSERT INTO query_cache
                    (question_hash, question_normalized, question_original, response, embedding, created_at, last_accessed)
                    VALUES (@hash, @normalized, @original, @response, @embedding, @created, @accessed)
                    ON CONFLICT(question_hash) DO UPDATE SET
                        response = @response,
                        embedding = COALESCE(@embedding, embedding),
                        last_accessed = @accessed,
                        hit_count = hit_count + 1
                ";

                using var command = new SqliteCommand(insertSql, connection);
                command.Parameters.AddWithValue("@hash", exactHash);
                command.Parameters.AddWithValue("@normalized", normalizedHash);
                command.Parameters.AddWithValue("@original", question);
                command.Parameters.AddWithValue("@response", response);
                command.Parameters.AddWithValue("@embedding", embeddingBytes != null ? (object)embeddingBytes : DBNull.Value);
                command.Parameters.AddWithValue("@created", DateTime.UtcNow);
                command.Parameters.AddWithValue("@accessed", DateTime.UtcNow);

                await command.ExecuteNonQueryAsync();

                sw.Stop();
                Logger.Success($"Resposta salva em cache ({sw.ElapsedMilliseconds}ms)");

                // Adicionar ao cache em memória
                var cached = new CachedResponse
                {
                    QuestionOriginal = question,
                    QuestionNormalized = normalized,
                    Response = response,
                    Level = CacheLevel.Exact
                };
                AddToMemoryCache(exactHash, cached);
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao salvar em cache", ex);
                // Não propagar erro - falha de cache não deve quebrar o fluxo
            }
        }

        /// <summary>
        /// Level 1: Verifica match exato por hash
        /// </summary>
        private async Task<CachedResponse?> CheckExactMatchAsync(string hash)
        {
            // Primeiro verificar cache em memória
            lock (_memoryCacheLock)
            {
                if (_memoryCache.TryGetValue(hash, out var memCached))
                {
                    Logger.Debug("Cache hit em memória (LRU)");
                    return memCached;
                }
            }

            // Buscar no banco
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "SELECT question_original, question_normalized, response FROM query_cache WHERE question_hash = @hash";
            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@hash", hash);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new CachedResponse
                {
                    QuestionOriginal = reader.GetString(0),
                    QuestionNormalized = reader.GetString(1),
                    Response = reader.GetString(2),
                    Level = CacheLevel.Exact,
                    SimilarityScore = 1.0f
                };
            }

            return null;
        }


        /// <summary>
        /// Level 3: Verifica match semântico usando embeddings
        /// </summary>
        private async Task<CachedResponse?> CheckSemanticMatchAsync(string question, string normalized)
        {
            try
            {
                // Gerar embedding da pergunta atual
                var questionEmbedding = await _embeddingService.GenerateEmbeddingAsync(normalized);
                if (questionEmbedding == null)
                {
                    Logger.Warning("Falha ao gerar embedding para busca semântica");
                    return null;
                }

                // Buscar todas as entradas com embeddings
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT question_original, question_normalized, response, embedding
                    FROM query_cache
                    WHERE embedding IS NOT NULL
                    ORDER BY last_accessed DESC
                    LIMIT @limit
                ";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@limit", _config.SemanticSearchLimit);

                var candidates = new List<(string original, string normalized, string response, float similarity)>();

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var original = reader.GetString(0);
                    var norm = reader.GetString(1);
                    var response = reader.GetString(2);
                    var embeddingBytes = (byte[])reader.GetValue(3);

                    var cachedEmbedding = EmbeddingService.DeserializeEmbedding(embeddingBytes);
                    var similarity = EmbeddingService.CosineSimilarity(questionEmbedding, cachedEmbedding);

                    if (similarity >= _config.SemanticSimilarityThreshold)
                    {
                        candidates.Add((original, norm, response, similarity));
                    }
                }

                // Retornar o mais similar (validação LLM será feita no GetCachedResponseAsync)
                if (candidates.Any())
                {
                    var best = candidates.OrderByDescending(c => c.similarity).First();

                    return new CachedResponse
                    {
                        QuestionOriginal = best.original,
                        QuestionNormalized = best.normalized,
                        Response = best.response,
                        Level = CacheLevel.Semantic,
                        SimilarityScore = best.similarity
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("Erro na busca semântica", ex);
                return null;
            }
        }

        /// <summary>
        /// Incrementa contador de hits para uma entrada
        /// </summary>
        private async Task IncrementHitCountAsync(string hash)
        {
            try
            {
                await SqliteHelper.ExecuteWithRetryAsync(async () =>
                {
                    using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE query_cache
                        SET hit_count = hit_count + 1,
                            last_accessed = @now
                        WHERE question_hash = @hash
                    ";

                    using var command = new SqliteCommand(sql, connection);
                    command.Parameters.AddWithValue("@hash", hash);
                    command.Parameters.AddWithValue("@now", DateTime.UtcNow);

                    await command.ExecuteNonQueryAsync();
                }, "Incrementar hit count");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Erro ao incrementar hit count: {ex.Message}");
            }
        }

        /// <summary>
        /// Adiciona entrada ao cache LRU em memória
        /// </summary>
        private void AddToMemoryCache(string key, CachedResponse response)
        {
            lock (_memoryCacheLock)
            {
                // Se já existe, remover da posição antiga
                if (_memoryCache.ContainsKey(key))
                {
                    _memoryCacheOrder.Remove(key);
                }

                // Adicionar no início (mais recente)
                _memoryCache[key] = response;
                _memoryCacheOrder.AddFirst(key);

                // Manter tamanho máximo (LRU)
                while (_memoryCacheOrder.Count > MAX_MEMORY_CACHE_SIZE)
                {
                    var oldest = _memoryCacheOrder.Last!.Value;
                    _memoryCacheOrder.RemoveLast();
                    _memoryCache.Remove(oldest);
                }
            }
        }

        /// <summary>
        /// Remove entradas expiradas (TTL)
        /// </summary>
        public async Task CleanupExpiredAsync()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var cutoffDate = DateTime.UtcNow.AddDays(-_config.CacheTtlDays);

                var sql = "DELETE FROM query_cache WHERE last_accessed < @cutoff";
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@cutoff", cutoffDate);

                var deleted = await command.ExecuteNonQueryAsync();

                if (deleted > 0)
                {
                    Logger.Info($"Limpeza de cache: {deleted} entradas antigas removidas");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao limpar cache expirado", ex);
            }
        }

        /// <summary>
        /// Obtém estatísticas do cache
        /// </summary>
        public async Task<Dictionary<string, object>> GetCacheStatsAsync()
        {
            var stats = new Dictionary<string, object>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // Total de entradas
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM query_cache", connection))
                {
                    stats["total_entries"] = (long?)await cmd.ExecuteScalarAsync() ?? 0;
                }

                // Total de hits
                using (var cmd = new SqliteCommand("SELECT SUM(hit_count) FROM query_cache", connection))
                {
                    stats["total_hits"] = (long?)await cmd.ExecuteScalarAsync() ?? 0;
                }

                // Entradas com embeddings
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM query_cache WHERE embedding IS NOT NULL", connection))
                {
                    stats["entries_with_embeddings"] = (long?)await cmd.ExecuteScalarAsync() ?? 0;
                }

                // Top 5 mais acessadas
                var topQuestions = new List<Dictionary<string, object>>();
                using (var cmd = new SqliteCommand(
                    "SELECT question_original, hit_count FROM query_cache ORDER BY hit_count DESC LIMIT 5",
                    connection))
                {
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        topQuestions.Add(new Dictionary<string, object>
                        {
                            ["question"] = reader.GetString(0),
                            ["hits"] = reader.GetInt32(1)
                        });
                    }
                }
                stats["top_questions"] = topQuestions;

                // Cache em memória
                lock (_memoryCacheLock)
                {
                    stats["memory_cache_size"] = _memoryCache.Count;
                    stats["memory_cache_max"] = MAX_MEMORY_CACHE_SIZE;
                }

                return stats;
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao obter estatísticas de cache", ex);
                return new Dictionary<string, object> { ["error"] = ex.Message };
            }
        }

        /// <summary>
        /// Limpa todo o cache (útil para testes/debug)
        /// </summary>
        public async Task ClearAllAsync()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqliteCommand("DELETE FROM query_cache", connection);
                var deleted = await command.ExecuteNonQueryAsync();

                lock (_memoryCacheLock)
                {
                    _memoryCache.Clear();
                    _memoryCacheOrder.Clear();
                }

                Logger.Success($"Cache limpo completamente: {deleted} entradas removidas");
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao limpar cache", ex);
                throw;
            }
        }

        /// <summary>
        /// Valida com LLM se duas perguntas são semanticamente equivalentes
        /// Retorna true se a IA confirmar que são a mesma pergunta
        /// </summary>
        private async Task<bool> ValidateSemanticMatchWithLLM(string question1, string question2)
        {
            try
            {
                var validationPrompt = $@"Você é um validador de perguntas. Analise se as duas perguntas abaixo pedem EXATAMENTE a mesma informação.

IMPORTANTE:
- Responda APENAS ""SIM"" ou ""NÃO""
- ""SIM"" = as perguntas pedem a mesma coisa (mesmo que escritas diferente)
- ""NÃO"" = as perguntas pedem coisas diferentes (mesmo que similares)

Exemplos:
- ""Quantos casos em 2021?"" vs ""Qual total de casos em 2021?"" → SIM
- ""Casos de 2021 a 2023"" vs ""Casos de 2021 a 2024"" → NÃO
- ""Compare homens e mulheres"" vs ""Diferença entre sexos"" → SIM
- ""Top 5 regiões"" vs ""Top 10 regiões"" → NÃO

Pergunta 1: {question1}
Pergunta 2: {question2}

Resposta:";

                Logger.Debug("Validando match semântico com LLM...");
                var response = await _aiProvider!.SendMessageAsync(validationPrompt, new List<Models.Message>());

                var answer = response.Trim().ToUpperInvariant();
                var isEquivalent = answer.Contains("SIM") || answer.Contains("YES");

                Logger.Debug($"LLM respondeu: {response.Trim()} → {(isEquivalent ? "Equivalentes" : "Diferentes")}");

                return isEquivalent;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Erro ao validar com LLM (assumindo NÃO equivalente): {ex.Message}");
                return false; // Em caso de erro, não usar cache
            }
        }
    }
}
