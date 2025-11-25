using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScrapperGranular.AI;
using ScrapperGranular.AI.Interfaces;
using ScrapperGranular.AI.Providers;
using ScrapperGranular.Models;

// ============================================================================
// SCRAPPER ONCOLÓGICO GRANULAR - Coleta dados específicos com cruzamentos
// ============================================================================

namespace ScrapperGranular
{
    static class Const
    {
        public static readonly Dictionary<int, string> REGIONS = new()
        {
            [1] = "Norte", [2] = "Nordeste", [3] = "Sudeste", [4] = "Sul", [5] = "Centro-Oeste"
        };

        public static readonly Dictionary<string, string> SEXPARAM = new()
        {
            ["ALL"] = "TODAS_AS_CATEGORIAS__",
            ["M"] = "Masculino%7CM%7C1",
            ["F"] = "Feminino%7CF%7C1"
        };

        public static readonly Dictionary<string, string> AGE_GROUPS = new()
        {
            ["0 a 19 anos"] = "0+a+19+anos%7C000-019%7C3",
            ["20 a 24 anos"] = "20+a+24+anos%7C020-024%7C3",
            ["25 a 29 anos"] = "25+a+29+anos%7C025-029%7C3",
            ["30 a 34 anos"] = "30+a+34+anos%7C030-034%7C3",
            ["35 a 39 anos"] = "35+a+39+anos%7C035-039%7C3",
            ["40 a 44 anos"] = "40+a+44+anos%7C040-044%7C3",
            ["45 a 49 anos"] = "45+a+49+anos%7C045-049%7C3",
            ["50 a 54 anos"] = "50+a+54+anos%7C050-054%7C3",
            ["55 a 59 anos"] = "55+a+59+anos%7C055-059%7C3",
            ["60 a 64 anos"] = "60+a+64+anos%7C060-064%7C3",
            ["65 a 69 anos"] = "65+a+69+anos%7C065-069%7C3",
            ["70 a 74 anos"] = "70+a+74+anos%7C070-074%7C3",
            ["75 a 79 anos"] = "75+a+79+anos%7C075-079%7C3",
            ["80 anos e mais"] = "80+anos+e+mais%7C080-999%7C3"
        };

        public static readonly List<string> CODES_DETALHADOS = BuildCidList();
        private static List<string> BuildCidList()
        {
            var l = new List<string>();
            void AddRange(IEnumerable<int> seq) => l.AddRange(seq.Select(n => $"C{n:00}"));
            AddRange(Enumerable.Range(0, 17));
            AddRange(Enumerable.Range(17, 10));
            AddRange(new[] { 30, 31, 32, 33, 34, 37, 38, 39 });
            AddRange(new[] { 40, 41, 43, 44, 45, 46, 47, 48, 49 });
            AddRange(Enumerable.Range(50, 10));
            AddRange(Enumerable.Range(60, 10));
            AddRange(Enumerable.Range(70, 9));
            l.AddRange(new[]
            {
                "C79", "C80", "C81", "C82", "C83", "C84", "C85", "C88",
                "C90", "C91", "C92", "C93", "C94", "C95", "C96", "C97"
            });
            l.AddRange(Enumerable.Range(0, 8).Where(n => n != 8).Select(n => $"D{n:00}"));
            l.Add("D09");
            l.AddRange(Enumerable.Range(37, 12).Select(n => $"D{n:00}"));
            return l;
        }

        // CIDs mais comuns - estratégia de priorização
        public static readonly HashSet<string> COMMON_CIDS = new()
        {
            "C50", "C53", "C54", "C56", "C18", "C19", "C20", "C21", "C22", "C25",
            "C32", "C33", "C34", "C43", "C44", "C61", "C62", "C64", "C67", "C71",
            "C73", "C78", "C80", "C81", "C82", "C83", "C84", "C85", "C90", "C91",
            "C92", "C93", "C94", "C95", "C96", "C97"
        };

        public const string URL_POST = "http://tabnet.datasus.gov.br/cgi/webtabx.exe?PAINEL_ONCO/PAINEL_ONCOLOGIABR.def";
        public const string URL_COOKIE = "http://tabnet.datasus.gov.br/cgi/dhdat.exe?PAINEL_ONCO/PAINEL_ONCOLOGIABR.def";

        public static readonly Regex RE_ADDROWS = new(@"data\.addRows\(\s*\[(.*?)\]\s*\);",
                                                    RegexOptions.Compiled | RegexOptions.Singleline);
        public static readonly Regex RE_LINHA = new(@"\[\s*[""']\s*(\d+)\s+Regi[^""']+[""']\s*,\s*\{v:\s*([\d\.]+)",
                                                  RegexOptions.Compiled);
    }

    // ============================================================================
    // CONFIGURAÇÃO E ESTRATÉGIAS
    // ============================================================================

    public enum ColetaStrategy
    {
        Completa,       // Todas as combinações possíveis
        Hierarquica,    // Totais -> Faixas -> CIDs relevantes
        Seletiva,       // Só CIDs comuns + anos recentes
        Incremental     // Só dados que não existem no banco
    }

    record ConfigGranular(
        List<int> Anos,
        List<string> Regioes,
        List<string> Sexos,
        List<string> FaixasEtarias,
        List<string> Cids,
        ColetaStrategy Strategy,
        string DatabasePath,
        int MaxWorkers = 16,
        int TimeoutSeconds = 45,
        int MaxRetries = 3,
        int SaveBatchSize = 500,
        bool EnableVerboseLogging = true
    );

    public record CasoOncologico(
        int Ano,
        string Regiao,
        string Sexo,
        string FaixaEtaria,
        string Cid,
        int Casos,
        DateTime CreatedAt
    );

