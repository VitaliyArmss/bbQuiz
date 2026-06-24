using System;
using System.Collections.Generic;
using System.Text;
using Serilog;
using Serilog.Sinks.File;
using Serilog.Sinks.SystemConsole;

namespace bbQuiz
{
    internal static class BotLogger
    {
        public static readonly ILogger Game = new LoggerConfiguration()
            .WriteTo.File("logs/games/game-.log",
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        public static readonly ILogger Settings = new LoggerConfiguration()
            .WriteTo.File("logs/settings/settings-.log",
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        public static readonly ILogger Commands = new LoggerConfiguration()
            .WriteTo.File("logs/settings/commands-.log",
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        public static readonly ILogger Errors = new LoggerConfiguration()
            .WriteTo.File("logs/errors/error-.log",
                rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }
}
