﻿using System;
using Exceptionless.Core;
using Exceptionless.Core.Extensions;
using Exceptionless.Insulation.Configuration;
using Exceptionless.Insulation.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LogLevel = Exceptionless.Logging.LogLevel;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.Exceptionless;

namespace Exceptionless.Insulation.Jobs {
    public class JobServiceProvider {
        public static IServiceProvider GetServiceProvider() {
            AppDomain.CurrentDomain.SetDataDirectory();

            string environment = Environment.GetEnvironmentVariable("AppMode");
            if (String.IsNullOrWhiteSpace(environment))
                environment = "Production";

            string currentDirectory = AppContext.BaseDirectory;
            var config = new ConfigurationBuilder()
                .SetBasePath(currentDirectory)
                .AddYamlFile("appsettings.yml", optional: true, reloadOnChange: true)
                .AddYamlFile($"appsettings.{environment}.yml", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var settings = Settings.ReadFromConfiguration(config, environment);
            settings.DisableIndexConfiguration = true;

            var loggerConfig = new LoggerConfiguration().ReadFrom.Configuration(config);

            if (!String.IsNullOrEmpty(Settings.Current.ExceptionlessApiKey) && !String.IsNullOrEmpty(Settings.Current.ExceptionlessServerUrl)) {
                var client = ExceptionlessClient.Default;
                client.Configuration.SetDefaultMinLogLevel(LogLevel.Warn);
                client.Configuration.UseLogger(new SelfLogLogger());
                client.Configuration.SetVersion(Settings.Current.Version);
                client.Configuration.UseInMemoryStorage();

                if (String.IsNullOrEmpty(Settings.Current.InternalProjectId))
                    client.Configuration.Enabled = false;

                client.Configuration.ServerUrl = Settings.Current.ExceptionlessServerUrl;
                client.Startup(Settings.Current.ExceptionlessApiKey);

                loggerConfig.WriteTo.Sink(new ExceptionlessSink(), LogEventLevel.Verbose);
            }

            Log.Logger = loggerConfig.CreateLogger();
            Log.Information("Bootstrapping {AppMode} mode job ({InformationalVersion}) on {MachineName} using {@Settings} loaded from {Folder}", environment, Settings.Current.InformationalVersion, Environment.MachineName, Settings.Current, currentDirectory);

            if (settings.EnableMetricsReporting)
                settings.MetricsConnectionString = MetricsConnectionString.Parse(settings.MetricsConnectionString?.ConnectionString);

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSerilog(Log.Logger));
            services.AddSingleton(settings);
            Core.Bootstrapper.RegisterServices(services);
            Bootstrapper.RegisterServices(services, true);

            var container = services.BuildServiceProvider();

            Core.Bootstrapper.LogConfiguration(container, settings, container.GetRequiredService<ILoggerFactory>());
            if (Settings.Current.EnableBootstrapStartupActions)
                container.RunStartupActionsAsync().GetAwaiter().GetResult();

            return container;
        }
    }
}
