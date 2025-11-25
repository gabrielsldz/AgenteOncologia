using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ScrapperGranular.AI.Cache
{
    /// <summary>
    /// Normaliza texto para melhorar cache hits
    /// Remove stopwords, acentos, pontuação e normaliza números
    /// </summary>
    public static class TextNormalizer
    {
        // Stopwords em português
        private static readonly HashSet<string> Stopwords = new()
        {
            "o", "a", "os", "as", "um", "uma", "uns", "umas",
            "de", "da", "do", "das", "dos", "em", "no", "na", "nos", "nas",
            "por", "para", "com", "sem", "sob", "sobre",
            "e", "ou", "mas", "pois", "porque", "se", "que",
            "me", "te", "se", "lhe", "nos", "vos", "lhes",
            "meu", "minha", "meus", "minhas", "teu", "tua", "teus", "tuas",
            "seu", "sua", "seus", "suas", "nosso", "nossa", "nossos", "nossas",
            "este", "esta", "estes", "estas", "esse", "essa", "esses", "essas",
            "aquele", "aquela", "aqueles", "aquelas", "isto", "isso", "aquilo",
            "qual", "quais", "quanto", "quanta", "quantos", "quantas",
            "onde", "quando", "como", "quem",
            "foi", "fez", "tem", "tinha", "ha", "havia", "houve",
            "ser", "estar", "ter", "haver", "fazer",
            "mais", "menos", "muito", "pouco", "todo", "toda", "todos", "todas",
            "outro", "outra", "outros", "outras", "mesmo", "mesma", "mesmos", "mesmas",
            "ja", "ainda", "tambem", "so", "apenas", "somente"
        };

        // Mapeamento de números por extenso
        private static readonly Dictionary<string, string> NumberWords = new()
        {
            { "dois mil e vinte e um", "2021" },
            { "dois mil e vinte e dois", "2022" },
            { "dois mil e vinte e tres", "2023" },
            { "dois mil e vinte e quatro", "2024" },
            { "dois mil e vinte e cinco", "2025" },
            { "dois mil e vinte", "2020" },
            { "dois mil e dezenove", "2019" },
            { "dois mil e dezoito", "2018" },
            { "dois mil e dezessete", "2017" },
            { "dois mil e dezesseis", "2016" },
            { "dois mil e quinze", "2015" },
            { "dois mil e quatorze", "2014" },
            { "dois mil e treze", "2013" }
        };

        /// <summary>
        /// Normaliza texto completo para comparação
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // 1. Lowercase
            text = text.ToLowerInvariant();

            // 2. Remover acentos
            text = RemoveAccents(text);

            // 3. Normalizar números por extenso
            text = NormalizeNumberWords(text);

            // 4. Remover pontuação (manter números)
            text = Regex.Replace(text, @"[^\w\s]", " ");

            // 5. Normalizar espaços
            text = Regex.Replace(text, @"\s+", " ");

            // 6. Remover stopwords
            text = RemoveStopwords(text);

            // 7. Ordenar palavras (para melhorar matches)
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Array.Sort(words);
            text = string.Join(" ", words);

            return text.Trim();
        }

        /// <summary>
        /// Normaliza para busca de cache (menos agressivo)
        /// </summary>
        public static string NormalizeForCache(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // 1. Lowercase
            text = text.ToLowerInvariant();

            // 2. Remover acentos
            text = RemoveAccents(text);

            // 3. Normalizar números por extenso
            text = NormalizeNumberWords(text);

            // 4. Remover pontuação
            text = Regex.Replace(text, @"[^\w\s]", " ");

            // 5. Normalizar espaços
            text = Regex.Replace(text, @"\s+", " ");

            // 6. Remover stopwords
            text = RemoveStopwords(text);

            return text.Trim();
        }

        /// <summary>
        /// Remove acentos e caracteres especiais
        /// </summary>
        private static string RemoveAccents(string text)
        {
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Remove stopwords do texto
        /// </summary>
        private static string RemoveStopwords(string text)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var filteredWords = words.Where(word => !Stopwords.Contains(word));
            return string.Join(" ", filteredWords);
        }

        /// <summary>
        /// Normaliza números escritos por extenso
        /// </summary>
        private static string NormalizeNumberWords(string text)
        {
            foreach (var (word, number) in NumberWords)
            {
                text = text.Replace(word, number);
            }
            return text;
        }

        /// <summary>
        /// Gera hash SHA256 do texto normalizado
        /// </summary>
        public static string GenerateHash(string text)
        {
            var normalized = Normalize(text);
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(normalized);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLower();
        }

        /// <summary>
        /// Extrai palavras-chave principais do texto
        /// </summary>
        public static List<string> ExtractKeywords(string text, int maxKeywords = 5)
        {
            var normalized = NormalizeForCache(text);
            var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Contagem de frequência
            var frequency = new Dictionary<string, int>();
            foreach (var word in words)
            {
                if (word.Length >= 3) // Palavras com pelo menos 3 caracteres
                {
                    frequency[word] = frequency.GetValueOrDefault(word, 0) + 1;
                }
            }

            // Retornar top N palavras mais frequentes
            return frequency
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Take(maxKeywords)
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary>
        /// Calcula similaridade básica entre dois textos (Jaccard)
        /// </summary>
        public static double JaccardSimilarity(string text1, string text2)
        {
            var words1 = Normalize(text1).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var words2 = Normalize(text2).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

            if (words1.Count == 0 && words2.Count == 0)
                return 1.0;

            if (words1.Count == 0 || words2.Count == 0)
                return 0.0;

            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();

            return (double)intersection / union;
        }
    }
}
