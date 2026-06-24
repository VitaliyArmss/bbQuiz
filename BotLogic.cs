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

namespace bbQuiz
{
    static class BotLogic
    {
        static async Task Main() // Запуск
        {
            Env.Load(Path.Combine(AppContext.BaseDirectory, ".env"));
            var token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN");
            BotData.connString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");

            var botClient = new TelegramBotClient(token);

            using var cts = new CancellationTokenSource();

            botClient.StartReceiving(
                UpdateHandler.HandleUpdateAsync,
                UpdateHandler.HandleErrorAsync,
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
            new BotCommand { Command = "/skip", Description = "Пропустить вопрос (требуется от двух пользователей)" },
            new BotCommand { Command = "/settings", Description = "Настроить игру" }
        });

            var me = await botClient.GetMe();
            Console.WriteLine($"Бот @{me.Username} запущен");

            await Task.Delay(Timeout.Infinite, cts.Token);
        }

        internal static async Task StartGame(
            ITelegramBotClient botClient,
            long chatId,
            GameState game)
        {
            try
            {
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

                game.isStarting = false;

                await botClient.SendMessage(chatId,
                $"🎮 Викторина началась!\n\nВопрос 1:\n{first.Key}");

                StartQuestionTimer(botClient, chatId, await ChatSettings.GetTimeBetweenHints(chatId));
            }

            catch (Exception ex) {
                game.isStarting = true;
                Console.WriteLine($"{ex.Message}");
            }
            
        }
        internal static void StartQuestionTimer(
            ITelegramBotClient botClient,
            long chatId,
            int time)
        {
            if (!BotData.Games.TryGetValue(chatId, out var game))
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

        internal static async Task MoveNextQuestion(
            ITelegramBotClient botClient,
            long chatId)
        {
            if (!BotData.Games.TryGetValue(chatId, out var game))
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

                    StartQuestionTimer(botClient, chatId, await ChatSettings.GetTimeBetweenHints(chatId));
                }
                else
                {
                    await SaveResults(botClient, chatId);
                    await ShowResults(botClient, chatId);
                    game.Dispose();
                    BotData.Games.TryRemove(chatId, out _);
                }
            }
            finally
            {
                game.Lock.Release();
            }
        }

        internal static async Task SaveResults(
            ITelegramBotClient botClient,
            long chatId)
        {
            if (!BotData.Games.TryGetValue(chatId, out var game))
                return;

            using var conn = new NpgsqlConnection(BotData.connString);
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

        internal static async Task ShowResults(
            ITelegramBotClient botClient,
            long chatId)
        {
            if (!BotData.Games.TryGetValue(chatId, out var game))
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

        internal static async Task<Dictionary<string, string>> GetQuestions(int count, string category)
        {
            var dict = new Dictionary<string, string>();

            var allowed = new[] { "random", "cinema", "music", "food", "nature", "geography", "videogames", "locale" };
            if (!allowed.Contains(category))
                category = "random";

            try
            {
                using var conn = new NpgsqlConnection(BotData.connString);
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

        internal static async Task<string> GetHint(string answer, int letters_cnt)
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

        internal static async Task FinishGame(
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
            BotData.Games.TryRemove(chatId, out _);
        }
    }
}