    public record JobQuery(
        int Ano,
        string Regiao,
        string Sexo,
        string FaixaEtaria,
        string Cid
    )
    {
        public string ToKey() => $"{Ano}-{Regiao}-{Sexo}-{FaixaEtaria}-{Cid}";
        public override string ToString() => ToKey();
    }

    // ============================================================================
    // SISTEMA DE BANCO DE DADOS SQLite
    // ============================================================================

    public class DatabaseManager
    {
        private readonly string _connectionString;
        private readonly object _lock = new();

        public DatabaseManager(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS casos_oncologicos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ano INTEGER NOT NULL,
                    regiao TEXT NOT NULL,
                    sexo TEXT NOT NULL,
                    faixa_etaria TEXT NOT NULL,
                    cid TEXT NOT NULL,
                    casos INTEGER NOT NULL,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    
                    UNIQUE(ano, regiao, sexo, faixa_etaria, cid)
                );

                CREATE INDEX IF NOT EXISTS idx_ano_regiao ON casos_oncologicos(ano, regiao);
                CREATE INDEX IF NOT EXISTS idx_cid_sexo ON casos_oncologicos(cid, sexo);
                CREATE INDEX IF NOT EXISTS idx_faixa_ano ON casos_oncologicos(faixa_etaria, ano);
                CREATE INDEX IF NOT EXISTS idx_casos_desc ON casos_oncologicos(casos DESC);
                CREATE INDEX IF NOT EXISTS idx_created_at ON casos_oncologicos(created_at);

                CREATE TABLE IF NOT EXISTS coleta_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );
            ";

