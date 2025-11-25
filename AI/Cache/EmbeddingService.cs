using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using ScrapperGranular.Utils;

namespace ScrapperGranular.AI.Cache
{
    /// <summary>
    /// Serviço para gerar embeddings usando Gemini Embedding API
    /// </summary>
    public class EmbeddingService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string EMBEDDING_API_URL = "https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent";

        public EmbeddingService(string apiKey)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        /// <summary>
        /// Gera embedding para um texto
        /// </summary>
        public async Task<float[]?> GenerateEmbeddingAsync(string text)
        {
            try
            {
                Logger.Debug($"Gerando embedding para texto ({text.Length} chars)");

                var requestBody = new
                {
                    content = new { parts = new[] { new { text } } }
                };

                var url = $"{EMBEDDING_API_URL}?key={_apiKey}";
                var response = await _httpClient.PostAsJsonAsync(url, requestBody);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Error($"Erro ao gerar embedding: {response.StatusCode}");
                    return null;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var jsonDoc = JsonDocument.Parse(jsonResponse);

                if (jsonDoc.RootElement.TryGetProperty("embedding", out var embeddingElement) &&
                    embeddingElement.TryGetProperty("values", out var valuesElement))
                {
                    var embedding = valuesElement.EnumerateArray()
                        .Select(v => (float)v.GetDouble())
                        .ToArray();

                    Logger.Success($"Embedding gerado: {embedding.Length} dimensões");
                    return embedding;
                }

                Logger.Warning("Formato de resposta de embedding inválido");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("Erro ao gerar embedding", ex);
                return null;
            }
        }

        /// <summary>
        /// Calcula similaridade cosine entre dois vetores
        /// </summary>
        public static float CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return 0f;

            double dotProduct = 0;
            double magnitudeA = 0;
            double magnitudeB = 0;

            for (int i = 0; i < a.Length; i++)
            {
                dotProduct += a[i] * b[i];
                magnitudeA += a[i] * a[i];
                magnitudeB += b[i] * b[i];
            }

            magnitudeA = Math.Sqrt(magnitudeA);
            magnitudeB = Math.Sqrt(magnitudeB);

            if (magnitudeA == 0 || magnitudeB == 0)
                return 0f;

            return (float)(dotProduct / (magnitudeA * magnitudeB));
        }

        /// <summary>
        /// Serializa embedding para BLOB do SQLite
        /// </summary>
        public static byte[] SerializeEmbedding(float[] embedding)
        {
            var bytes = new byte[embedding.Length * sizeof(float)];
            Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        /// <summary>
        /// Deserializa embedding do BLOB do SQLite
        /// </summary>
        public static float[] DeserializeEmbedding(byte[] bytes)
        {
            var embedding = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
            return embedding;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
