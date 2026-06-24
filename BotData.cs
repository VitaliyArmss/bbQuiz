using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace bbQuiz
{
    internal static class BotData
    {
        internal static string connString { get; set; }

        internal static readonly DateTime startedAt =
            DateTime.UtcNow.AddSeconds(5);

        internal static ConcurrentDictionary<long, GameState> Games { get; }
            = new();

        internal static ConcurrentDictionary<long, ConcurrentDictionary<long, int>> GlobalScores = new();
    }
}
