using System.Collections.Generic;
using System.Threading.Tasks;
using ScrapperGranular.Models;

namespace ScrapperGranular.AI.Interfaces
{
    /// <summary>
    /// Interface para provedores de IA (Gemini, Groq, Cohere, etc.)
    /// </summary>
    public interface IAIProvider
    {
        /// <summary>
        /// Envia uma mensagem para a IA e retorna a resposta
        /// </summary>
        /// <param name="userMessage">Mensagem do usuário</param>
        /// <param name="conversationHistory">Histórico da conversação</param>
        /// <returns>Resposta da IA</returns>
        Task<string> SendMessageAsync(string userMessage, List<Message> conversationHistory);

        /// <summary>
        /// Nome do provedor (ex: "Gemini", "Groq")
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Verifica se a API está configurada corretamente
        /// </summary>
        Task<bool> TestConnectionAsync();
    }
}