            using var command = new SqliteCommand(createTableSql, connection);
            command.ExecuteNonQuery();
        }

        public async Task<List<CasoOncologico>> BulkInsertAsync(List<CasoOncologico> casos)
        {
            var inserted = new List<CasoOncologico>();
            
            lock (_lock)
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                try
                {
                    var insertSql = @"
                        INSERT OR REPLACE INTO casos_oncologicos 
                        (ano, regiao, sexo, faixa_etaria, cid, casos, created_at)
                        VALUES (@ano, @regiao, @sexo, @faixa, @cid, @casos, @created)";

                    using var command = new SqliteCommand(insertSql, connection, transaction);

                    foreach (var caso in casos)
                    {
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@ano", caso.Ano);
                        command.Parameters.AddWithValue("@regiao", caso.Regiao);
                        command.Parameters.AddWithValue("@sexo", caso.Sexo);
                        command.Parameters.AddWithValue("@faixa", caso.FaixaEtaria);
                        command.Parameters.AddWithValue("@cid", caso.Cid);
                        command.Parameters.AddWithValue("@casos", caso.Casos);
                        command.Parameters.AddWithValue("@created", caso.CreatedAt);

                        if (command.ExecuteNonQuery() > 0)
                            inserted.Add(caso);
                    }

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            return inserted;
        }

        public HashSet<string> GetExistingJobs()
        {
            var existing = new HashSet<string>();
            
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var sql = "SELECT ano, regiao, sexo, faixa_etaria, cid FROM casos_oncologicos";
            using var command = new SqliteCommand(sql, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var job = new JobQuery(
                    reader.GetInt32("ano"),
                    reader.GetString("regiao"),
                    reader.GetString("sexo"),
                    reader.GetString("faixa_etaria"),
                    reader.GetString("cid")
                );
                existing.Add(job.ToKey());
            }

            return existing;
        }

        public async Task<Dictionary<string, object>> GetStatisticsAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var stats = new Dictionary<string, object>();

            // Contagem total
            var countSql = "SELECT COUNT(*) FROM casos_oncologicos";
            using (var command = new SqliteCommand(countSql, connection))
            {
                stats["total_records"] = await command.ExecuteScalarAsync();
            }

            // Casos por ano
            var yearSql = @"
                SELECT ano, SUM(casos) as total_casos 
                FROM casos_oncologicos 
                GROUP BY ano 
                ORDER BY ano";
            
            var yearStats = new Dictionary<int, long>();
            using (var command = new SqliteCommand(yearSql, connection))
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    yearStats[reader.GetInt32("ano")] = reader.GetInt64("total_casos");
                }
            }
            stats["casos_por_ano"] = yearStats;

            // Top 10 CIDs
            var cidSql = @"
                SELECT cid, SUM(casos) as total_casos 
                FROM casos_oncologicos 
                GROUP BY cid 
                ORDER BY total_casos DESC 
                LIMIT 10";
            
            var cidStats = new Dictionary<string, long>();
            using (var command = new SqliteCommand(cidSql, connection))
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    cidStats[reader.GetString("cid")] = reader.GetInt64("total_casos");
                }
            }
            stats["top_cids"] = cidStats;

            return stats;
        }

        public async Task OptimizeDatabaseAsync()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // VACUUM para otimizar o banco e reduzir fragmentação
                using (var vacuumCommand = new SqliteCommand("VACUUM;", connection))
                {
                    await vacuumCommand.ExecuteNonQueryAsync();
                }

                // REINDEX para reconstruir índices
                using (var reindexCommand = new SqliteCommand("REINDEX;", connection))
                {
                    await reindexCommand.ExecuteNonQueryAsync();
                }

                // ANALYZE para atualizar estatísticas do otimizador
                using (var analyzeCommand = new SqliteCommand("ANALYZE;", connection))
                {
                    await analyzeCommand.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                // Log but don't throw - optimization is not critical
                Console.WriteLine($"[WARN] Falha na otimização do banco: {ex.Message}");
            }
        }

        public async Task<List<CasoOncologico>> QueryAsync(
            int? ano = null,
            string? regiao = null,
            string? sexo = null,
            string? faixaEtaria = null,
            string? cid = null,
            int limit = 1000)
        {
            var cases = new List<CasoOncologico>();
            var sql = new StringBuilder("SELECT * FROM casos_oncologicos WHERE 1=1");
            var parameters = new List<SqliteParameter>();

            if (ano.HasValue)
            {
                sql.Append(" AND ano = @ano");
                parameters.Add(new SqliteParameter("@ano", ano.Value));
            }

            if (!string.IsNullOrEmpty(regiao))
            {
                sql.Append(" AND regiao = @regiao");
                parameters.Add(new SqliteParameter("@regiao", regiao));
            }

            if (!string.IsNullOrEmpty(sexo))
            {
                sql.Append(" AND sexo = @sexo");
                parameters.Add(new SqliteParameter("@sexo", sexo));
            }

            if (!string.IsNullOrEmpty(faixaEtaria))
            {
                sql.Append(" AND faixa_etaria = @faixa");
                parameters.Add(new SqliteParameter("@faixa", faixaEtaria));
            }

            if (!string.IsNullOrEmpty(cid))
            {
                sql.Append(" AND cid = @cid");
                parameters.Add(new SqliteParameter("@cid", cid));
            }

            sql.Append($" ORDER BY casos DESC LIMIT {limit}");

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqliteCommand(sql.ToString(), connection);
            command.Parameters.AddRange(parameters.ToArray());

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                cases.Add(new CasoOncologico(
                    reader.GetInt32("ano"),
                    reader.GetString("regiao"),
                    reader.GetString("sexo"),
                    reader.GetString("faixa_etaria"),
                    reader.GetString("cid"),
                    reader.GetInt32("casos"),
                    reader.GetDateTime("created_at")
                ));
            }

            return cases;
        }
    }

    // ============================================================================
    // SISTEMA DE COLETA E ESTRATÉGIAS
    // ============================================================================

    class ScrapperEngine
    {
        private HttpClient _httpClient;
        private readonly DatabaseManager _database;
        private readonly ConfigGranular _config;
        private readonly SemaphoreSlim _semaphore;
        private readonly object _statsLock = new();
        
        private int _totalRequests;
        private int _successfulRequests;
        private int _failedRequests;
        private int _emptyDataRequests;
        private int _recordsInserted;
        private DateTime _startTime;
        private DateTime _lastPerformanceCheck;
        private List<double> _recentSpeeds = new();

        public ScrapperEngine(ConfigGranular config, DatabaseManager database)
        {
            _config = config;
            _database = database;
            _semaphore = new SemaphoreSlim(config.MaxWorkers);
            _startTime = DateTime.Now;
            _lastPerformanceCheck = DateTime.Now;

            _httpClient = CreateHttpClient();
        }

        private HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = _config.MaxWorkers,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2), // Reduzido para renovar mais frequentemente
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
            };

            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Origin", "http://tabnet.datasus.gov.br");
            client.DefaultRequestHeaders.Referrer = new Uri(Const.URL_COOKIE);

            return client;
        }

        private void RefreshHttpClientIfNeeded()
        {
            // Renovar HttpClient a cada 1000 requests para evitar degradação
            if (_totalRequests % 1000 == 0 && _totalRequests > 0)
            {
                try
                {
                    _httpClient?.Dispose();
                    _httpClient = CreateHttpClient();
                    LogInfo($"HttpClient renovado após {_totalRequests} requests para manter performance");
                }
                catch (Exception ex)
                {
                    LogError($"Erro ao renovar HttpClient: {ex.Message}");
                }
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                await _httpClient.GetAsync(Const.URL_COOKIE + "?PAINEL_ONCO/PAINEL_ONCOLOGIABR.def=");
                LogInfo("Cookie inicial obtido com sucesso");
            }
            catch (Exception ex)
            {
                LogError($"Falha ao obter cookie inicial: {ex.Message}");
            }
        }

        public async Task<List<JobQuery>> GenerateJobsAsync()
        {
            var jobs = new List<JobQuery>();

            switch (_config.Strategy)
            {
                case ColetaStrategy.Completa:
                    jobs = GenerateCompleteJobs();
                    break;

                case ColetaStrategy.Hierarquica:
                    jobs = GenerateHierarchicalJobs();
                    break;

                case ColetaStrategy.Seletiva:
                    jobs = GenerateSelectiveJobs();
                    break;

                case ColetaStrategy.Incremental:
                    jobs = await GenerateIncrementalJobsAsync();
                    break;
            }

            LogInfo($"Gerados {jobs.Count:N0} jobs para estratégia {_config.Strategy}");
            return jobs;
        }

        private List<JobQuery> GenerateCompleteJobs()
        {
            return (from ano in _config.Anos
                    from regiao in _config.Regioes
                    from sexo in _config.Sexos
                    from faixa in _config.FaixasEtarias
                    from cid in _config.Cids
                    select new JobQuery(ano, regiao, sexo, faixa, cid)).ToList();
        }

        private List<JobQuery> GenerateHierarchicalJobs()
        {
            var jobs = new List<JobQuery>();

            // Nível 1: Totais gerais (sem faixa/CID específicos)
            foreach (var ano in _config.Anos)
            foreach (var regiao in _config.Regioes)
            foreach (var sexo in _config.Sexos)
            {
                jobs.Add(new JobQuery(ano, regiao, sexo, "TODAS", "TODOS"));
            }

            // Nível 2: Por faixa etária (sem CID específico)
            foreach (var ano in _config.Anos)
            foreach (var regiao in _config.Regioes)
            foreach (var sexo in _config.Sexos)
            foreach (var faixa in _config.FaixasEtarias.Where(f => f != "TODAS"))
            {
                jobs.Add(new JobQuery(ano, regiao, sexo, faixa, "TODOS"));
            }

            // Nível 3: CIDs comuns com todas as faixas
            foreach (var ano in _config.Anos)
            foreach (var regiao in _config.Regioes)
            foreach (var sexo in _config.Sexos)
            foreach (var faixa in _config.FaixasEtarias.Where(f => f != "TODAS"))
            foreach (var cid in _config.Cids.Where(c => Const.COMMON_CIDS.Contains(c)))
            {
                jobs.Add(new JobQuery(ano, regiao, sexo, faixa, cid));
            }

            // Nível 4: CIDs raros apenas com totais
            foreach (var ano in _config.Anos)
            foreach (var regiao in _config.Regioes)
            foreach (var sexo in _config.Sexos)
            foreach (var cid in _config.Cids.Where(c => !Const.COMMON_CIDS.Contains(c)))
            {
                jobs.Add(new JobQuery(ano, regiao, sexo, "TODAS", cid));
            }

            return jobs;
        }

        private List<JobQuery> GenerateSelectiveJobs()
        {
            var jobs = new List<JobQuery>();
            var recentYears = _config.Anos.Where(a => a >= DateTime.Now.Year - 5).ToList();

            // CIDs mais comuns + anos recentes + todas as combinações
            foreach (var ano in recentYears)
            foreach (var regiao in _config.Regioes)
            foreach (var sexo in _config.Sexos)
            foreach (var faixa in _config.FaixasEtarias)
            foreach (var cid in _config.Cids.Where(c => Const.COMMON_CIDS.Contains(c)))
            {
                jobs.Add(new JobQuery(ano, regiao, sexo, faixa, cid));
            }

            // CIDs raros + anos recentes + só totais
            foreach (var ano in recentYears)
            foreach (var regiao in _config.Regioes)
            foreach (var sexo in _config.Sexos)
            foreach (var cid in _config.Cids.Where(c => !Const.COMMON_CIDS.Contains(c)))
            {
                jobs.Add(new JobQuery(ano, regiao, sexo, "TODAS", cid));
            }

            // Anos antigos + só CIDs muito comuns
            var veryCommonCids = new[] { "C50", "C53", "C18", "C19", "C33", "C34", "C61" };
            foreach (var ano in _config.Anos.Where(a => a < DateTime.Now.Year - 5))
            foreach (var regiao in _config.Regioes)
            foreach (var sexo in _config.Sexos)
            foreach (var cid in veryCommonCids)
            {
                jobs.Add(new JobQuery(ano, regiao, sexo, "TODAS", cid));
            }

            return jobs;
        }

        private async Task<List<JobQuery>> GenerateIncrementalJobsAsync()
        {
            var allJobs = GenerateCompleteJobs();
            var existingJobs = _database.GetExistingJobs();
            
            var pendingJobs = allJobs.Where(job => !existingJobs.Contains(job.ToKey())).ToList();
            
            LogInfo($"Jobs incrementais: {pendingJobs.Count:N0} pendentes de {allJobs.Count:N0} total");
            return pendingJobs;
        }

        public async Task<List<CasoOncologico>> ProcessJobsAsync(List<JobQuery> jobs)
        {
            var allCases = new List<CasoOncologico>();
            var processingBatch = new List<CasoOncologico>();
            var completed = 0;

            LogInfo($"Iniciando processamento de {jobs.Count:N0} jobs com {_config.MaxWorkers} workers");

            var tasks = jobs.Select(async job =>
            {
                await _semaphore.WaitAsync();
                try
                {
                    var cases = await ProcessSingleJobAsync(job);
                    if (cases.Any())
                    {
                        lock (_statsLock)
                        {
                            processingBatch.AddRange(cases);

                            // Salvar em lotes
                            if (processingBatch.Count >= _config.SaveBatchSize)
                            {
                                var toSave = processingBatch.ToList();
                                processingBatch.Clear();
                                _ = Task.Run(async () =>
                                {
                                    var saved = await _database.BulkInsertAsync(toSave);
                                    lock (_statsLock)
                                    {
                                        allCases.AddRange(saved);
                                    }
                                });
                            }

                            var currentCompleted = Interlocked.Increment(ref completed);
                            if (currentCompleted % 100 == 0 || currentCompleted == jobs.Count)
                            {
                                ShowProgress(currentCompleted, jobs.Count);
                            }
                        }
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Salvar lote final
            if (processingBatch.Any())
            {
                var saved = await _database.BulkInsertAsync(processingBatch);
                allCases.AddRange(saved);
            }

            LogInfo($"Processamento concluído: {allCases.Count:N0} casos salvos");
            return allCases;
        }

        private async Task<List<CasoOncologico>> ProcessSingleJobAsync(JobQuery job)
        {
            var cases = new List<CasoOncologico>();

            for (int attempt = 1; attempt <= _config.MaxRetries; attempt++)
            {
                try
                {
                    var currentRequests = Interlocked.Increment(ref _totalRequests);
                    
                    // Renovar HttpClient periodicamente
                    RefreshHttpClientIfNeeded();
                    
                    // Garbage collection periódico
                    PerformMaintenanceIfNeeded(currentRequests);

                    var payload = BuildPayload(job);
                    using var content = new StringContent(payload, Encoding.UTF8, "application/x-www-form-urlencoded");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.TimeoutSeconds));
                    
                    using var response = await _httpClient.PostAsync(Const.URL_POST, content, cts.Token);
                    response.EnsureSuccessStatusCode();
                    
                    var html = await response.Content.ReadAsStringAsync();
                    var regionData = ParseHtml(html, job);

                    if (regionData != null)
                    {
                        foreach (var (regiao, casos) in regionData)
                        {
                            if (casos > 0)
                            {
                                cases.Add(new CasoOncologico(
                                    job.Ano,
                                    regiao,
                                    job.Sexo,
                                    job.FaixaEtaria,
                                    job.Cid,
                                    casos,
                                    DateTime.Now
                                ));
                            }
                        }

                        Interlocked.Increment(ref _successfulRequests);
                        if (!cases.Any())
                            Interlocked.Increment(ref _emptyDataRequests);

                        return cases;
                    }
                }
                catch when (attempt < _config.MaxRetries)
                {
                    await Task.Delay(300 + Random.Shared.Next(700));
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedRequests);
                    LogError($"Job {job} falhou definitivamente: {ex.Message}");
                    break;
                }
            }

            return cases;
        }

        private void PerformMaintenanceIfNeeded(int currentRequests)
        {
            // Garbage collection a cada 500 requests
            if (currentRequests % 500 == 0 && currentRequests > 0)
            {
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect(); // Segunda coleta para objetos finalizados
                    
                    LogInfo($"Garbage collection executado após {currentRequests} requests");
                }
                catch (Exception ex)
                {
                    LogError($"Erro durante garbage collection: {ex.Message}");
                }
            }

            // Otimização do banco a cada 5000 registros inseridos
            if (_recordsInserted % 5000 == 0 && _recordsInserted > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _database.OptimizeDatabaseAsync();
                        LogInfo($"Banco otimizado após {_recordsInserted} registros inseridos");
                    }
                    catch (Exception ex)
                    {
                        LogError($"Erro na otimização do banco: {ex.Message}");
                    }
                });
            }
        }

        private string BuildPayload(JobQuery job)
        {
            var payload = 
                "Linha=Regi%E3o+-+resid%EAncia%7CSUBSTR%28CO_MUNICIPIO_RESIDENCIA%2C1%2C1%29%7C1" +
                "%7Cterritorio%5Cbr_regiao.cnv&Coluna=--N%E3o-Ativa--&Incremento=Casos%7C%3D+count%28*%29" +
                $"&PAno+do+diagn%F3stico={job.Ano}%7C{job.Ano}%7C4&XRegi%E3o+-+resid%EAncia=TODAS_AS_CATEGORIAS__" +
                "&XRegi%E3o+-+diagn%F3stico=TODAS_AS_CATEGORIAS__&XRegi%E3o+-+tratamento=TODAS_AS_CATEGORIAS__" +
                "&XUF+da+resid%EAncia=TODAS_AS_CATEGORIAS__&XUF+do+diagn%F3stico=TODAS_AS_CATEGORIAS__" +
                "&XUF+do+tratamento=TODAS_AS_CATEGORIAS__&SRegi%E3o+de+Saude+-+resid%EAncia=TODAS_AS_CATEGORIAS__" +
                "&SRegi%E3o+de+Saude+-+diagn%F3stico=TODAS_AS_CATEGORIAS__&SRegi%E3o+de+Saude+-+tratamento=TODAS_AS_CATEGORIAS__" +
                "&SMunic%ED%ADpio+da+resid%EAncia=TODAS_AS_CATEGORIAS__&SMunic%ED%ADpio+do+diagn%F3stico=TODAS_AS_CATEGORIAS__" +
                "&SMunic%ED%ADpio+do+tratamento=TODAS_AS_CATEGORIAS__&XDiagn%F3stico=TODAS_AS_CATEGORIAS__" +
                "&XDiagn%F3stico+Detalhado=TODAS_AS_CATEGORIAS__" +
                $"&XSexo={Const.SEXPARAM[job.Sexo]}&XFaixa+et%E1ria=TODAS_AS_CATEGORIAS__&XIdade=TODAS_AS_CATEGORIAS__" +
                "&XM%EAs%2FAno+do+diagn%F3stico=TODAS_AS_CATEGORIAS__" +
                "&nomedef=PAINEL_ONCO%2FPAINEL_ONCOLOGIABR.def&grafico=";

            // Aplicar filtros específicos
            if (job.FaixaEtaria != "TODAS" && Const.AGE_GROUPS.ContainsKey(job.FaixaEtaria))
            {
                payload = payload.Replace("&XFaixa+et%E1ria=TODAS_AS_CATEGORIAS__",
                                        $"&XFaixa+et%E1ria={Const.AGE_GROUPS[job.FaixaEtaria]}");
            }

            if (job.Cid != "TODOS")
            {
                payload = payload.Replace("&XDiagn%F3stico+Detalhado=TODAS_AS_CATEGORIAS__",
                                        $"&XDiagn%F3stico+Detalhado={job.Cid}%7C{job.Cid}%7C3");
            }

            return payload;
        }

        private Dictionary<string, int>? ParseHtml(string html, JobQuery job)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            // Detectar páginas vazias
            if (html.Contains("Nenhum registro selecionado") || html.Contains("Nenhum registro foi selecionado"))
            {
                return Const.REGIONS.Values.ToDictionary(r => r, r => 0);
            }

            var match = Const.RE_ADDROWS.Match(html);
            if (!match.Success)
                return null;

            var matches = Const.RE_LINHA.Matches(match.Groups[1].Value);
            if (matches.Count == 0)
                return null;

            try
            {
                return matches.ToDictionary(
                    m => Const.REGIONS[int.Parse(m.Groups[1].Value)],
                    m => (int)double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                );
            }
            catch (Exception ex)
            {
                LogError($"Erro ao parsear dados para {job}: {ex.Message}");
                return null;
            }
        }

        private void ShowProgress(int completed, int total)
        {
            var now = DateTime.Now;
            var elapsed = now - _startTime;
            var overallRate = completed / Math.Max(elapsed.TotalSeconds, 1);
            var eta = completed < total ? TimeSpan.FromSeconds((total - completed) / Math.Max(overallRate, 1)) : TimeSpan.Zero;

            // Calcular velocidade instantânea (últimos 10 segundos)
            var instantRate = CalculateInstantaneousRate(now, completed);
            
            // Detectar degradação de performance
            var performanceStatus = GetPerformanceStatus(overallRate, instantRate);

            // Atualizar contador de registros para otimizações
            Interlocked.Exchange(ref _recordsInserted, _successfulRequests);

            Console.Write($"\r  {completed:N0}/{total:N0} ({completed * 100.0 / total:F1}%) | " +
                         $"✓{_successfulRequests} ∅{_emptyDataRequests} ✗{_failedRequests} | " +
                         $"{overallRate:F1} req/s ({instantRate:F1} atual) {performanceStatus} | ETA: {eta:hh\\:mm\\:ss}");
        }

        private double CalculateInstantaneousRate(DateTime now, int completed)
        {
            // Adicionar velocidade atual à lista (máximo 10 amostras)
            lock (_statsLock)
            {
                var timeSinceLastCheck = (now - _lastPerformanceCheck).TotalSeconds;
                if (timeSinceLastCheck >= 10.0) // Calcular a cada 10 segundos
                {
                    var recentRate = completed / Math.Max(timeSinceLastCheck, 1);
                    
                    _recentSpeeds.Add(recentRate);
                    if (_recentSpeeds.Count > 6) // Manter apenas últimas 6 amostras (1 minuto)
                        _recentSpeeds.RemoveAt(0);
                    
                    _lastPerformanceCheck = now;
                    return _recentSpeeds.Average();
                }
                
                return _recentSpeeds.Count > 0 ? _recentSpeeds.Average() : 0;
            }
        }

        private string GetPerformanceStatus(double overallRate, double instantRate)
        {
            if (instantRate < overallRate * 0.7) // 30% mais lento que a média
                return "⚠️";  // Performance degradada
            else if (instantRate > overallRate * 1.2) // 20% mais rápido
                return "🚀"; // Performance melhorada
            else
                return "✅"; // Performance normal
        }

        private void LogInfo(string message)
        {
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INFO: {message}";
            Console.WriteLine(msg);
        }

        private void LogError(string message)
        {
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}";
            Console.WriteLine(msg);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _semaphore?.Dispose();
        }
    }

    // ============================================================================
    // INTERFACE DE USUÁRIO E PROGRAMA PRINCIPAL
    // ============================================================================

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("╔════════════════════════════════════════════════════╗");
            Console.WriteLine("║   SCRAPPER ONCOLÓGICO GRANULAR + AGENTE IA         ║");
            Console.WriteLine("╚════════════════════════════════════════════════════╝\n");

            try
            {
                // Menu principal
                var mode = ShowMainMenu();

                if (mode == 1)
                {
                    await RunScrapperMode();
                }
                else if (mode == 2)
                {
                    await RunAgentMode();
                }
                else if (mode == 3)
                {
                    await RunQueryMode();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ ERRO: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"Detalhes: {ex.InnerException.Message}");
            }

            Console.WriteLine("\nPressione qualquer tecla para sair...");
            Console.ReadKey();
        }

        static int ShowMainMenu()
        {
            Console.WriteLine("╔════════════════════════════════════════════════════╗");
            Console.WriteLine("║              SELECIONE O MODO                      ║");
            Console.WriteLine("╠════════════════════════════════════════════════════╣");
            Console.WriteLine("║  1. 📊 Modo Extrator (Coletar dados do DATASUS)    ║");
            Console.WriteLine("║  2. 🤖 Modo Agente IA (Analisar dados existentes)  ║");
            Console.WriteLine("║  3. 🔍 Modo Consulta (Consultas diretas ao banco)  ║");
            Console.WriteLine("╚════════════════════════════════════════════════════╝\n");

            while (true)
            {
                Console.Write("Escolha uma opção (1-3): ");
                if (int.TryParse(Console.ReadLine(), out int choice) && choice >= 1 && choice <= 3)
                {
                    return choice;
                }
                Console.WriteLine("❌ Opção inválida! Digite 1, 2 ou 3.");
            }
        }

        static async Task RunScrapperMode()
        {
            Console.WriteLine("\n=== MODO EXTRATOR ===\n");

            try
            {
                var config = GetConfigurationFromUser();
                var database = new DatabaseManager(config.DatabasePath);
                var scrapper = new ScrapperEngine(config, database);

                // Inicializar sistema
                await scrapper.InitializeAsync();

                // Gerar jobs baseado na estratégia
                var jobs = await scrapper.GenerateJobsAsync();

                if (jobs.Count == 0)
                {
                    Console.WriteLine("Nenhum job para processar!");
                    return;
                }

                // Confirmar execução
                Console.WriteLine($"\nEstrategia: {config.Strategy}");
                Console.WriteLine($"Jobs para processar: {jobs.Count:N0}");
                Console.WriteLine($"Estimativa de tempo: ~{EstimateTime(jobs.Count, config.MaxWorkers):F1} minutos");
                Console.Write("\nDeseja continuar? (s/n): ");

                if (Console.ReadLine()?.ToLower() != "s")
                {
                    Console.WriteLine("Operação cancelada.");
                    return;
                }

                // Executar coleta
                var startTime = DateTime.Now;
                var results = await scrapper.ProcessJobsAsync(jobs);

                // Relatório final
                Console.WriteLine($"\n\n=== COLETA FINALIZADA ===");
                Console.WriteLine($"Tempo total: {(DateTime.Now - startTime).TotalMinutes:F1} minutos");
                Console.WriteLine($"Casos coletados: {results.Count:N0}");
                Console.WriteLine($"Database: {config.DatabasePath}");

                // Estatísticas do banco
                var stats = await database.GetStatisticsAsync();
                Console.WriteLine($"\n=== ESTATÍSTICAS DO BANCO ===");
                Console.WriteLine($"Total de registros: {stats["total_records"]:N0}");

                if (stats["casos_por_ano"] is Dictionary<int, long> anoStats)
                {
                    Console.WriteLine("\nCasos por ano:");
                    foreach (var (ano, casos) in anoStats.OrderBy(x => x.Key))
                    {
                        Console.WriteLine($"  {ano}: {casos:N0} casos");
                    }
                }

                if (stats["top_cids"] is Dictionary<string, long> cidStats)
                {
                    Console.WriteLine("\nTop 10 CIDs:");
                    foreach (var (cid, casos) in cidStats.Take(10))
                    {
                        Console.WriteLine($"  {cid}: {casos:N0} casos");
                    }
                }

                // Exemplos de consulta
                Console.WriteLine("\n=== EXEMPLOS DE CONSULTA ===");
                await ShowQueryExamples(database);

                scrapper.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ ERRO: {ex.Message}");
                throw;
            }
        }

        static async Task RunAgentMode()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════╗");
            Console.WriteLine("║            🤖 MODO AGENTE IA                       ║");
            Console.WriteLine("╚════════════════════════════════════════════════════╝\n");

            // Verificar se o banco de dados existe
            Console.Write("Caminho do banco SQLite [casos_oncologicos.db]: ");
            var dbPath = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(dbPath))
                dbPath = "casos_oncologicos.db";

            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"❌ Banco de dados não encontrado: {dbPath}");
                Console.WriteLine("Execute o Modo Extrator primeiro para coletar dados!");
                return;
            }

            // Configurar API Key
            Console.WriteLine("\n═══════════════════════════════════════════════════");
            Console.WriteLine("Para usar o Agente IA, você precisa de uma API key do Google Gemini.");
            Console.WriteLine("Obtenha gratuitamente em: https://aistudio.google.com/");
            Console.WriteLine("═══════════════════════════════════════════════════\n");

            // Caminho do arquivo de configuração
            var apiKeyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".gemini_apikey");
            string? apiKey = null;

            // Tentar ler API key do arquivo primeiro
            if (File.Exists(apiKeyFile))
            {
                try
                {
                    apiKey = File.ReadAllText(apiKeyFile).Trim();
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        Console.WriteLine("✅ API key carregada do arquivo de configuração");
                        Console.WriteLine($"   Arquivo: {Path.GetFileName(apiKeyFile)}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Erro ao ler arquivo de configuração: {ex.Message}");
                }
            }

            // Se não encontrou, pedir ao usuário
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.Write("Digite sua API key do Gemini: ");
                apiKey = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    Console.WriteLine("❌ API key é obrigatória!");
                    return;
                }

                // Perguntar se quer salvar
                Console.Write("\nDeseja salvar a API key localmente para próximas execuções? (s/n): ");
                if (Console.ReadLine()?.ToLower() == "s")
                {
                    try
                    {
                        File.WriteAllText(apiKeyFile, apiKey);
                        Console.WriteLine($"✅ API key salva em: {apiKeyFile}");
                        Console.WriteLine("   Na próxima vez será carregada automaticamente!");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Não foi possível salvar: {ex.Message}");
                    }
                }
            }

            try
            {
                // Inicializar componentes
                var database = new DatabaseManager(dbPath);
                var aiProvider = new GeminiProvider(apiKey);
                var agent = new AgentAssistant(database, aiProvider, dbPath);

                // Testar conexão com a API
                Console.WriteLine("\n🔄 Testando conexão com a API Gemini...");
                var connectionOk = await aiProvider.TestConnectionAsync();

                if (!connectionOk)
                {
                    Console.WriteLine("❌ Falha ao conectar com a API Gemini. Verifique sua API key.");
                    return;
                }

                Console.WriteLine("✅ Conexão estabelecida com sucesso!");

                // Obter estatísticas do banco
                var stats = await database.GetStatisticsAsync();
                Console.WriteLine($"\n📊 Banco de dados carregado: {stats["total_records"]:N0} registros");

                // Mostrar sugestões
                Console.WriteLine("\n╔════════════════════════════════════════════════════╗");
                Console.WriteLine("║         EXEMPLOS DE PERGUNTAS                      ║");
                Console.WriteLine("╠════════════════════════════════════════════════════╣");
                var suggestions = agent.GetSuggestedQuestions();
                for (int i = 0; i < Math.Min(5, suggestions.Count); i++)
                {
                    Console.WriteLine($"║  • {suggestions[i].PadRight(48)}║");
                }
                Console.WriteLine("╚════════════════════════════════════════════════════╝\n");

                Console.WriteLine("Digite 'sair' para voltar ao menu principal");
                Console.WriteLine("Digite 'limpar' para limpar o histórico");
                Console.WriteLine("Digite 'exportar' para salvar a conversação");
                Console.WriteLine("────────────────────────────────────────────────────\n");

                // Loop de conversação
                while (true)
                {
                    Console.Write("\n💬 Você: ");
                    var userInput = Console.ReadLine();

                    if (string.IsNullOrWhiteSpace(userInput))
                        continue;

                    var input = userInput.Trim().ToLower();

                    if (input == "sair" || input == "exit" || input == "quit")
                    {
                        var convStats = agent.GetConversationStats();
                        Console.WriteLine($"\n📊 Estatísticas da sessão:");
                        Console.WriteLine($"   • Perguntas feitas: {convStats.UserMessages}");
                        Console.WriteLine($"   • Respostas: {convStats.ModelMessages}");
                        Console.WriteLine($"   • Duração: {convStats.Duration:mm\\:ss}");
                        break;
                    }

                    if (input == "limpar" || input == "clear")
                    {
                        agent.ClearConversation();
                        Console.WriteLine("✅ Histórico limpo!");
                        continue;
                    }

                    if (input == "exportar" || input == "export")
                    {
                        var exportPath = $"conversacao_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                        File.WriteAllText(exportPath, agent.ExportConversation());
                        Console.WriteLine($"✅ Conversação exportada para: {exportPath}");
                        continue;
                    }

                    // Processar pergunta
                    var response = await agent.ProcessQuestionAsync(userInput);
                    Console.WriteLine($"\n🤖 Assistente: {response}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ ERRO: {ex.Message}");
                throw;
            }
        }

        static async Task RunQueryMode()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════╗");
            Console.WriteLine("║         🔍 MODO CONSULTA DIRETA                    ║");
            Console.WriteLine("╚════════════════════════════════════════════════════╝\n");

            Console.Write("Caminho do banco SQLite [casos_oncologicos.db]: ");
            var dbPath = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(dbPath))
                dbPath = "casos_oncologicos.db";

            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"❌ Banco de dados não encontrado: {dbPath}");
                return;
            }

            var database = new DatabaseManager(dbPath);
            await ShowQueryExamples(database);
        }

        static ConfigGranular GetConfigurationFromUser()
        {
            Console.WriteLine("=== CONFIGURAÇÃO ===\n");

            // Anos
            var anos = GetYearRange();

            // Estratégia
            Console.WriteLine("\nEstrategias disponíveis:");
            Console.WriteLine("1. Completa - Todas as combinações (muito lento)");
            Console.WriteLine("2. Hierárquica - Otimizada em níveis (recomendado)");
            Console.WriteLine("3. Seletiva - Foco em dados relevantes (rápido)");
            Console.WriteLine("4. Incremental - Só dados que não existem");

            ColetaStrategy strategy;
            while (true)
            {
                Console.Write("Escolha a estratégia (1-4): ");
                if (int.TryParse(Console.ReadLine(), out int choice) && choice >= 1 && choice <= 4)
                {
                    strategy = (ColetaStrategy)(choice - 1);
                    break;
                }
                Console.WriteLine("Opção inválida!");
            }

            // Configurações avançadas
            Console.Write("\nWorkers (threads) [16]: ");
            var workersInput = Console.ReadLine();
            var workers = string.IsNullOrWhiteSpace(workersInput) ? 16 : int.Parse(workersInput);

            Console.Write("Timeout por request (segundos) [45]: ");
            var timeoutInput = Console.ReadLine();
            var timeout = string.IsNullOrWhiteSpace(timeoutInput) ? 45 : int.Parse(timeoutInput);

            Console.Write("Nome do banco SQLite [casos_oncologicos.db]: ");
            var dbPath = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(dbPath))
                dbPath = "casos_oncologicos.db";

            return new ConfigGranular(
                anos,
                Const.REGIONS.Values.ToList(),
                new[] { "ALL", "M", "F" }.ToList(),
                Const.AGE_GROUPS.Keys.ToList(),
                Const.CODES_DETALHADOS,
                strategy,
                dbPath,
                workers,
                timeout
            );
        }

        static List<int> GetYearRange()
        {
            while (true)
            {
                try
                {
                    Console.Write("Ano inicial (2013-2025): ");
                    var inicialStr = Console.ReadLine();
                    if (!int.TryParse(inicialStr, out int inicial) || inicial < 2013 || inicial > 2025)
                        throw new ArgumentException("Ano inicial inválido");

                    Console.Write("Ano final (2013-2025): ");
                    var finalStr = Console.ReadLine();
                    if (!int.TryParse(finalStr, out int final) || final < 2013 || final > 2025)
                        throw new ArgumentException("Ano final inválido");

                    if (final < inicial)
                        (inicial, final) = (final, inicial);

                    return Enumerable.Range(inicial, final - inicial + 1).ToList();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro: {ex.Message}. Tente novamente.");
                }
            }
        }

        static double EstimateTime(int jobCount, int workers)
        {
            // Estimativa baseada em ~2 requisições por segundo por worker
            var requestsPerSecond = workers * 2.0;
            var estimatedSeconds = jobCount / requestsPerSecond;
            return estimatedSeconds / 60.0; // converter para minutos
        }

        static async Task ShowQueryExamples(DatabaseManager database)
        {
            try
            {
                // Exemplo 1: Câncer de mama em mulheres
                var mamaFeminino = await database.QueryAsync(
                    cid: "C50", 
                    sexo: "F", 
                    limit: 5
                );

                if (mamaFeminino.Any())
                {
                    Console.WriteLine("\n1. Câncer de mama em mulheres (top 5):");
                    foreach (var caso in mamaFeminino)
                    {
                        Console.WriteLine($"   {caso.Ano} | {caso.Regiao} | {caso.FaixaEtaria} | {caso.Casos:N0} casos");
                    }
                }

                // Exemplo 2: Casos por região em 2021
                var casos2021 = await database.QueryAsync(ano: 2021, limit: 10);
                if (casos2021.Any())
                {
                    Console.WriteLine("\n2. Maiores incidências em 2021 (top 10):");
                    foreach (var caso in casos2021)
                    {
                        Console.WriteLine($"   {caso.Regiao} | {caso.Cid} | {caso.Sexo} | {caso.FaixaEtaria} | {caso.Casos:N0} casos");
                    }
                }

                // Exemplo 3: Específico - Sudeste, mulheres, 40-44 anos
                var especifico = await database.QueryAsync(
                    regiao: "Sudeste",
                    sexo: "F",
                    faixaEtaria: "40 a 44 anos",
                    limit: 5
                );

                if (especifico.Any())
                {
                    Console.WriteLine("\n3. Sudeste, mulheres, 40-44 anos (top 5):");
                    foreach (var caso in especifico)
                    {
                        Console.WriteLine($"   {caso.Ano} | {caso.Cid} | {caso.Casos:N0} casos");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao mostrar exemplos: {ex.Message}");
            }
        }
    }
}