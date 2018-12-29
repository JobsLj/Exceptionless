﻿using System;
using System.Linq;
using App.Metrics;
using App.Metrics.Infrastructure;
using App.Metrics.Internal.Infrastructure;
using App.Metrics.Reporting.Graphite;
using App.Metrics.Reporting.Http;
using App.Metrics.Reporting.InfluxDB;
using Exceptionless.Core;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Geo;
using Exceptionless.Core.Mail;
using Exceptionless.Core.Queues.Models;
using Exceptionless.Core.Utility;
using Exceptionless.Insulation.Geo;
using Exceptionless.Insulation.Mail;
using Exceptionless.Insulation.Metrics;
using Exceptionless.Insulation.Redis;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog.Sinks.Exceptionless;
using StackExchange.Redis;

namespace Exceptionless.Insulation {
    public class Bootstrapper {
        public static void RegisterServices(IServiceCollection container, bool runMaintenanceTasks) {
            if (!String.IsNullOrEmpty(Settings.Current.ExceptionlessApiKey) && !String.IsNullOrEmpty(Settings.Current.ExceptionlessServerUrl)) {
                var client = ExceptionlessClient.Default;
                client.Configuration.ServerUrl = Settings.Current.ExceptionlessServerUrl;
                client.Configuration.ApiKey = Settings.Current.ExceptionlessApiKey;

                client.Configuration.SetDefaultMinLogLevel(Logging.LogLevel.Warn);
                client.Configuration.UseLogger(new SelfLogLogger());
                client.Configuration.SetVersion(Settings.Current.Version);
                if (String.IsNullOrEmpty(Settings.Current.InternalProjectId))
                    client.Configuration.Enabled = false;

                client.Configuration.UseInMemoryStorage();
                client.Configuration.UseReferenceIds();

                container.ReplaceSingleton<ICoreLastReferenceIdManager, ExceptionlessClientCoreLastReferenceIdManager>();
                container.AddSingleton<ExceptionlessClient>(client);
            }

            if (!String.IsNullOrEmpty(Settings.Current.GoogleGeocodingApiKey))
                container.ReplaceSingleton<IGeocodeService>(s => new GoogleGeocodeService(Settings.Current.GoogleGeocodingApiKey));

            RegisterMetricsReporting(container);

            if (Settings.Current.AppMode != AppMode.Development)
                container.ReplaceSingleton<IMailSender, MailKitMailSender>();

            if (!String.IsNullOrEmpty(Settings.Current.RedisConnectionString)) {
                container.AddSingleton<ConnectionMultiplexer>(s => ConnectionMultiplexer.Connect(Settings.Current.RedisConnectionString));

                if (Settings.Current.HasAppScope)
                    container.ReplaceSingleton<ICacheClient>(s => new ScopedCacheClient(CreateRedisCacheClient(s), Settings.Current.AppScope));
                else
                    container.ReplaceSingleton<ICacheClient>(CreateRedisCacheClient);

                container.ReplaceSingleton<IConnectionMapping, RedisConnectionMapping>();
                container.ReplaceSingleton<IMessageBus>(s => new RedisMessageBus(new RedisMessageBusOptions {
                    Subscriber = s.GetRequiredService<ConnectionMultiplexer>().GetSubscriber(),
                    Topic = $"{Settings.Current.AppScopePrefix}messages",
                    Serializer = s.GetRequiredService<ISerializer>(),
                    LoggerFactory = s.GetRequiredService<ILoggerFactory>()
                }));
            }

            if (!String.IsNullOrEmpty(Settings.Current.AzureStorageQueueConnectionString)) {
                container.ReplaceSingleton(s => CreateAzureStorageQueue<EventPost>(s, retries: 1));
                container.ReplaceSingleton(s => CreateAzureStorageQueue<EventUserDescription>(s));
                container.ReplaceSingleton(s => CreateAzureStorageQueue<EventNotificationWorkItem>(s));
                container.ReplaceSingleton(s => CreateAzureStorageQueue<WebHookNotification>(s));
                container.ReplaceSingleton(s => CreateAzureStorageQueue<MailMessage>(s));
                container.ReplaceSingleton(s => CreateAzureStorageQueue<WorkItemData>(s, workItemTimeout: TimeSpan.FromHours(1)));
            } else if (!String.IsNullOrEmpty(Settings.Current.RedisConnectionString)) {
                container.ReplaceSingleton(s => CreateRedisQueue<EventPost>(s, runMaintenanceTasks, retries: 1));
                container.ReplaceSingleton(s => CreateRedisQueue<EventUserDescription>(s, runMaintenanceTasks));
                container.ReplaceSingleton(s => CreateRedisQueue<EventNotificationWorkItem>(s, runMaintenanceTasks));
                container.ReplaceSingleton(s => CreateRedisQueue<WebHookNotification>(s, runMaintenanceTasks));
                container.ReplaceSingleton(s => CreateRedisQueue<MailMessage>(s, runMaintenanceTasks));
                container.ReplaceSingleton(s => CreateRedisQueue<WorkItemData>(s, runMaintenanceTasks, workItemTimeout: TimeSpan.FromHours(1)));
            }

            if (!String.IsNullOrEmpty(Settings.Current.AzureStorageConnectionString)) {
                container.ReplaceSingleton<IFileStorage>(s => new AzureFileStorage(new AzureFileStorageOptions {
                    ConnectionString = Settings.Current.AzureStorageConnectionString,
                    ContainerName =  $"{Settings.Current.AppScopePrefix}ex-events",
                    Serializer = s.GetRequiredService<ITextSerializer>(),
                    LoggerFactory = s.GetRequiredService<ILoggerFactory>()
                }));
            } else if (!String.IsNullOrEmpty(Settings.Current.AliyunStorageConnectionString)) {
                container.ReplaceSingleton<IFileStorage>(s => new AliyunFileStorage(new AliyunFileStorageOptions {
                    ConnectionString = Settings.Current.AliyunStorageConnectionString,
                    Serializer = s.GetRequiredService<ITextSerializer>(),
                    LoggerFactory = s.GetRequiredService<ILoggerFactory>()
                }));
            } // else if (!String.IsNullOrEmpty(Settings.Current.MinioStorageConnectionString)) {
            //    container.ReplaceSingleton<IFileStorage>(s => new MinioFileStorage(new MinioFileStorageOptions {
            //        ConnectionString = Settings.Current.MinioStorageConnectionString,
            //        Serializer = s.GetRequiredService<ITextSerializer>(),
            //        LoggerFactory = s.GetRequiredService<ILoggerFactory>()
            //    }));
            //}
        }

