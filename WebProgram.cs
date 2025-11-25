using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScrapperGranular.AI;
using ScrapperGranular.AI.Cache;
using ScrapperGranular.AI.Providers;
using ScrapperGranular.Database;

namespace ScrapperGranular.Web
{
    public class WebProgram
    {
        // Sistema de cache simplificado (2 níveis apenas)
        private static QueryCache? _responseCache = null;                   // Nível 1: Resposta (exact + semantic+LLM)
        private static SqlResultsCache? _sqlResultsCache = null;           // Nível 2: Resultados SQL (exact hash)
        private static readonly object _cacheLock = new object();

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Configurar CORS para desenvolvimento
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
            });

            var app = builder.Build();

            app.UseCors();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // Endpoint para chat com a IA
            app.MapPost("/api/chat", async (ChatRequest request) =>
            {
                try
                {
                    if (request == null || string.IsNullOrWhiteSpace(request.Message))
                    {
                        return Results.BadRequest(new { error = "Mensagem é obrigatória" });
                    }

                    // Verificar se o banco de dados existe
                    var dbPath = request.DatabasePath ?? "casos_oncologicos.db";
                    if (!File.Exists(dbPath))
                    {
                        return Results.BadRequest(new
                        {
                            error = "Banco de dados não encontrado. Execute o modo extrator primeiro!",
                            databasePath = dbPath
                        });
                    }

                    // Verificar API Key
                    var apiKey = request.ApiKey;
                    if (string.IsNullOrWhiteSpace(apiKey))
                    {
                        // Tentar ler do arquivo
                        var apiKeyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".gemini_apikey");
                        if (File.Exists(apiKeyFile))
                        {
                            apiKey = File.ReadAllText(apiKeyFile).Trim();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(apiKey))
                    {
                        return Results.BadRequest(new
                        {
                            error = "API Key do Gemini é obrigatória. Obtenha em https://aistudio.google.com/",
                            needsApiKey = true
                        });
                    }

                    // Inicializar componentes
                    var database = new DatabaseManager(dbPath);
                    var aiProvider = new GeminiProvider(apiKey);

                    // Inicializar sistema de cache multi-camadas (singleton)
                    if (_responseCache == null)
                    {
                        lock (_cacheLock)
                        {
                            if (_responseCache == null)
                            {
                                Console.WriteLine("🚀 Inicializando sistema de cache simplificado (2 níveis)...");

                                // Configurar WAL mode no banco principal
                                SqliteHelper.ConfigureWalMode(dbPath);

                                _responseCache = new QueryCache(dbPath, apiKey, aiProvider: aiProvider);
                                _sqlResultsCache = new SqlResultsCache(dbPath);

                                Console.WriteLine("✅ Cache simplificado inicializado:");
                                Console.WriteLine("   • Nível 1: Resposta Completa (exact + semantic COM validação LLM obrigatória)");
                                Console.WriteLine("   • Nível 2: Resultados SQL (hash exato apenas)");
                            }
                        }
                    }
                    var agent = new AgentAssistant(
                        database,
                        aiProvider,
                        dbPath,
                        _responseCache,
                        _sqlResultsCache
                    );

                    // Processar pergunta (com cache automático)
                    var response = await agent.ProcessQuestionAsync(request.Message);

                    return Results.Ok(new ChatResponse
                    {
                        Response = response,
                        Success = true
                    });
                }
                catch (Exception ex)
                {
                    return Results.Ok(new ChatResponse
                    {
                        Response = $"Erro ao processar sua pergunta: {ex.Message}",
                        Success = false,
                        Error = ex.Message
                    });
                }
            });

            // Endpoint para obter estatísticas do banco
            app.MapGet("/api/stats", async (string? dbPath) =>
            {
                try
                {
                    var path = dbPath ?? "casos_oncologicos.db";
                    if (!File.Exists(path))
                    {
                        return Results.NotFound(new { error = "Banco de dados não encontrado" });
                    }

                    var database = new DatabaseManager(path);
                    var stats = await database.GetStatisticsAsync();

                    return Results.Ok(stats);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // ============================================
            // ENDPOINTS DE CACHE MULTI-CAMADAS
            // ============================================

            // Estatísticas gerais de todos os caches
            app.MapGet("/api/cache/stats/all", async () =>
            {
                try
                {
                    var allStats = new Dictionary<string, object>();

                    if (_responseCache != null)
                        allStats["nivel1_response"] = await _responseCache.GetCacheStatsAsync();

                    if (_sqlResultsCache != null)
                        allStats["nivel2_sql_results"] = await _sqlResultsCache.GetStatsAsync();

                    allStats["cache_enabled"] = _responseCache != null;
                    allStats["total_levels"] = 2;

                    return Results.Ok(allStats);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // Estatísticas por nível individual
            app.MapGet("/api/cache/stats/response", async () =>
            {
                if (_responseCache == null)
                    return Results.Ok(new { enabled = false });
                return Results.Ok(await _responseCache.GetCacheStatsAsync());
            });

            app.MapGet("/api/cache/stats/sql-results", async () =>
            {
                if (_sqlResultsCache == null)
                    return Results.Ok(new { enabled = false });
                return Results.Ok(await _sqlResultsCache.GetStatsAsync());
            });

            // Limpar caches
            app.MapPost("/api/cache/clear/all", async () =>
            {
                try
                {
                    if (_responseCache != null) await _responseCache.ClearAllAsync();
                    if (_sqlResultsCache != null) await _sqlResultsCache.ClearAllAsync();

                    return Results.Ok(new { success = true, message = "Todos os caches limpos (2 níveis)" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/cache/clear/response", async () =>
            {
                if (_responseCache == null)
                    return Results.BadRequest(new { error = "Cache não inicializado" });
                await _responseCache.ClearAllAsync();
                return Results.Ok(new { success = true });
            });

            app.MapPost("/api/cache/clear/sql-results", async () =>
            {
                if (_sqlResultsCache == null)
                    return Results.BadRequest(new { error = "Cache não inicializado" });
                await _sqlResultsCache.ClearAllAsync();
                return Results.Ok(new { success = true });
            });

            // Manutenção
            app.MapPost("/api/cache/cleanup", async () =>
            {
                try
                {
                    if (_responseCache != null) await _responseCache.CleanupExpiredAsync();
                    if (_sqlResultsCache != null) await _sqlResultsCache.CleanupExpiredAsync();

                    return Results.Ok(new { success = true, message = "Cache expirado removido" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // Endpoint para verificar saúde da API
            app.MapGet("/api/health", () =>
            {
                return Results.Ok(new
                {
                    status = "healthy",
                    timestamp = DateTime.UtcNow,
                    version = "2.0.0-multilayer-cache",
                    cache_system = new
                    {
                        enabled = _responseCache != null,
                        layers = new
                        {
                            nivel1_response = _responseCache != null,
                            nivel2_sql_results = _sqlResultsCache != null
                        }
                    }
                });
            });

            Console.WriteLine(@"
╔═══════════════════════════════════════════════════╗
║   🔬 ANÁLISE ONCOLÓGICA IA - WEB INTERFACE        ║
║         SISTEMA DE CACHE SIMPLIFICADO (2 NÍVEIS)  ║
╚═══════════════════════════════════════════════════╝

🌐 Servidor iniciado em:
   http://localhost:5000

📊 API Endpoints Principais:
   POST /api/chat           - Conversar com a IA
   GET  /api/stats          - Estatísticas do banco
   GET  /api/health         - Status completo da API

⚡ Sistema de Cache Simplificado (2 Níveis - ZERO Falsos Positivos):
   Nível 1: Resposta Completa (Exact + Semantic COM validação LLM)
   Nível 2: Resultados SQL (hash exato apenas)

📈 Endpoints de Cache:
   GET  /api/cache/stats/all        - Stats de todos os níveis
   GET  /api/cache/stats/response   - Nível 1
   GET  /api/cache/stats/sql-results - Nível 2
   POST /api/cache/clear/all        - Limpar tudo
   POST /api/cache/cleanup          - Remover expirados

✅ Garantia: Todos os erros são da IA, não do cache
   • Cache exact: 100% confiável
   • Cache semantic: SEMPRE validado pela IA

Pressione Ctrl+C para parar o servidor
────────────────────────────────────────────────────
");

            app.Run("http://localhost:5000");
        }
    }

    public record ChatRequest
    {
        public string Message { get; set; } = "";
        public string? ApiKey { get; set; }
        public string? DatabasePath { get; set; }
    }

    public record ChatResponse
    {
        public string Response { get; set; } = "";
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
}
