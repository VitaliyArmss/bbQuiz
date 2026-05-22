using DotNetEnv;
using Npgsql;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static System.Runtime.InteropServices.JavaScript.JSType;

class Program
{
    static string connString;
    static readonly DateTime startedAt = DateTime.UtcNow.AddSeconds(10);
    //static DateTime vadimtried = DateTime.UtcNow.AddMinutes(-60);
    class GameState
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
        public int SkipsNeeded = 2;
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

    static ConcurrentDictionary<long, ConcurrentDictionary<long, int>> GlobalScores = new();
    static ConcurrentDictionary<long, GameState> Games = new();

    static async Task Main()
    {
        Env.Load(Path.Combine(AppContext.BaseDirectory, ".env"));
        var token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN");
        var botClient = new TelegramBotClient(token);
        connString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");

        using var cts = new CancellationTokenSource();

        botClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            new ReceiverOptions
            {
                DropPendingUpdates = true
            },
            cancellationToken: cts.Token
        );

        await botClient.SetMyCommands(new[]
        {
            new BotCommand { Command = "/start", Description = "Начать игру" },
            new BotCommand { Command = "/stop", Description = "Закончить игру" },
            new BotCommand { Command = "/help", Description = "Список команд" },
            new BotCommand { Command = "/scores", Description = "Показать общий счет" },
            new BotCommand { Command = "/skip", Description = "Пропустить вопрос (требуется от двух пользователей)" }
        });

        var me = await botClient.GetMe();
        Console.WriteLine($"Бот @{me.Username} запущен");