        private static IMetricsRoot BuildAppMetrics(IMetricsConnectionString connectionString) {
            var metricsBuilder = AppMetrics.CreateDefaultBuilder();
            switch (connectionString) {
                case InfuxDBMetricsConnectionString influxConnectionString:
                    metricsBuilder.Report.ToInfluxDb(new MetricsReportingInfluxDbOptions {
                        InfluxDb = {
                            BaseUri = new Uri(influxConnectionString.ServerUrl),
                            UserName = influxConnectionString.UserName,
                            Password = influxConnectionString.Password,
                            Database = influxConnectionString.Database
                        }
                    });
                    break;
                case HttpMetricsConnectionString httpConnectionString:
                    metricsBuilder.Report.OverHttp(new MetricsReportingHttpOptions {
                        HttpSettings = {
                            RequestUri = new Uri(httpConnectionString.ServerUrl),
                            UserName = httpConnectionString.UserName,
                            Password = httpConnectionString.Password
                        }
                    });
                    break;
                case GraphiteMetricsConnectionString graphiteConnectionString:
                    metricsBuilder.Report.ToGraphite(new MetricsReportingGraphiteOptions {
                        Graphite = {
                            BaseUri = new Uri(graphiteConnectionString.ServerUrl)
                        }
                    });
                    break;
                default:
                    return null;
            }
            return metricsBuilder.Build();
        }

