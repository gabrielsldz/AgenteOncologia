using System;
using System.Collections.Generic;
using System.Linq;
using ScrapperGranular.Models;

namespace ScrapperGranular.AI
{
    /// <summary>
    /// Gerencia o histórico de conversação e contexto
    /// </summary>
    public class ConversationManager
    {
        private readonly List<Message> _messages;
        private readonly int _maxMessages;

        public ConversationManager(int maxMessages = 20)
        {
            _messages = new List<Message>();
            _maxMessages = maxMessages;
        }

        /// <summary>
        /// Adiciona uma mensagem ao histórico
        /// </summary>
        public void AddMessage(string role, string content)
        {
            _messages.Add(new Message(role, content));

            // Manter apenas as últimas N mensagens para não exceder o contexto
            while (_messages.Count > _maxMessages)
            {
                // Remove a mensagem mais antiga (mas mantém a primeira que é o system prompt)
                if (_messages.Count > 1)
                    _messages.RemoveAt(1);
            }
        }

        /// <summary>
        /// Obtém o histórico completo da conversação
        /// </summary>
        public List<Message> GetHistory()
        {
            return _messages.ToList();
        }

        /// <summary>
        /// Obtém as últimas N mensagens
        /// </summary>
        public List<Message> GetRecentMessages(int count)
        {
            return _messages.TakeLast(count).ToList();
        }

        /// <summary>
        /// Limpa todo o histórico
        /// </summary>
        public void Clear()
        {
            _messages.Clear();
        }

        /// <summary>
        /// Conta total de mensagens
        /// </summary>
        public int MessageCount => _messages.Count;

        /// <summary>
        /// Obtém estatísticas da conversação
        /// </summary>
        public (int UserMessages, int ModelMessages, TimeSpan Duration) GetStats()
        {
            var userMsgs = _messages.Count(m => m.Role == "user");
            var modelMsgs = _messages.Count(m => m.Role == "model");

            var duration = TimeSpan.Zero;
            if (_messages.Count >= 2)
            {
                duration = _messages.Last().Timestamp - _messages.First().Timestamp;
            }

            return (userMsgs, modelMsgs, duration);
        }

        /// <summary>
        /// Exporta o histórico para texto
        /// </summary>
        public string ExportToText()
        {
            var lines = _messages.Select(m =>
                $"[{m.Timestamp:yyyy-MM-dd HH:mm:ss}] {m.Role.ToUpper()}: {m.Content}"
            );
            return string.Join("\n\n", lines);
        }
    }
}