        await Task.Delay(Timeout.Infinite, cts.Token);
    }

    static async Task HandleUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken)
    {
        long chatId;

        // Игнор старых сообщений
        if (update.Message != null &&
            update.Message.Date < startedAt)
            return;

        if (update.CallbackQuery != null)
        {
            var callback = update.CallbackQuery;
            chatId = callback.Message.Chat.Id;
            var data = callback.Data;

            // (желательно) убрать "часики" у кнопки
            await botClient.AnswerCallbackQuery(callback.Id);

            await botClient.DeleteMessage(
                callback.Message.Chat.Id,
                callback.Message.MessageId
            );

            if (Games.ContainsKey(chatId))
            {
                if (!Games[chatId].isStarting)
                {
                    await botClient.SendMessage(chatId,
                    "Игра уже запущена! Отправьте /stop, чтобы закончить текущую игру.");
                    return;
                }
            }

            if (!(new[] { "5", "15", "30" }.Contains(data)))
            {
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new []
                    {
                        InlineKeyboardButton.WithCallbackData("5 раундов", "5"),
                        InlineKeyboardButton.WithCallbackData("15 раундов", "15"),
                        InlineKeyboardButton.WithCallbackData("30 раундов", "30")
                    }

                });

                Games[chatId] = new GameState
                {
                    Category = data
                };

                await botClient.SendMessage(
                chatId,
                "Выберите длину игры:",
                replyMarkup: keyboard
                );
            }
            else
            {
                Games[chatId].Rounds = int.Parse(data);
                StartGame(botClient, callback.Message.Chat.Id, Games[chatId]);
            }
            return;
        }

        var message = update.Message;
        if (message?.Text == null) return;

        chatId = message.Chat.Id;
        long userId = message.From.Id;
        string userName = message.From.FirstName;

        //if (userId == 1803956376)
        //{
        //    if (!Games.ContainsKey(chatId))
        //    {
        //        if (!((DateTime.UtcNow - vadimtried).TotalMinutes < 60))
        //        {
        //            Thread.Sleep(5000);
        //            await botClient.SendMessage(chatId,
        //            "о вадим ты в сети");
        //            Thread.Sleep(1000);
        //            await botClient.SendMessage(chatId,
        //                    "давай продолжим тестирование");
        //            Games[chatId] = new GameState
        //            {
        //                Category = "random",
        //                Rounds = 5
        //            };
        //            StartGame(botClient, chatId, Games[chatId]);
        //            vadimtried = DateTime.UtcNow;
        //        }
        //    }
        //}

        if (message.Text == "/secretmode" || message.Text == "/secretmode@blueberry_quiz_bot")
        {
            if (Games.ContainsKey(chatId))
            {
                await botClient.SendMessage(chatId,
                    "Игра уже запущена! Отправьте /stop, чтобы закончить текущую игру.");
                return;
            }
            StartGameSecretMode(botClient, chatId);
            return;
        }

        if (message.Text == "/scores" || message.Text == "/scores@blueberry_quiz_bot")
        {
            await ShowGlobalScores(botClient, chatId);
            return;
        }

        if (message.Text == "/help" || message.Text == "/help@blueberry_quiz_bot")
        {
            await botClient.SendMessage(chatId,
                "Привет! Я викторина-бот. Отправь /start, чтобы начать игру.");
            return;
        }

        if (message.Text == "/start" || message.Text == "/start@blueberry_quiz_bot")
        {
            if (Games.ContainsKey(chatId))
            {
                if (!Games[chatId].isStarting)
                {
                    await botClient.SendMessage(chatId,
                        "Игра уже запущена! Отправьте /stop, чтобы закончить текущую игру.");
                }
                return;
            }
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new []
                {
                    InlineKeyboardButton.WithCallbackData("Случайные", "random")
                },
                new []
                {
                    InlineKeyboardButton.WithCallbackData("Фильмы и сериалы 🎥", "cinema"),
                    InlineKeyboardButton.WithCallbackData("Музыка 🎵", "music")
                },
                new []
                {
                    InlineKeyboardButton.WithCallbackData("Видеоигры 🎮", "videogames"),
                    InlineKeyboardButton.WithCallbackData("Еда и напитки 🍞", "food")
                },
                new []
                {
                    InlineKeyboardButton.WithCallbackData("Животные и природа 🐶", "nature"),
                    InlineKeyboardButton.WithCallbackData("География 🏝️", "geography")
                }

            });

            await botClient.SendMessage(
                chatId,
                "Выберите тему:",
                replyMarkup: keyboard
            );

            return;
        }

        if (!Games.TryGetValue(chatId, out var game))
            return;
        game.EmptyRounds = 0;
        game.Names[userId] = userName;

        if (message.Text == "/stop" || message.Text == "/stop@blueberry_quiz_bot")
        {
            await FinishGame(botClient, chatId, game);
            return;
        }

        if (message.Text == "/skip" || message.Text == "/skip@blueberry_quiz_bot")
        {
            await game.Lock.WaitAsync();
            try
            {
                if (!game.ActiveQuestion) return;
                if (game.Skips.Contains(userId)) return;
                game.Skips.Add(userId);

                if (game.Skips.Count >= game.SkipsNeeded)
                {
                    game.ActiveQuestion = false;
                    game.Timer?.Cancel();
                    var answer = game.Questions[game.Index].Value;
                    await botClient.SendMessage(chatId,
                        $"⏩ Вопрос пропущен! Ответ: {answer}");

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        await MoveNextQuestion(botClient, chatId);
                    });
                }
            }
            finally
            {
                game.Lock.Release();
            }
        }

        await game.Lock.WaitAsync();
        try
        {
            if (!game.ActiveQuestion) return;

            var current = game.Questions[game.Index];

            if (message.Text.Equals(current.Value, StringComparison.OrdinalIgnoreCase))
            {
                game.Scores.AddOrUpdate(userId, 1, (k, v) => v + 1);
                //GlobalScores.AddOrUpdate(userId, 1, (k, v) => v + 1);
                game.ActiveQuestion = false;

                game.Timer?.Cancel();

                await botClient.SendMessage(chatId,
                    $"✅ {userName} ответил правильно!");

                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    await MoveNextQuestion(botClient, chatId);
                });
            }
        }
        finally
        {
            game.Lock.Release();
        }
    }

    static async Task StartGame(
        ITelegramBotClient botClient,
        long chatId,
        GameState game)
    {
        game.isStarting = false;
        var questions = await GetQuestions(game.Rounds, game.Category);

        if (questions == null || questions.Count == 0)
        {
            await botClient.SendMessage(chatId, "Ошибка загрузки вопросов 😢");
            return;
        }

        game.Questions = questions.ToList();
        game.ActiveQuestion = true;
        game.Index = 0;

        var first = game.Questions[0];

        await botClient.SendMessage(chatId,
            $"🎮 Викторина началась!\n\nВопрос 1:\n{first.Key}");

        StartQuestionTimer(botClient, chatId);
    }

    static async Task StartGameSecretMode(
        ITelegramBotClient botClient,
        long chatId)
    {
        var questions = await GetQuestions(100, "locale");

        if (questions == null || questions.Count == 0)
        {
            await botClient.SendMessage(chatId, "Ошибка загрузки вопросов 😢");
            return;
        }

        var game = new GameState
        {
            Rounds = questions.Count,
            Category = "locale"
        };

        Games[chatId] = game;

        game.Questions = questions.ToList();
        game.ActiveQuestion = true;
        game.Index = 0;

        var first = game.Questions[0];

        await botClient.SendMessage(chatId,
            $"🎮 Викторина началась!\n\nВопрос 1:\n{first.Key}");

        StartQuestionTimer(botClient, chatId);
    }

    static void StartQuestionTimer(
        ITelegramBotClient botClient,
        long chatId,
        int time = 7)
    {
        if (!Games.TryGetValue(chatId, out var game))
            return;

        var cts = new CancellationTokenSource();
        game.Timer = cts;

        _ = Task.Run(async () =>
        {
            string answer;

            await game.Lock.WaitAsync();
            try
            {
                answer = game.Questions[game.Index].Value;
            }
            finally
            {
                game.Lock.Release();
            }

            var steps = new[] { 0, answer.Length / 3 };

            foreach (var step in steps)
            {
                await Task.Delay(time * 1000, cts.Token);
                if (cts.Token.IsCancellationRequested) return;

                var hint = await GetHint(answer, step);
                await botClient.SendMessage(chatId, $"💡 Подсказка:\n{hint}");
            }

            await Task.Delay(time * 1000, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            await game.Lock.WaitAsync();
            try
            {
                if (game.ActiveQuestion)
                {
                    game.ActiveQuestion = false;

                    await botClient.SendMessage(chatId,
                        $"⏰ Время вышло! Ответ: {answer}");
                }
            }
            finally
            {
                game.Lock.Release();
            }

            await Task.Delay(1000);
            await MoveNextQuestion(botClient, chatId);
        });
    }

    static async Task MoveNextQuestion(
        ITelegramBotClient botClient,
        long chatId)
    {
        if (!Games.TryGetValue(chatId, out var game))
            return;
        game.EmptyRounds++;
        if (game.EmptyRounds > 2)
        {
            await botClient.SendMessage(chatId,
                $"Упс..😙 Кажется никто не играет.. Пока закончим");
            await FinishGame(botClient, chatId, game);
            return;
        }

        await game.Lock.WaitAsync();
        try
        {
            game.Skips.Clear();
            game.Timer?.Cancel();
            game.Timer?.Dispose();
            
            game.Index++;

            if (game.Index < game.Questions.Count)
            {
                var next = game.Questions[game.Index];
                game.ActiveQuestion = true;

                await botClient.SendMessage(chatId,
                    $"Вопрос {game.Index + 1}:\n{next.Key}");

                StartQuestionTimer(botClient, chatId);
            }
            else
            {
                await SaveResults(botClient, chatId);
                await ShowResults(botClient, chatId);
                game.Dispose();
                Games.TryRemove(chatId, out _);
            }
        }
        finally
        {
            game.Lock.Release();
        }
    }

    static async Task SaveResults(
        ITelegramBotClient botClient,
        long chatId)
    {
        if (!Games.TryGetValue(chatId, out var game))
            return;

        using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        foreach (var pair in game.Scores)
        {
            string sql = "INSERT INTO scores (chat_id, user_id, score, user_name)" +
                "\r\nVALUES (@chat_id, @user_id, @score, @user_name)" +
                "\r\nON CONFLICT (chat_id, user_id)" +
                "\r\nDO UPDATE SET " +
                "score = scores.score + EXCLUDED.score," +
                "user_name = EXCLUDED.user_name;";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("chat_id", chatId);
            cmd.Parameters.AddWithValue("user_id", pair.Key);
            cmd.Parameters.AddWithValue("score", pair.Value);
            cmd.Parameters.AddWithValue("user_name", game.Names[pair.Key]);

            await cmd.ExecuteNonQueryAsync();
        }
    }

    static async Task ShowResults(
        ITelegramBotClient botClient,
        long chatId)
    {
        if (!Games.TryGetValue(chatId, out var game))
            return;

        var result = string.Join("\n",
            game.Scores.OrderByDescending(x => x.Value)
                .Select(x => $"{game.Names[x.Key]}: {x.Value}"));

        if (string.IsNullOrEmpty(result))
        {
            await botClient.SendMessage(chatId,
                $"🏁 Игра окончена!");
        }
        else
        {
            await botClient.SendMessage(chatId,
                $"🏁 Игра окончена!\n\nРезультаты:\n{result}");
        }
    }
    static async Task ShowGlobalScores(
    ITelegramBotClient botClient,
    long chatId)
    {
        var list = new List<(string Name, int Score)>();

        using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        string sql =
            "SELECT user_name, score " +
            "FROM scores " +
            "WHERE chat_id = @chatId " +
            "ORDER BY score DESC " +
            "LIMIT 15;";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("chatId", chatId);

        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string name = reader["user_name"].ToString();
            int score = Convert.ToInt32(reader["score"]);

            list.Add((name, score));
        }

        var result = string.Join("\n",
            list.Select(x => $"{x.Name}: {x.Score}"));

        await botClient.SendMessage(chatId,
            $"🏆 Общий счет в данном чате:\n{result}");
    }

    static async Task<Dictionary<string, string>> GetQuestions(int count, string category)
    {
        var dict = new Dictionary<string, string>();

        var allowed = new[] { "random", "cinema", "music", "food", "nature", "geography", "videogames", "locale" };
        if (!allowed.Contains(category))
            category = "random";

        try {
            using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();

            string sql;
            if (category == "random")
            {
                sql = $"SELECT *" +
                    $"\r\nFROM (" +
                    $"\r\nSELECT * FROM cinema" +
                    $"\r\nUNION ALL" +
                    $"\r\nSELECT * FROM food" +
                    $"\r\nUNION ALL" +
                    $"\r\nSELECT * FROM geography" +
                    $"\r\nUNION ALL" +
                    $"\r\nSELECT * FROM music" +
                    $"\r\nUNION ALL" +
                    $"\r\nSELECT * FROM nature" +
                    $"\r\nUNION ALL" +
                    $"\r\nSELECT * FROM videogames" +
                    $"\r\n) AS combined" +
                    $"\r\nORDER BY RANDOM()" +
                    $"\r\nLIMIT @count;";
            }
            else
            {
                sql = $"SELECT * FROM \"{category}\" ORDER BY RANDOM() LIMIT @count";
            }

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("count", count);

            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                dict[reader["question"].ToString()] =
                    reader["answer"].ToString();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при получении вопросов: {ex.Message}");
            return null;
        }

        return dict;
    }

    static async Task<string> GetHint(string answer, int letters_cnt)
    {
        answer = answer.ToUpper();

        var chars = answer.ToCharArray();

        var letterPositions = Enumerable.Range(0, chars.Length)
            .Where(i => chars[i] != ' ' && chars[i] != '-')
            .ToList();

        var opened = new HashSet<int>();

        for (int i = 0; i < letters_cnt && letterPositions.Count > 0; i++)
        {
            int index = Random.Shared.Next(letterPositions.Count);

            opened.Add(letterPositions[index]);

            letterPositions.RemoveAt(index);
        }

        var result = new List<string>();

        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == ' ' || chars[i] == '-')
                result.Add(chars[i].ToString());
            else if (opened.Contains(i))
                result.Add(chars[i].ToString());
            else
                result.Add("_");
        }

        int lettersCount = chars.Count(c => c != ' ' && c != '-');
        return $"{string.Join(" ", result)} ({lettersCount} букв)";
    }

    static Task HandleErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(exception.Message);
        return Task.CompletedTask;
    }

    static async Task FinishGame(
    ITelegramBotClient botClient,
    long chatId,
    GameState game)
    {
        await game.Lock.WaitAsync();
        try
        {
            // 1. Остановить игру
            game.ActiveQuestion = false;
            game.Timer?.Cancel();
        }
        finally
        {
            game.Lock.Release();
        }

        await SaveResults(botClient, chatId);
        await ShowResults(botClient, chatId);

        // 3. УДАЛИТЬ ИЗ ПАМЯТИ
        game.Dispose();
        Games.TryRemove(chatId, out _);
    }
}