        private static void RegisterMetricsReporting(IServiceCollection container) {
            var connectionString = Settings.Current.MetricsConnectionString;
            if (connectionString is StatsDMetricsConnectionString statsdConnectionString) {
                container.ReplaceSingleton<IMetricsClient>(s => new StatsDMetricsClient(new StatsDMetricsClientOptions {
                    ServerName = statsdConnectionString.ServerName,
                    Port = statsdConnectionString.ServerPort,
                    Prefix = "ex",
                    LoggerFactory = s.GetRequiredService<ILoggerFactory>()
                }));
            } else if (connectionString != null) {
                var metrics = BuildAppMetrics(connectionString);
                if (metrics != null) {
                    container.ReplaceSingleton(metrics.Clock);
                    container.ReplaceSingleton(metrics.Filter);
                    container.ReplaceSingleton(metrics.DefaultOutputMetricsFormatter);
                    container.ReplaceSingleton(metrics.OutputMetricsFormatters);
                    container.ReplaceSingleton(metrics.DefaultOutputEnvFormatter);
                    container.ReplaceSingleton(metrics.OutputEnvFormatters);
                    container.TryAddSingleton<EnvironmentInfoProvider>();
                    container.ReplaceSingleton<IMetrics>(metrics);
                    container.ReplaceSingleton(metrics);
                    container.ReplaceSingleton(metrics.Options);
                    container.ReplaceSingleton(metrics.Reporters);
                    container.ReplaceSingleton(metrics.ReportRunner);
                    container.TryAddSingleton<AppMetricsMarkerService, AppMetricsMarkerService>();
                    container.ReplaceSingleton<IMetricsClient, AppMetricsClient>();
                }
            }
        }

        private static IQueue<T> CreateAzureStorageQueue<T>(IServiceProvider container, int retries = 2, TimeSpan? workItemTimeout = null) where T : class {
            return new AzureStorageQueue<T>(new AzureStorageQueueOptions<T> {
                ConnectionString = Settings.Current.AzureStorageQueueConnectionString,
                Name = GetQueueName<T>().ToLowerInvariant(),
                Retries = retries,
                Behaviors = container.GetServices<IQueueBehavior<T>>().ToList(),
                WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5.0)),
                Serializer = container.GetRequiredService<ISerializer>(),
                LoggerFactory = container.GetRequiredService<ILoggerFactory>()
            });
        }

        private static IQueue<T> CreateRedisQueue<T>(IServiceProvider container, bool runMaintenanceTasks, int retries = 2, TimeSpan? workItemTimeout = null) where T : class {
            return new RedisQueue<T>(new RedisQueueOptions<T> {
                ConnectionMultiplexer = container.GetRequiredService<ConnectionMultiplexer>(),
                Name = GetQueueName<T>(),
                Retries = retries,
                Behaviors = container.GetServices<IQueueBehavior<T>>().ToList(),
                WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5.0)),
                RunMaintenanceTasks = runMaintenanceTasks,
                Serializer = container.GetRequiredService<ISerializer>(),
                LoggerFactory = container.GetRequiredService<ILoggerFactory>()
            });
        }

        private static string GetQueueName<T>() {
            return String.Concat(Settings.Current.QueueScopePrefix, typeof(T).Name);
        }

        private static RedisCacheClient CreateRedisCacheClient(IServiceProvider container) {
            return new RedisCacheClient(new RedisCacheClientOptions {
                ConnectionMultiplexer = container.GetRequiredService<ConnectionMultiplexer>(),
                Serializer = container.GetRequiredService<ISerializer>(),
                LoggerFactory = container.GetRequiredService<ILoggerFactory>()
            });
        }
    }
}