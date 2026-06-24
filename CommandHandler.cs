using Npgsql;
using System;
using System.Collections.Generic;
using System.Text;
using Telegram.Bot;

namespace bbQuiz
{
    internal class CommandHandler
    {
        internal static async Task StartCommand(ITelegramBotClient botClient,
            long chatId) 
        {
            if (BotData.Games.ContainsKey(chatId))
            {
                if (!BotData.Games[chatId].isStarting)
                {
                    await botClient.SendMessage(chatId,
                        "Игра уже запущена! Отправьте /stop, чтобы закончить текущую игру.");
                }
                return;
            }

            await botClient.SendMessage( // выбор категории
                chatId,
                "Выберите тему:",
                replyMarkup: Keyboards.Categories
            );
        }
        internal static async Task StopCommand(ITelegramBotClient botClient,
            long chatId, GameState game)
        {
            await BotLogic.FinishGame(botClient, chatId, game);
        }
        internal static async Task SkipCommand(ITelegramBotClient botClient,
            long chatId, long userId, string userName, GameState game)
        {
            await game.Lock.WaitAsync();
            try
            {
                if (!game.ActiveQuestion) return;
                if (game.Skips.Contains(userId)) return;
                game.Skips.Add(userId);

                if (game.Skips.Count >= await ChatSettings.GetSkipsNeed(chatId))
                {
                    game.ActiveQuestion = false;
                    game.Timer?.Cancel();
                    var answer = game.Questions[game.Index].Value;
                    await botClient.SendMessage(chatId,
                        $"⏩ Вопрос пропущен! Ответ: {answer}");

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        await BotLogic.MoveNextQuestion(botClient, chatId);
                    });
                }
                game.EmptyRounds = 0; // обновляем счетчик пустых раундов, если кто-то написал сообщение
                game.Names[userId] = userName; //обновляем имя пользователя в словаре имен
            }
            finally
            {
                game.Lock.Release();
            }
        }
        internal static async Task ScoresCommand(
        ITelegramBotClient botClient,
        long chatId)
        {
            var list = new List<(string Name, int Score)>();

            using var conn = new NpgsqlConnection(BotData.connString);
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
        internal static async Task HelpCommand(ITelegramBotClient botClient,
            long chatId)
        {
            await botClient.SendMessage(chatId,
                    "Привет! Я викторина-бот. Отправь /start, чтобы начать игру.");
        }
        internal static async Task SettingsCommand(ITelegramBotClient botClient,
            long chatId, long userId)
        {
            if (BotData.Games.TryGetValue(chatId, out var game))
            {
                await botClient.SendMessage(chatId,
                    "Настройки недоступны во время игры");
                return;
            }
            ChatSettings.StartSettingsTimer(chatId, userId, 60);
            await botClient.SendMessage(chatId,
                "Что вы хотите настроить?", replyMarkup: await Keyboards.GetSettingsKeyboard(chatId));
        }
    }
}
