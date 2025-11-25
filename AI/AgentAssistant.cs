using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScrapperGranular.AI.Cache;
using ScrapperGranular.AI.Interfaces;
using ScrapperGranular.Database;
using ScrapperGranular.Models;
using ScrapperGranular.Utils;

namespace ScrapperGranular.AI
{
    /// <summary>
    /// Assistente de IA para análise de dados oncológicos com sistema de cache multi-camadas
    /// </summary>
    public class AgentAssistant
    {
        private readonly DatabaseManager _database;
        private readonly IAIProvider _aiProvider;
        private readonly ConversationManager _conversation;
        private readonly string _connectionString;

        // Sistema de cache simplificado (2 níveis apenas)
        private readonly QueryCache? _responseCache;              // Nível 1: Resposta (exact + semantic+LLM)
        private readonly SqlResultsCache? _sqlResultsCache;       // Nível 2: Resultados SQL (exact hash)

        // Regex para extrair queries SQL da resposta da IA
        private static readonly Regex SqlQueryRegex = new(@"\[SQL\](.*?)\[/SQL\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        public AgentAssistant(
            DatabaseManager database,
            IAIProvider aiProvider,
            string dbPath,
            QueryCache? responseCache = null,
            SqlResultsCache? sqlResultsCache = null)
        {
            _database = database;
            _aiProvider = aiProvider;
            _conversation = new ConversationManager(maxMessages: 20);
            _connectionString = $"Data Source={dbPath}";

            // Caches simplificados (2 níveis)
            _responseCache = responseCache;
            _sqlResultsCache = sqlResultsCache;

            InitializeSystemPrompt();
        }

        /// <summary>
        /// Inicializa o prompt de sistema que instrui a IA sobre o banco de dados
        /// </summary>
        private void InitializeSystemPrompt()
        {
            var systemPrompt = @"Você é um assistente especializado em análise de dados oncológicos brasileiros do DATASUS.

BANCO DE DADOS:
Tabela: casos_oncologicos
Campos:
  - ano (INTEGER): Ano do diagnóstico (2013-2025)
  - regiao (TEXT): Região do Brasil (Norte, Nordeste, Sudeste, Sul, Centro-Oeste)
  - sexo (TEXT): ALL (todos), M (masculino), F (feminino)
  - faixa_etaria (TEXT): 14 faixas de '0 a 19 anos' até '80 anos e mais'
  - cid (TEXT): Código CID do câncer (C00-C97, D00-D48)
  - casos (INTEGER): Número de casos registrados

CÓDIGOS CID MAIS COMUNS:
- C50: Câncer de mama
- C53: Câncer do colo do útero
- C61: Câncer de próstata
- C34: Câncer de traqueia, brônquios e pulmões
- C18-C21: Câncer colorretal
- C16: Câncer de estômago
- C73: Câncer de tireoide
- C67: Câncer de bexiga
- C64: Câncer de rim
- C25: Câncer de pâncreas

INSTRUÇÕES IMPORTANTES:

1. PRIMEIRA RESPOSTA (Gerar Query):
   - Quando o usuário fizer uma pergunta, gere APENAS a query SQL no formato [SQL]...[/SQL]
   - NÃO adicione texto antes ou depois da query
   - NÃO explique o que você vai fazer
   - Apenas: [SQL]SELECT...[/SQL]

2. SEGUNDA RESPOSTA (Após receber resultados):
   - Responda de forma CONVERSACIONAL e DIRETA
   - Use tom natural, amigável e profissional (como ChatGPT)
   - Vá direto aos números e insights
   - NÃO use estruturas formais como:
     ❌ 'Para comparar...'
     ❌ 'Análise dos Resultados:'
     ❌ 'Conclusão:'
     ❌ 'Com base nos dados...'
   - NÃO mencione 'query SQL', 'consulta', 'banco de dados' ou termos técnicos
   - Simplesmente responda à pergunta diretamente

IMPORTANTE - COMO DIFERENCIAR AS FASES:
   - Se voce receber uma PERGUNTA NOVA do usuario: Gere SQL com [SQL]...[/SQL]
   - Se voce receber 'Dados obtidos da consulta SQL': ANALISE os dados (NUNCA gere SQL novamente!)
   - NUNCA gere SQL quando estiver analisando resultados ja obtidos!

3. Voce pode gerar multiplas queries se necessario.

4. Use estatisticas descritivas (numeros, porcentagens, comparacoes).

5. Termine com 2-3 sugestoes de perguntas relacionadas (use bullet points simples).

EXEMPLOS DE RESPOSTAS CORRETAS:

Pergunta: Quantos casos de cancer de mama em 2021?
1a Resposta: [SQL]SELECT SUM(casos) as total FROM casos_oncologicos WHERE cid='C50' AND ano=2021[/SQL]
2a Resposta: Em 2021, foram registrados 112.700 casos de cancer de mama em mulheres no Brasil.

Outras perguntas que posso responder:
* Qual foi a evolucao dos casos nos ultimos 5 anos?
* Como se distribuem por regiao do pais?
* Qual a faixa etaria mais afetada?

Pergunta: Compare cancer de pulmao entre homens e mulheres
1a Resposta: [SQL]SELECT sexo, SUM(casos) as total FROM casos_oncologicos WHERE cid='C34' GROUP BY sexo[/SQL]
2a Resposta: Os homens apresentam 75.175 casos de cancer de pulmao, enquanto as mulheres tem 63.221 casos. Isso representa uma diferenca de aproximadamente 12 mil casos.

Outras analises interessantes:
* Existe diferenca na distribuicao etaria entre os sexos?
* Como essa proporcao varia entre as regioes do Brasil?
* Qual a tendencia temporal para cada sexo?";

            _conversation.AddMessage("user", systemPrompt);
            _conversation.AddMessage("model", "Entendido! Estou pronto para ajudar com análises de dados oncológicos. Posso responder perguntas sobre incidência de câncer no Brasil, fazer comparações entre regiões, analisar tendências ao longo dos anos e muito mais. Como posso ajudar?");
        }

