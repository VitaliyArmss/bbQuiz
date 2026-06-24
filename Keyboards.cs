using Telegram.Bot.Types.ReplyMarkups;

namespace bbQuiz
{
    public static class Keyboards
    {
        public static readonly InlineKeyboardMarkup Categories =
            new(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "Случайные",
                        "category:random")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "Фильмы и сериалы 🎥",
                        "category:cinema"),
                    InlineKeyboardButton.WithCallbackData(
                        "Музыка 🎵",
                        "category:music")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "Видеоигры 🎮",
                        "category:videogames"),
                    InlineKeyboardButton.WithCallbackData(
                        "Еда и напитки 🍞",
                        "category:food")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "Животные и природа 🐶",
                        "category:nature"),
                    InlineKeyboardButton.WithCallbackData(
                        "География 🏝️",
                        "category:geography")
                }
            });

        public static readonly InlineKeyboardMarkup Rounds =
            new(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("5 раундов", "roundsCnt:5"),
                    InlineKeyboardButton.WithCallbackData("15 раундов", "roundsCnt:15"),
                    InlineKeyboardButton.WithCallbackData("30 раундов", "roundsCnt:30")
                }
            });
        public async static Task<InlineKeyboardMarkup> GetSettingsKeyboard(long chatId)
        {
            // Сначала получаем значения (асинхронно)
            var skips = await ChatSettings.GetSkipsNeed(chatId);
            var time = await ChatSettings.GetTimeBetweenHints(chatId);

            // Потом создаем клавиатуру (синхронно)
            return new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData($"Кол-во необходимых скипов ({skips})", "settings:skips")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData($"Время между подсказками ({time})", "settings:time")
                }
            });
        }
    }
}