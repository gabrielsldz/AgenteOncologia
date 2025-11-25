using System;
using System.Collections.Generic;
using System.Linq;

namespace ScrapperGranular.Utils
{
    /// <summary>
    /// Sistema de logging colorido e intuitivo para o terminal
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new();
        private static bool _enableColors = true;

        // Cores ANSI
        private static class Colors
        {
            public const string Reset = "\u001b[0m";
            public const string Gray = "\u001b[90m";
            public const string Red = "\u001b[91m";
            public const string Green = "\u001b[92m";
            public const string Yellow = "\u001b[93m";
            public const string Blue = "\u001b[94m";
            public const string Magenta = "\u001b[95m";
            public const string Cyan = "\u001b[96m";
            public const string White = "\u001b[97m";

            // Bold
            public const string BoldRed = "\u001b[1;91m";
            public const string BoldGreen = "\u001b[1;92m";
            public const string BoldYellow = "\u001b[1;93m";
            public const string BoldCyan = "\u001b[1;96m";
            public const string BoldMagenta = "\u001b[1;95m";
        }

        public static void EnableColors(bool enable) => _enableColors = enable;

        private static string Colorize(string text, string color)
        {
            return _enableColors ? $"{color}{text}{Colors.Reset}" : text;
        }

        private static string Timestamp()
        {
            return DateTime.Now.ToString("HH:mm:ss");
        }

        // ============================================
        // MÉTODOS DE LOG PRINCIPAIS
        // ============================================