        /// <summary>
        /// Processa uma pergunta do usuário com sistema de cache multi-camadas (5 níveis)
        /// </summary>
        public async Task<string> ProcessQuestionAsync(string userQuestion)
        {
            var totalSw = Stopwatch.StartNew();

            try
            {
                Logger.BigSeparator("NOVA PERGUNTA");
                Logger.Box("Pergunta do Usuário", userQuestion);

                // ============================================
                // NÍVEL 1: CACHE DE RESPOSTA COMPLETA (exact + semantic+LLM)
                // ============================================
                if (_responseCache != null)
                {
                    Logger.Info("Verificando cache de resposta completa...");
                    var cachedResponse = await _responseCache.GetCachedResponseAsync(userQuestion);

                    if (cachedResponse != null)
                    {
                        totalSw.Stop();
                        _conversation.AddMessage("user", userQuestion);
                        _conversation.AddMessage("model", cachedResponse.Response);

                        var timeSavedMs = 2000;
                        Logger.Success($"Resposta retornada do cache!");
                        Logger.Metric("Tempo economizado (estimado)", $"~{timeSavedMs}ms");
                        Logger.Metric("Tempo total", $"{totalSw.ElapsedMilliseconds}ms");
                        Logger.BigSeparator();

                        return cachedResponse.Response;
                    }
                }

                // Adicionar pergunta ao histórico
                _conversation.AddMessage("user", userQuestion);

                // ============================================
                // GERAR SQL COM IA (SEM cache de geração)
                // ============================================
                Logger.Info("Gerando SQL com IA...");
                var querySw = Stopwatch.StartNew();

                var aiResponse = await _aiProvider.SendMessageAsync(
                    userQuestion,
                    _conversation.GetHistory()
                );

                querySw.Stop();
                Logger.Metric("Tempo IA (geração query)", $"{querySw.ElapsedMilliseconds}ms");

                // Extrair SQL
                var sqlQueries = ExtractSqlQueries(aiResponse);
                if (!sqlQueries.Any())
                {
                    // Sem SQL, resposta direta
                    var cleanResponse = RemoveSqlQueries(aiResponse);
                    _conversation.AddMessage("model", cleanResponse);

                    // Salvar no cache de resposta
                    await _responseCache?.SaveAsync(userQuestion, cleanResponse);

                    totalSw.Stop();
                    Logger.Success("Processamento concluído!");
                    Logger.Metric("Tempo total", $"{totalSw.ElapsedMilliseconds}ms");
                    Logger.BigSeparator();

                    return cleanResponse;
                }

                var generatedSql = sqlQueries[0];
                Logger.Success("SQL gerada!");
                Logger.Box("SQL Query", generatedSql);

                // ============================================
                // NÍVEL 2: CACHE DE RESULTADOS SQL (hash exato apenas)
                // ============================================
                string queryResultJson;
                int rowCount;

                if (_sqlResultsCache != null)
                {
                    Logger.Info("Verificando cache de resultados SQL...");
                    var cachedResult = await _sqlResultsCache.GetCachedResultAsync(generatedSql!);

                    if (cachedResult != null)
                    {
                        queryResultJson = cachedResult.ResultJson;
                        rowCount = cachedResult.RowCount;
                        Logger.CacheHit("SQL_RESULTS");
                        Logger.Metric("Tempo economizado (exec SQL)", $"~{15}ms");
                    }
                    else
                    {
                        // Executar SQL
                        var execResult = await ExecuteQueryAsync(generatedSql!);
                        queryResultJson = execResult;
                        rowCount = CountRows(execResult);

                        // Salvar no cache
                        await _sqlResultsCache.SaveResultAsync(generatedSql!, queryResultJson, rowCount);
                    }
                }
                else
                {
                    // Sem cache, executar normalmente
                    queryResultJson = await ExecuteQueryAsync(generatedSql!);
                    rowCount = CountRows(queryResultJson);
                }

                // ============================================
                // ANÁLISE COM IA (SEM pattern cache)
                // ============================================
                Logger.Info("Enviando resultados para IA analisar...");
                var analysisSw = Stopwatch.StartNew();

                var resultsMessage = $@"IMPORTANTE: A query SQL já foi executada com sucesso! Agora você deve ANALISAR os resultados abaixo.

⚠️ NÃO GERE SQL NOVAMENTE! Apenas interprete os dados e responda de forma conversacional.

Pergunta original do usuário:
{userQuestion}

Dados obtidos da consulta SQL:
{queryResultJson}

Agora responda à pergunta de forma natural, direta e amigável, SEM gerar SQL.";

                _conversation.AddMessage("user", resultsMessage);

                var analysisResponse = await _aiProvider.SendMessageAsync(
                    resultsMessage,
                    _conversation.GetHistory()
                );

                analysisSw.Stop();
                Logger.Metric("Tempo IA (análise)", $"{analysisSw.ElapsedMilliseconds}ms");

                // CAMADA 3: Detectar se IA gerou SQL por engano e re-tentar
                if (analysisResponse.Contains("[SQL]") && analysisResponse.Contains("[/SQL]"))
                {
                    Logger.Warning("IA gerou SQL na fase de análise - re-tentando com prompt mais direto...");

                    // Re-tentar com prompt ainda mais explícito
                    var retryMessage = $@"❌ ERRO: Você acabou de gerar SQL, mas isso está INCORRETO neste momento!

Os dados JÁ FORAM OBTIDOS do banco de dados. Veja os resultados abaixo:

{queryResultJson}

Sua tarefa agora é APENAS ANALISAR estes dados e responder de forma conversacional.
NÃO gere SQL. NÃO use tags [SQL]. Apenas analise os números e responda.

Pergunta original: {userQuestion}

Responda agora de forma natural:";

                    analysisResponse = await _aiProvider.SendMessageAsync(retryMessage, _conversation.GetHistory());
                    Logger.Metric("Tempo IA (retry análise)", $"{analysisSw.ElapsedMilliseconds}ms");

                    // Se ainda gerar SQL, usar fallback
                    if (analysisResponse.Contains("[SQL]"))
                    {
                        Logger.Error("IA continua gerando SQL após retry - usando análise de fallback");
                        analysisResponse = GenerateFallbackAnalysis(userQuestion, queryResultJson);
                    }
                }

                var finalResponse = RemoveSqlQueries(analysisResponse);

                // Adicionar resposta ao histórico
                _conversation.AddMessage("model", finalResponse);

                // Salvar no cache de resposta completa
                await _responseCache?.SaveAsync(userQuestion, finalResponse);

                totalSw.Stop();
                Logger.Success("Processamento concluído!");
                Logger.Metric("Tempo total", $"{totalSw.ElapsedMilliseconds}ms");
                Logger.BigSeparator();

                return finalResponse;
            }
            catch (Exception ex)
            {
                totalSw.Stop();
                Logger.Error("Erro ao processar pergunta", ex);
                Logger.Metric("Tempo até erro", $"{totalSw.ElapsedMilliseconds}ms");
                Logger.BigSeparator();

                return $"❌ Erro ao processar sua pergunta: {ex.Message}";
            }
        }

