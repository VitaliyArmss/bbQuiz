using Npgsql;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Telegram.Bot.Types;

namespace bbQuiz
{
    internal class ChatSettings
    {
        internal static ConcurrentDictionary<long, int> TimeBetweenHints = new();
        internal static ConcurrentDictionary<long, int> SkipsNeed = new();
        internal static ConcurrentDictionary<long, long> isChatBeingConfigured = new();
        internal static ConcurrentDictionary<long, string> whichSettingChanging = new();
        internal static ConcurrentDictionary<long, SemaphoreSlim> semaphores = new();

        internal static async Task StartSettingsTimer(long chatId, long userId, int seconds)
        {
            isChatBeingConfigured[chatId] = userId;
            semaphores[chatId] = new SemaphoreSlim(1, 1);
            await Task.Delay(seconds * 1000);
            await CancelSettings(chatId);
        }
        internal static Task CancelSettings(long chatId)
        {
            isChatBeingConfigured.TryRemove(chatId, out _);
            whichSettingChanging.TryRemove(chatId, out _);
            semaphores.TryRemove(chatId, out var _);
            return Task.CompletedTask;
        }

        internal static async Task LoadSettings(long chatId)
        {
            if (SkipsNeed.TryGetValue(chatId, out var _)) return;
            const string query = "SELECT * FROM chatsettings WHERE chat_id = @chatId";

            await using var connection = new NpgsqlConnection(BotData.connString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("chatId", chatId);
            await using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                SkipsNeed[chatId] = reader.IsDBNull(1)
                    ? 2
                    : reader.GetInt32(1);

                TimeBetweenHints[chatId] = reader.IsDBNull(2)
                    ? 7
                    : reader.GetInt32(2);
            }
        }
        internal static async Task<int> GetSkipsNeed(long chatId)
        {
            if (SkipsNeed.TryGetValue(chatId, out var value))
                return value;

            await LoadSettings(chatId);

            if (SkipsNeed.TryGetValue(chatId, out value))
                return value;

            await SetSkipsNeed(chatId, 2);
            return 2;
        }
        internal async static Task SetSkipsNeed(long chatId, int value)
        {
            SkipsNeed[chatId] = value;

            using var conn = new NpgsqlConnection(BotData.connString);
            await conn.OpenAsync();

            string sql = @"
            INSERT INTO chatsettings (chat_id, skips_need)
            VALUES (@chatId, @value)
            ON CONFLICT (chat_id)
            DO UPDATE SET skips_need = EXCLUDED.skips_need";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("chatId", chatId);
            cmd.Parameters.AddWithValue("value", value);
            await cmd.ExecuteNonQueryAsync();
        }

        internal static async Task<int> GetTimeBetweenHints(long chatId)
        {
            if (TimeBetweenHints.TryGetValue(chatId, out var value))
                return value;

            await LoadSettings(chatId);

            if (TimeBetweenHints.TryGetValue(chatId, out value))
                return value;

            await SetTimeBetweenHints(chatId, 7);
            return 7;
        }
        internal async static Task SetTimeBetweenHints(long chatId, int value)
        {
            TimeBetweenHints[chatId] = value;

            using var conn = new NpgsqlConnection(BotData.connString);
            await conn.OpenAsync();

            string sql = @"
            INSERT INTO chatsettings (chat_id, time_between_hints)
            VALUES (@chatId, @value)
            ON CONFLICT (chat_id)
            DO UPDATE SET time_between_hints = EXCLUDED.time_between_hints";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("chatId", chatId);
            cmd.Parameters.AddWithValue("value", value);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
