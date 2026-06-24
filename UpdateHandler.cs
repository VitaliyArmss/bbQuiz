using System.ComponentModel.Design;
using System.Net.Http.Headers;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace bbQuiz
{
    internal static class UpdateHandler
    {
        internal static async Task HandleUpdateAsync(ITelegramBotClient botClient,
            Update update,
            CancellationToken cancellationToken)
        {
            if (update.CallbackQuery != null)
            {
                await HandleCallbackQuery(botClient, update.CallbackQuery);
                return;
            }

            if (update.Message == null) return;

            if (update.Message.Date < BotData.startedAt) return;

            var message = update.Message;
            if (message?.Text == null)
                return;

            await HandleMessage(botClient, message);
        }

        internal static async Task HandleCallbackQuery(ITelegramBotClient botClient, CallbackQuery callback)
        {
            if (callback.Message == null || string.IsNullOrEmpty(callback.Data))
                return;

            long chatId = callback.Message.Chat.Id;
            var data = callback.Data;

            await botClient.AnswerCallbackQuery(callback.Id);

            await botClient.DeleteMessage(
                callback.Message.Chat.Id,
                callback.Message.MessageId
            );

            if (BotData.Games.ContainsKey(chatId))
            {
                if (!BotData.Games[chatId].isStarting)
                {
                    await botClient.SendMessage(chatId,
                    "Игра уже запущена! Отправьте /stop, чтобы закончить текущую игру.");
                    return;
                }
            }

            if (data.StartsWith("category:"))
            {;

                BotData.Games[chatId] = new GameState
                {
                    Category = data.Split(':')[1]
                };

                await botClient.SendMessage(
                chatId,
                "Выберите длину игры:",
                replyMarkup: Keyboards.Rounds
                );
            }
            else if (data.StartsWith("roundsCnt:"))
            {
                BotData.Games[chatId].Rounds = int.Parse(data.Split(':')[1]);
                BotLogic.StartGame(botClient, callback.Message.Chat.Id, BotData.Games[chatId]);
            }
            else if (data.StartsWith("settings:"))
            {
                var setting_obj = data.Split(':')[1];
                switch (setting_obj)
                {
                    case "skips":
                        ChatSettings.whichSettingChanging[chatId] = "skips";
                        await botClient.SendMessage(
                            chatId,
                            "Введите кол-во скипов:"
                        );
                        break;
                    case "time":
                        ChatSettings.whichSettingChanging[chatId] = "time";
                        await botClient.SendMessage(
                            chatId,
                            "Введите время в секундах: "
                        );
                        break;
                    default:
                        break;
                }
            }
            return;
        }
        internal static async Task HandleMessage(ITelegramBotClient botClient, Telegram.Bot.Types.Message message)
        {
            long chatId = message.Chat.Id;
            long userId = message.From.Id;
            string userName = message.From.FirstName;

            if (ChatSettings.isChatBeingConfigured.TryGetValue(chatId, out var settingUserId))
            {
                if (settingUserId != userId)
                {
                    await botClient.SendMessage(chatId,
                        "Сейчас другой пользователь настраивает игру. Пожалуйста, подождите.");
                    return;
                }
                await HandleSettingsInput(botClient, message);
                return;
            }

            if (await HandleCommand(botClient, message))
            {
                BotLogger.Commands.Information("Команда от {UserId} в чате {ChatId}: {Text}", message.From.Id, message.Chat.Id, message.Text);
                return;
            }
            if (BotData.Games.TryGetValue(chatId, out var game))
            {
                await HandleAnswer(botClient, message, game);
            }
        }
        internal static async Task<bool> HandleCommand(
            ITelegramBotClient botClient,
            Telegram.Bot.Types.Message message)
        {
            long chatId = message.Chat.Id;

            switch (message.Text)
            {
                case "/scores":
                case "/scores@blueberry_quiz_bot":
                    await CommandHandler.ScoresCommand(botClient, chatId);
                    return true;
                case "/help":
                case "/help@blueberry_quiz_bot":
                    await CommandHandler.HelpCommand(botClient, chatId);
                    return true;
                case "/start":
                case "/start@blueberry_quiz_bot":
                    await CommandHandler.StartCommand(botClient, chatId);
                    return true;
                case "/settings":
                case "/settings@blueberry_quiz_bot":
                    await CommandHandler.SettingsCommand(botClient, chatId, message.From.Id);
                    return true;
                default:
                    // Неизвестная команда
                    break;
            }

            // Если команда относится к игре
            if (BotData.Games.TryGetValue(chatId, out var game))
            {
                switch (message.Text)
                {
                    case "/stop":
                    case "/stop@blueberry_quiz_bot":
                        await CommandHandler.StopCommand(botClient, chatId, game);
                        return true;
                    case "/skip":
                    case "/skip@blueberry_quiz_bot":
                        await CommandHandler.SkipCommand(botClient, chatId, message.From.Id, message.From.FirstName, game);
                        return true;
                    default:
                        // Неизвестная команда
                        break;
                }
            }

            return false;
        }

        internal static async Task HandleAnswer(
            ITelegramBotClient botClient,
            Telegram.Bot.Types.Message message,
            GameState game)
        {
            await game.Lock.WaitAsync();
            try
            {
                if (!game.ActiveQuestion) return;

                game.EmptyRounds = 0; // обновляем счетчик пустых раундов, если кто-то написал сообщение
                game.Names[message.From.Id] = message.From.FirstName; //обновляем имя пользователя в словаре имен

                var current = game.Questions[game.Index];

                if (message.Text.Equals(current.Value, StringComparison.OrdinalIgnoreCase))
                {
                    game.Scores.AddOrUpdate(message.From.Id, 1, (k, v) => v + 1);
                    game.ActiveQuestion = false;

                    game.Timer?.Cancel();

                    BotLogger.Game.Information("Чат {chatId}: {userId} ответил правильно: {message}", message.Chat.Id, message.From.Id, message.Text);

                    await botClient.SendMessage(message.Chat.Id,
                        $"✅ {message.From.FirstName} ответил правильно!");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1000);
                            await BotLogic.MoveNextQuestion(botClient, message.Chat.Id);
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Errors.Error("Ошибка перехода к след. вопросу в чате: {chatId}. {message}", message.Chat.Id, ex.Message);
                        }
                    });
                }
            }
            finally
            {
                game.Lock.Release();
            }
        }

        internal static async Task HandleSettingsInput(
    ITelegramBotClient botClient,
    Telegram.Bot.Types.Message message)
        {
            long chatId = message.Chat.Id;

            if (!ChatSettings.whichSettingChanging.TryGetValue(chatId, out var setting))
                return;

            if (!ChatSettings.semaphores.TryGetValue(chatId, out var semaphore))
                return;

            await semaphore.WaitAsync();
            try
            {
                switch (setting)
                {
                    case "skips":
                        if (int.TryParse(message.Text, out int skips) && skips > 0 && skips < 20)
                        {
                            await ChatSettings.SetSkipsNeed(chatId, skips);
                            await ChatSettings.CancelSettings(chatId);

                            BotLogger.Settings.Information("Чат {chatId} изменил skips_needed на {skips}", chatId, skips);

                            await botClient.SendMessage(
                                chatId,
                                $"Кол-во скипов для пропуска вопроса установлено на {skips}.");
                            return;
                        }

                        await botClient.SendMessage(
                            chatId,
                            "Пожалуйста, введите корректное число для скипов.");
                        return;

                    case "time":
                        if (int.TryParse(message.Text, out int time) && time > 0 && time < 61)
                        {
                            await ChatSettings.SetTimeBetweenHints(chatId, time);
                            await ChatSettings.CancelSettings(chatId);

                            BotLogger.Settings.Information("Чат {chatId} изменил time_between_hints на {time}", chatId, time);

                            await botClient.SendMessage(
                                chatId,
                                $"Время между подсказками установлено на {time} секунд.");
                            return;
                        }

                        await botClient.SendMessage(
                            chatId,
                            "Пожалуйста, введите корректное число для времени.");
                        return;
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        internal static Task HandleErrorAsync(
            ITelegramBotClient botClient,
            Exception exception,
            CancellationToken cancellationToken)
        {
            BotLogger.Errors.Error(exception, "Произошла ошибка");
            Console.WriteLine(exception);
            return Task.CompletedTask;
        }
    }
}