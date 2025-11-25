using System;

namespace ScrapperGranular.Models
{
    /// <summary>
    /// Representa uma mensagem na conversação com a IA
    /// </summary>
    public record Message
    {
        public string Role { get; init; } 
        public string Content { get; init; }
        public DateTime Timestamp { get; init; }

        public Message(string role, string content)
        {
            Role = role;
            Content = content;
            Timestamp = DateTime.Now;
        }
    }
}
