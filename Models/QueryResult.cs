using System;
using System.Collections.Generic;

namespace ScrapperGranular.Models
{
    /// <summary>
    /// Resultado de uma consulta ao banco de dados
    /// </summary>
    public record QueryResult
    {
        public List<CasoOncologico> Cases { get; init; }
        public int TotalRecords { get; init; }
        public string Query { get; init; }
        public TimeSpan ExecutionTime { get; init; }
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }

        public QueryResult(List<CasoOncologico> cases, string query, TimeSpan executionTime, bool success = true, string? errorMessage = null)
        {
            Cases = cases;
            TotalRecords = cases.Count;
            Query = query;
            ExecutionTime = executionTime;
            Success = success;
            ErrorMessage = errorMessage;
        }

        public static QueryResult Error(string query, string errorMessage)
        {
            return new QueryResult(
                new List<CasoOncologico>(),
                query,
                TimeSpan.Zero,
                false,
                errorMessage
            );
        }
    }
}
