using System;

namespace ScrapperGranular.AI.Cache
{
    /// <summary>
    /// Representa uma resposta em cache
    /// </summary>
    public record CachedResponse
    {
        public int Id { get; init; }
        public string QuestionOriginal { get; init; } = "";
        public string QuestionNormalized { get; init; } = "";
        public string QuestionHash { get; init; } = "";
        public float[]? Embedding { get; init; }
        public string SqlQuery { get; init; } = "";
        public string Response { get; init; } = "";
        public int HitCount { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime LastUsedAt { get; init; }
        public DateTime ExpiresAt { get; init; }

        // Informações adicionais (não salvas no banco)
        public CacheLevel? Level { get; init; }
        public float? SimilarityScore { get; init; }
    }

    /// <summary>
    /// Nível de cache onde a resposta foi encontrada
    /// </summary>
    public enum CacheLevel
    {
        Exact,           // Nível 1: Cache exato (hash)
        Normalized,      // Nível 1: Cache normalizado (texto limpo)
        Semantic,        // Nível 1: Cache semântico (embeddings)
        SqlGeneration,   // Nível 2: SQL gerada cacheada
        SqlResults,      // Nível 3: Resultados SQL cacheados
        Pattern,         // Nível 4: Padrão de análise detectado
        MaterializedView // Nível 5: View otimizada usada
    }

    /// <summary>
    /// Configuração do sistema de cache multi-camadas
    /// </summary>
    public record CacheConfig
    {
        // Nível 1: Cache de Resposta Completa
        public bool EnableResponseCache { get; init; } = true;
        public bool EnableSemanticCache { get; init; } = true;
        public bool EnableLlmValidation { get; init; } = true;  // Valida matches semânticos com LLM
        public int ResponseCacheTtlDays { get; init; } = 7;
        public float SemanticSimilarityThreshold { get; init; } = 0.85f;
        public int SemanticSearchLimit { get; init; } = 100;

        // Nível 2: Cache de Geração SQL
        public bool EnableSqlGenerationCache { get; init; } = true;
        public int SqlGenCacheTtlDays { get; init; } = 30;
        public float SqlGenSimilarityThreshold { get; init; } = 0.80f;

        // Nível 3: Cache de Resultados SQL
        public bool EnableSqlResultsCache { get; init; } = true;
        public int SqlResultsCacheTtlHours { get; init; } = 24;

        // Nível 4: Cache de Padrões
        public bool EnablePatternCache { get; init; } = true;
        public int MinPatternOccurrences { get; init; } = 3;

        // Nível 5: Views Materializadas
        public bool EnableMaterializedViews { get; init; } = true;
        public bool AutoOptimizeSql { get; init; } = true;

        // Configurações gerais (retrocompatibilidade)
        public bool Enabled { get; init; } = true;
        public int TtlDays { get; init; } = 7;
        public int CacheTtlDays { get; init; } = 7;
        public float SemanticThreshold { get; init; } = 0.85f;
        public int MaxCacheSize { get; init; } = 10000;
        public bool UseEmbeddings { get; init; } = true;
        public bool VerboseLogs { get; init; } = true;
        public int CleanupIntervalHours { get; init; } = 1;
    }

    /// <summary>
    /// Estatísticas do cache
    /// </summary>
    public record CacheStatistics
    {
        public int TotalQuestions { get; init; }
        public int CacheHits { get; init; }
        public int CacheMisses { get; init; }
        public double HitRate => TotalQuestions > 0 ? (double)CacheHits / TotalQuestions : 0;

        public int ExactHits { get; init; }
        public int NormalizedHits { get; init; }
        public int SemanticHits { get; init; }

        public long TimeSavedMs { get; init; }
        public int ApiCallsSaved { get; init; }
        public int TokensSaved { get; init; }

        public long CacheSizeBytes { get; init; }
        public int TotalEntries { get; init; }
        public DateTime? OldestEntry { get; init; }
        public DateTime? NewestEntry { get; init; }

        public double AvgCachedResponseMs { get; init; }
        public double AvgUncachedResponseMs { get; init; }
    }
}
