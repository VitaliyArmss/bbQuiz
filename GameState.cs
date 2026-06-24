using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace bbQuiz
{
    internal class GameState
    {
        public bool isStarting = true;
        public string Category = "random";
        public int Rounds = 15;
        public int Index = 0;
        public List<KeyValuePair<string, string>> Questions;
        public ConcurrentDictionary<long, int> Scores = new();
        public bool ActiveQuestion = false;
        public CancellationTokenSource Timer;
        public ConcurrentDictionary<long, string> Names = new();
        public ConcurrentBag<long> Skips = new();
        public SemaphoreSlim Lock = new(1, 1);
        public int EmptyRounds = 0;
        public void Dispose()
        {
            isStarting = true;
            Index = 0;
            Questions?.Clear();
            Names?.Clear();
            ActiveQuestion = false;
            Timer?.Dispose();
            Lock.Dispose();
        }
    }
}