        public static void Info(string message)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] INFO   ", Colors.Cyan);
                var icon = "ℹ️ ";
                Console.WriteLine($"{prefix} {icon} {message}");
            }
        }

        public static void Success(string message)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] SUCCESS", Colors.BoldGreen);
                var icon = "✅";
                Console.WriteLine($"{prefix} {icon} {message}");
            }
        }

        public static void Warning(string message)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] WARNING", Colors.BoldYellow);
                var icon = "⚠️ ";
                Console.WriteLine($"{prefix} {icon} {message}");
            }
        }

        public static void Error(string message, Exception? ex = null)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] ERROR  ", Colors.BoldRed);
                var icon = "❌";
                Console.WriteLine($"{prefix} {icon} {message}");

                if (ex != null)
                {
                    Console.WriteLine(Colorize($"         └─> {ex.GetType().Name}: {ex.Message}", Colors.Red));
                }
            }
        }

        public static void Debug(string message)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] DEBUG  ", Colors.Gray);
                var icon = "🔍";
                Console.WriteLine($"{prefix} {icon} {message}");
            }
        }

        public static void Metric(string label, object value, string? unit = null)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] METRIC ", Colors.Blue);
                var icon = "📊";
                var valueStr = unit != null ? $"{value}{unit}" : value.ToString();
                Console.WriteLine($"{prefix} {icon} {Colorize(label, Colors.White)}: {Colorize(valueStr!, Colors.BoldCyan)}");
            }
        }

        // ============================================
        // LOGS ESPECIALIZADOS
        // ============================================

        public static void CacheHit(string level, float? similarity = null)
        {
            lock (_lock)
            {
                var icons = new Dictionary<string, string>
                {
                    ["EXACT"] = "⚡",
                    ["NORMALIZED"] = "⚡",
                    ["SEMANTIC"] = "⚡",
                    ["SQL_GENERATION"] = "🔧",
                    ["SQL_RESULTS"] = "💾",
                    ["PATTERN"] = "🎯",
                    ["MATERIALIZED_VIEW"] = "⚡"
                };

                var prefix = Colorize($"[{Timestamp()}] CACHE  ", Colors.BoldMagenta);
                var icon = icons.ContainsKey(level.ToUpper()) ? icons[level.ToUpper()] : "⚡";
                var similarityStr = similarity.HasValue ? $" ({similarity.Value:P0} similar)" : "";

                var levelColor = level.ToUpper() switch
                {
                    "SQL_GENERATION" => Colors.Yellow,
                    "SQL_RESULTS" => Colors.Cyan,
                    "PATTERN" => Colors.Magenta,
                    "MATERIALIZED_VIEW" => Colors.Blue,
                    _ => Colors.BoldGreen
                };

                Console.WriteLine($"{prefix} {icon} CACHE HIT - {Colorize(level.ToUpper(), levelColor)}{similarityStr}");
            }
        }

        public static void CacheMiss(string level)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] CACHE  ", Colors.Gray);
                var icon = "⚡";
                Console.WriteLine($"{prefix} {icon} Cache {level} não encontrado");
            }
        }

        public static void QueryExecution(string sql, int rows, long milliseconds)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] QUERY  ", Colors.Cyan);
                var icon = "💾";
                Console.WriteLine($"{prefix} {icon} Executando query...");

                // SQL formatado em box
                var sqlLines = sql.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l));
                foreach (var line in sqlLines)
                {
                    Console.WriteLine(Colorize($"         │ {line}", Colors.Gray));
                }

                Console.WriteLine($"{prefix} {icon} {Colorize($"{rows} linha(s)", Colors.BoldCyan)} retornada(s) em {Colorize($"{milliseconds}ms", Colors.BoldYellow)}");
            }
        }

        public static void QueryResult(Dictionary<string, object> result)
        {
            lock (_lock)
            {
                var prefix = Colorize($"[{Timestamp()}] RESULT ", Colors.Cyan);
                Console.WriteLine($"{prefix} 📋 Resultado:");

                foreach (var (key, value) in result)
                {
                    Console.WriteLine($"         │ {Colorize(key, Colors.White)}: {Colorize(value.ToString()!, Colors.BoldCyan)}");
                }
            }
        }

        // ============================================
        // FORMATAÇÃO ESPECIAL
        // ============================================

        public static void Box(string title, string content, int width = 60)
        {
            lock (_lock)
            {
                var topBorder = "┌" + new string('─', width - 2) + "┐";
                var bottomBorder = "└" + new string('─', width - 2) + "┘";
                var titleLine = "│ " + Colorize(title.PadRight(width - 4), Colors.BoldCyan) + " │";
                var divider = "├" + new string('─', width - 2) + "┤";

                Console.WriteLine(Colorize(topBorder, Colors.Gray));
                Console.WriteLine(titleLine);
                Console.WriteLine(Colorize(divider, Colors.Gray));

                // Quebrar conteúdo em linhas
                var contentLines = WrapText(content, width - 4);
                foreach (var line in contentLines)
                {
                    Console.WriteLine($"│ {line.PadRight(width - 4)} │");
                }

                Console.WriteLine(Colorize(bottomBorder, Colors.Gray));
            }
        }

        public static void Separator(char character = '═', int width = 60)
        {
            lock (_lock)
            {
                Console.WriteLine(Colorize(new string(character, width), Colors.Gray));
            }
        }

        public static void BigSeparator(string title = "")
        {
            lock (_lock)
            {
                Console.WriteLine();
                Separator('═', 60);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var paddedTitle = $" {title} ";
                    var padding = (60 - paddedTitle.Length) / 2;
                    Console.WriteLine(Colorize(
                        new string(' ', padding) + paddedTitle + new string(' ', padding),
                        Colors.BoldCyan
                    ));
                    Separator('═', 60);
                }
                Console.WriteLine();
            }
        }

        public static void Statistics(Dictionary<string, object> stats)
        {
            lock (_lock)
            {
                BigSeparator("ESTATÍSTICAS DA SESSÃO");

                foreach (var (key, value) in stats)
                {
                    var formattedKey = key.PadRight(25);
                    Console.WriteLine($"{Colorize(formattedKey, Colors.White)}: {Colorize(value.ToString()!, Colors.BoldGreen)}");
                }

                Separator('═', 60);
                Console.WriteLine();
            }
        }

        // ============================================
        // HELPERS
        // ============================================

        private static List<string> WrapText(string text, int maxWidth)
        {
            var lines = new List<string>();
            var words = text.Split(' ');
            var currentLine = "";

            foreach (var word in words)
            {
                if ((currentLine + word).Length > maxWidth)
                {
                    if (!string.IsNullOrWhiteSpace(currentLine))
                    {
                        lines.Add(currentLine.Trim());
                        currentLine = "";
                    }
                }
                currentLine += word + " ";
            }

            if (!string.IsNullOrWhiteSpace(currentLine))
            {
                lines.Add(currentLine.Trim());
            }

            return lines;
        }

        // ============================================
        // PROGRESS BAR
        // ============================================

        public static void Progress(int current, int total, string label = "Progresso")
        {
            lock (_lock)
            {
                var percent = (double)current / total;
                var barWidth = 40;
                var filled = (int)(barWidth * percent);
                var bar = new string('█', filled) + new string('░', barWidth - filled);

                Console.Write($"\r{Colorize($"[{Timestamp()}] ", Colors.Gray)}{label}: [{Colorize(bar, Colors.BoldGreen)}] {percent:P0} ({current}/{total})");

                if (current == total)
                {
                    Console.WriteLine(); // Nova linha quando completo
                }
            }
        }
    }
}