        /// <summary>
        /// Extrai queries SQL da resposta da IA
        /// </summary>
        private List<string> ExtractSqlQueries(string response)
        {
            var queries = new List<string>();
            var matches = SqlQueryRegex.Matches(response);

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var query = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(query))
                    {
                        queries.Add(query);
                    }
                }
            }

            return queries;
        }

        /// <summary>
        /// Remove queries SQL da resposta final para o usuário
        /// </summary>
        private string RemoveSqlQueries(string response)
        {
            // Remove todas as tags [SQL]...[/SQL] da resposta
            var cleaned = SqlQueryRegex.Replace(response, "");

            // Remove linhas vazias extras que podem ter ficado
            cleaned = Regex.Replace(cleaned, @"^\s*[\r\n]+", "", RegexOptions.Multiline);

            return cleaned.Trim();
        }

        /// <summary>
        /// Executa uma query SQL e retorna resultados formatados
        /// </summary>
        private async Task<string> ExecuteQueryAsync(string sqlQuery)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                // Validação básica de segurança
                if (!IsQuerySafe(sqlQuery))
                {
                    Logger.Error("Query rejeitada por motivos de segurança");
                    return "❌ ERRO: Query rejeitada por motivos de segurança. Use apenas SELECT na tabela casos_oncologicos.";
                }

                Logger.Debug("Conectando ao banco de dados SQLite...");

                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqliteCommand(sqlQuery, connection);
                using var reader = await command.ExecuteReaderAsync();

                var result = new StringBuilder();
                var columnCount = reader.FieldCount;
                var rowCount = 0;

                // Cabeçalhos
                var headers = new List<string>();
                for (int i = 0; i < columnCount; i++)
                {
                    headers.Add(reader.GetName(i));
                }
                result.AppendLine(string.Join(" | ", headers));
                result.AppendLine(new string('-', headers.Sum(h => h.Length) + (columnCount - 1) * 3));

                // Lista para mostrar no console
                var consoleResults = new List<Dictionary<string, object>>();

                // Linhas (limitar a 100 resultados)
                while (await reader.ReadAsync() && rowCount < 100)
                {
                    var values = new List<string>();
                    var rowDict = new Dictionary<string, object>();

                    for (int i = 0; i < columnCount; i++)
                    {
                        var value = reader.GetValue(i);
                        var valueStr = value?.ToString() ?? "NULL";
                        values.Add(valueStr);
                        rowDict[headers[i]] = value ?? "NULL";
                    }

                    result.AppendLine(string.Join(" | ", values));
                    consoleResults.Add(rowDict);
                    rowCount++;
                }

                sw.Stop();

                if (rowCount == 0)
                {
                    Logger.Warning("Nenhum resultado encontrado no banco");
                    return "⚠️ Nenhum resultado encontrado.";
                }

                Logger.Success($"Query executada com sucesso!");
                Logger.Metric("Linhas retornadas", rowCount);
                Logger.Metric("Tempo de execução", $"{sw.ElapsedMilliseconds}ms");

                // Mostrar resultados no console (limitar a 10 linhas para não poluir)
                var displayLimit = Math.Min(consoleResults.Count, 10);
                Logger.Info($"Resultados (mostrando {displayLimit} de {rowCount}):");

                for (int i = 0; i < displayLimit; i++)
                {
                    var row = consoleResults[i];
                    var rowStr = string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}"));
                    Console.WriteLine($"         │ Linha {i + 1}: {rowStr}");
                }

                if (rowCount > displayLimit)
                {
                    Logger.Info($"... e mais {rowCount - displayLimit} linha(s)");
                }

                result.AppendLine($"\n✓ {rowCount} linha(s) retornada(s) em {sw.ElapsedMilliseconds}ms");
                return result.ToString();
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.Error($"Erro ao executar query no banco", ex);
                return $"❌ Erro ao executar query: {ex.Message}";
            }
        }

        /// <summary>
        /// Conta número de linhas em um resultado de query
        /// </summary>
        private int CountRows(string queryResult)
        {
            if (string.IsNullOrWhiteSpace(queryResult))
                return 0;

            // Contar linhas que não são headers ou separadores
            var lines = queryResult.Split('\n');
            return lines.Count(l => !string.IsNullOrWhiteSpace(l) &&
                                   !l.Contains("---") &&
                                   !l.Contains(" | ") &&
                                   !l.StartsWith("✓"));
        }

        /// <summary>
        /// Valida se a query é segura (apenas SELECT)
        /// </summary>
        private bool IsQuerySafe(string query)
        {
            var upperQuery = query.ToUpperInvariant().Trim();

            // Deve começar com SELECT
            if (!upperQuery.StartsWith("SELECT"))
                return false;

            // Não pode conter comandos perigosos
            var dangerousKeywords = new[]
            {
                "DROP", "DELETE", "INSERT", "UPDATE", "ALTER",
                "CREATE", "TRUNCATE", "EXEC", "EXECUTE"
            };

            foreach (var keyword in dangerousKeywords)
            {
                if (upperQuery.Contains(keyword))
                    return false;
            }

            // Deve referenciar apenas a tabela casos_oncologicos
            if (!upperQuery.Contains("CASOS_ONCOLOGICOS"))
                return false;

            return true;
        }

        /// <summary>
        /// Obtém sugestões de perguntas para o usuário
        /// </summary>
        public List<string> GetSuggestedQuestions()
        {
            return new List<string>
            {
                "Quantos casos de câncer de mama foram registrados em 2021?",
                "Qual região teve mais casos de câncer de próstata?",
                "Compare os casos de câncer de pulmão entre homens e mulheres",
                "Mostre os 5 tipos de câncer mais comuns no Sudeste",
                "Qual a tendência de câncer de colo do útero de 2015 a 2023?",
                "Quantos casos de câncer em jovens (0-19 anos) no Norte?",
                "Compare câncer colorretal entre todas as regiões",
                "Qual faixa etária tem mais casos de câncer de tireoide?"
            };
        }

        /// <summary>
        /// Obtém estatísticas da conversação atual
        /// </summary>
        public (int UserMessages, int ModelMessages, TimeSpan Duration) GetConversationStats()
        {
            return _conversation.GetStats();
        }

        /// <summary>
        /// Limpa o histórico de conversação
        /// </summary>
        public void ClearConversation()
        {
            _conversation.Clear();
            InitializeSystemPrompt();
        }

        /// <summary>
        /// Exporta a conversação para arquivo
        /// </summary>
        public string ExportConversation()
        {
            return _conversation.ExportToText();
        }

        /// <summary>
        /// Gera análise básica de fallback quando IA falha repetidamente
        /// </summary>
        private string GenerateFallbackAnalysis(string question, string queryResults)
        {
            try
            {
                Logger.Warning("Gerando análise de fallback (IA falhou em interpretar corretamente)");

                var rows = ParseQueryResultToRows(queryResults);
                var rowCount = rows.Count;

                if (rowCount == 0)
                    return "Não foram encontrados dados para esta consulta no banco de dados.";

                var summary = new StringBuilder();
                summary.AppendLine($"📊 Encontrei {rowCount} resultado(s) para sua pergunta:");
                summary.AppendLine();

                // Mostrar primeiras 10 linhas de forma formatada
                var limit = Math.Min(10, rowCount);
                for (int i = 0; i < limit; i++)
                {
                    var row = rows[i];
                    var formattedRow = string.Join(", ", row.Select(kv =>
                    {
                        var key = kv.Key.Replace("_", " ");
                        var value = kv.Value;

                        // Formatação especial para números grandes
                        if (value is long longVal && longVal > 999)
                            return $"{key}: {longVal:N0}";

                        return $"{key}: {value}";
                    }));

                    summary.AppendLine($"• {formattedRow}");
                }

                if (rowCount > limit)
                    summary.AppendLine($"\n... e mais {rowCount - limit} resultado(s).");

                summary.AppendLine();
                summary.AppendLine("💡 Dica: Você pode fazer perguntas mais específicas para análises mais detalhadas.");

                return summary.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao gerar análise de fallback", ex);
                return $"Os dados foram obtidos com sucesso, mas houve um problema ao formatá-los. Total de linhas: {CountRows(queryResults)}";
            }
        }

        /// <summary>
        /// Converte resultado de query SQL (formato tabela) em lista de dicionários
        /// para uso em pattern cache
        /// </summary>
        private List<Dictionary<string, object>> ParseQueryResultToRows(string queryResult)
        {
            var rows = new List<Dictionary<string, object>>();

            try
            {
                if (string.IsNullOrWhiteSpace(queryResult))
                    return rows;

                var lines = queryResult.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (lines.Count < 2)
                    return rows;

                // Primeira linha: headers
                var headerLine = lines[0];
                var headers = headerLine.Split('|')
                    .Select(h => h.Trim())
                    .Where(h => !string.IsNullOrEmpty(h))
                    .ToList();

                if (headers.Count == 0)
                    return rows;

                // Pular linha separadora (----)
                var dataLines = lines.Skip(2)
                    .Where(l => !l.Contains("---") && !l.StartsWith("✓"))
                    .ToList();

                // Parse cada linha de dados
                foreach (var line in dataLines)
                {
                    var values = line.Split('|')
                        .Select(v => v.Trim())
                        .Where(v => !string.IsNullOrEmpty(v))
                        .ToList();

                    if (values.Count == headers.Count)
                    {
                        var row = new Dictionary<string, object>();
                        for (int i = 0; i < headers.Count; i++)
                        {
                            // Tentar converter para número se possível
                            if (long.TryParse(values[i], out var longVal))
                                row[headers[i]] = longVal;
                            else if (double.TryParse(values[i], out var doubleVal))
                                row[headers[i]] = doubleVal;
                            else
                                row[headers[i]] = values[i];
                        }
                        rows.Add(row);
                    }
                }

                return rows;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Erro ao parsear resultado de query: {ex.Message}");
                return rows;
            }
        }
    }
}
