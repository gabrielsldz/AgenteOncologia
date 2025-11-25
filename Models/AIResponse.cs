using System;
using System.Collections.Generic;

namespace ScrapperGranular.Models
{
    /// <summary>
    /// Representa a resposta completa da IA
    /// </summary>
    public record AIResponse
    {
        public string Text { get; init; }
        public List<string> SqlQueries { get; init; }
        public bool HasError { get; init; }
        public string? ErrorMessage { get; init; }

        public AIResponse(string text, List<string>? sqlQueries = null, bool hasError = false, string? errorMessage = null)
        {
            Text = text;
            SqlQueries = sqlQueries ?? new List<string>();
            HasError = hasError;
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>
    /// Resposta da API do Gemini
    /// </summary>
    public class GeminiApiResponse
    {
        public List<Candidate>? Candidates { get; set; }
    }

    public class Candidate
    {
        public Content? Content { get; set; }
    }

    public class Content
    {
        public List<Part>? Parts { get; set; }
    }

    public class Part
    {
        public string? Text { get; set; }
    }
}
