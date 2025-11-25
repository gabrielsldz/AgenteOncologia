using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using ScrapperGranular.AI.Interfaces;
using ScrapperGranular.Models;
using ScrapperGranular.Utils;

namespace ScrapperGranular.AI.Providers
{
    /// <summary>
    /// Provedor de IA usando Google Gemini Flash
    /// Tier gratuito: 1.500 requisições/dia, 1M tokens/minuto
    /// </summary>
    public class GeminiProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string API_URL = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

        public string ProviderName => "Google Gemini 2.5 Flash";

        public GeminiProvider(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API Key não pode ser vazia", nameof(apiKey));

            _apiKey = apiKey;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public async Task<string> SendMessageAsync(string userMessage, List<Message> conversationHistory)
        {
            try
            {
                Logger.Debug($"Preparando requisição para Gemini API");
                Logger.Metric("Mensagem", $"{userMessage.Length} caracteres");
                Logger.Metric("Histórico", $"{conversationHistory.Count} mensagens");

                // Construir histórico de conversação no formato do Gemini
                var contents = conversationHistory
                    .Select(m => new
                    {
                        role = m.Role,
                        parts = new[] { new { text = m.Content } }
                    })
                    .ToList();

                // Adicionar mensagem atual do usuário
                contents.Add(new
                {
                    role = "user",
                    parts = new[] { new { text = userMessage } }
                });

                var requestBody = new
                {
                    contents = contents,
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 65536,
                        topP = 0.95,
                        topK = 40
                    },
                    safetySettings = new[]
                    {
                        new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                        new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                        new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                        new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
                    }
                };

                var url = $"{API_URL}?key={_apiKey}";
                Logger.Debug($"Endpoint: {API_URL}");
                Logger.Debug("Enviando requisição POST para Gemini...");

                var response = await _httpClient.PostAsJsonAsync(url, requestBody);

                Logger.Debug($"Status HTTP: {(int)response.StatusCode} ({response.StatusCode})");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.Error($"Erro na API Gemini: {response.StatusCode}");
                    Logger.Debug($"Resposta de erro: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                    throw new Exception($"Erro na API Gemini: {response.StatusCode} - {errorContent}");
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                var preview = responseBody.Length > 150 ? responseBody.Substring(0, 150) + "..." : responseBody;
                Logger.Debug($"Resposta recebida: {preview}");

                // Configurar deserialização case-insensitive
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var result = JsonSerializer.Deserialize<GeminiApiResponse>(responseBody, jsonOptions);

                if (result?.Candidates == null || result.Candidates.Count == 0)
                {
                    Logger.Error("Resposta vazia da API Gemini");
                    throw new Exception("Resposta vazia da API Gemini");
                }

                var textResponse = result.Candidates[0]?.Content?.Parts?[0]?.Text;

                if (string.IsNullOrWhiteSpace(textResponse))
                {
                    Logger.Error("Texto da resposta está vazio");
                    throw new Exception("Texto da resposta está vazio");
                }

                Logger.Success($"Resposta da IA recebida ({textResponse.Length} caracteres)");
                return textResponse;
            }
            catch (HttpRequestException ex)
            {
                Logger.Error("Erro de conexão com API Gemini", ex);
                throw new Exception($"Erro de conexão com a API Gemini: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                Logger.Error("Erro ao processar JSON da resposta", ex);
                throw new Exception($"Erro ao processar resposta JSON: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao comunicar com Gemini", ex);
                throw new Exception($"Erro ao comunicar com Gemini: {ex.Message}", ex);
            }
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                Console.WriteLine($"[DEBUG] ========================================");
                Console.WriteLine($"[DEBUG] TESTE DE CONEXÃO COM GEMINI API");
                Console.WriteLine($"[DEBUG] ========================================");
                Console.WriteLine($"[DEBUG] API Key fornecida: {_apiKey.Substring(0, Math.Min(10, _apiKey.Length))}...{_apiKey.Substring(Math.Max(0, _apiKey.Length - 4))}");
                Console.WriteLine($"[DEBUG] Comprimento da API Key: {_apiKey.Length} caracteres");
                Console.WriteLine($"[DEBUG] API Key começa com 'AIza': {_apiKey.StartsWith("AIza")}");
                Console.WriteLine($"[DEBUG] ========================================\n");

                // Validar formato básico da API key
                if (!_apiKey.StartsWith("AIza"))
                {
                    Console.WriteLine($"[WARNING] API Key não começa com 'AIza'. Formato pode estar incorreto.");
                }

                var testMessage = "Responda apenas 'OK' se você está funcionando.";
                Console.WriteLine($"[DEBUG] Enviando mensagem de teste: '{testMessage}'\n");

                var response = await SendMessageAsync(testMessage, new List<Message>());

                Console.WriteLine($"\n[DEBUG] ========================================");
                Console.WriteLine($"[DEBUG] TESTE CONCLUÍDO COM SUCESSO!");
                Console.WriteLine($"[DEBUG] Resposta recebida: {response}");
                Console.WriteLine($"[DEBUG] ========================================\n");

                return !string.IsNullOrWhiteSpace(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[DEBUG] ========================================");
                Console.WriteLine($"[DEBUG] TESTE FALHOU!");
                Console.WriteLine($"[ERROR] Tipo de erro: {ex.GetType().Name}");
                Console.WriteLine($"[ERROR] Mensagem: {ex.Message}");

                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[ERROR] Erro interno: {ex.InnerException.Message}");
                }

                Console.WriteLine($"[DEBUG] ========================================\n");
                return false;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
