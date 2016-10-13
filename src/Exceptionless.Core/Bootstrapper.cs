﻿using System;
using System.Collections.Generic;
using AutoMapper;
using Exceptionless.Core.Authentication;
using Exceptionless.Core.Billing;
using Exceptionless.Core.Dependency;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Geo;
using Exceptionless.Core.Jobs.WorkItemHandlers;
using Exceptionless.Core.Mail;
using Exceptionless.Core.Models;
using Exceptionless.Core.Models.WorkItems;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Plugins.EventProcessor;
using Exceptionless.Core.Plugins.Formatting;
using Exceptionless.Core.Plugins.WebHook;
using Exceptionless.Core.Queues.Models;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Repositories.Configuration;
using Exceptionless.Core.Utility;
using Exceptionless.Serializer;
using FluentValidation;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Messaging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Repositories.Elasticsearch.Jobs;
using Foundatio.Serializer;
using Foundatio.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RazorSharpEmail;
using SimpleInjector;

namespace Exceptionless.Core {
    public class Bootstrapper {
        public static void RegisterServices(Container container, ILoggerFactory loggerFactory) {
            container.RegisterLogger(loggerFactory);
            container.RegisterSingleton<IDependencyResolver>(() => new SimpleInjectorDependencyResolver(container));

            JsonConvert.DefaultSettings = () => new JsonSerializerSettings {
                DateParseHandling = DateParseHandling.DateTimeOffset
            };

            var contractResolver = new ExceptionlessContractResolver();
            contractResolver.UseDefaultResolverFor(typeof(DataDictionary), typeof(SettingsDictionary), typeof(VersionOne.VersionOneWebHookStack), typeof(VersionOne.VersionOneWebHookEvent));

            var settings = new JsonSerializerSettings {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                DateParseHandling = DateParseHandling.DateTimeOffset,
                ContractResolver = contractResolver
            };

            settings.AddModelConverters(loggerFactory.CreateLogger(nameof(Bootstrapper)));

            container.RegisterSingleton<IContractResolver>(() => contractResolver);
            container.RegisterSingleton<JsonSerializerSettings>(settings);
            container.RegisterSingleton<JsonSerializer>(JsonSerializer.Create(settings));
            container.RegisterSingleton<ISerializer>(() => new JsonNetSerializer(settings));

            container.RegisterSingleton<IMetricsClient>(() => new InMemoryMetricsClient(loggerFactory: loggerFactory));

            container.RegisterSingleton<ExceptionlessElasticConfiguration>();
            container.AddStartupAction(() => container.GetInstance<ExceptionlessElasticConfiguration>().ConfigureIndexesAsync(beginReindexingOutdated: false));

            container.RegisterSingleton<ICacheClient, InMemoryCacheClient>();

            container.RegisterSingleton<IEnumerable<IQueueBehavior<EventPost>>>(() => new[] { new MetricsQueueBehavior<EventPost>(container.GetInstance<IMetricsClient>()) });
            container.RegisterSingleton<IEnumerable<IQueueBehavior<EventUserDescription>>>(() => new[] { new MetricsQueueBehavior<EventUserDescription>(container.GetInstance<IMetricsClient>()) });
            container.RegisterSingleton<IEnumerable<IQueueBehavior<EventNotificationWorkItem>>>(() => new[] { new MetricsQueueBehavior<EventNotificationWorkItem>(container.GetInstance<IMetricsClient>()) });
            container.RegisterSingleton<IEnumerable<IQueueBehavior<WebHookNotification>>>(() => new[] { new MetricsQueueBehavior<WebHookNotification>(container.GetInstance<IMetricsClient>()) });
            container.RegisterSingleton<IEnumerable<IQueueBehavior<MailMessage>>>(() => new[] { new MetricsQueueBehavior<MailMessage>(container.GetInstance<IMetricsClient>()) });
            container.RegisterSingleton<IEnumerable<IQueueBehavior<WorkItemData>>>(() => new[] { new MetricsQueueBehavior<WorkItemData>(container.GetInstance<IMetricsClient>()) });

            container.RegisterSingleton<IQueue<EventPost>>(() => new InMemoryQueue<EventPost>(behaviors: container.GetAllInstances<IQueueBehavior<EventPost>>()));
            container.RegisterSingleton<IQueue<EventUserDescription>>(() => new InMemoryQueue<EventUserDescription>(behaviors: container.GetAllInstances<IQueueBehavior<EventUserDescription>>()));
            container.RegisterSingleton<IQueue<EventNotificationWorkItem>>(() => new InMemoryQueue<EventNotificationWorkItem>(behaviors: container.GetAllInstances<IQueueBehavior<EventNotificationWorkItem>>()));
            container.RegisterSingleton<IQueue<WebHookNotification>>(() => new InMemoryQueue<WebHookNotification>(behaviors: container.GetAllInstances<IQueueBehavior<WebHookNotification>>()));
            container.RegisterSingleton<IQueue<MailMessage>>(() => new InMemoryQueue<MailMessage>(behaviors: container.GetAllInstances<IQueueBehavior<MailMessage>>()));
            
            var workItemHandlers = new WorkItemHandlers();
            workItemHandlers.Register<ReindexWorkItem>(container.GetInstance<ReindexWorkItemHandler>);
            workItemHandlers.Register<RemoveOrganizationWorkItem>(container.GetInstance<RemoveOrganizationWorkItemHandler>);
            workItemHandlers.Register<RemoveProjectWorkItem>(container.GetInstance<RemoveProjectWorkItemHandler>);
            workItemHandlers.Register<SetLocationFromGeoWorkItem>(container.GetInstance<SetLocationFromGeoWorkItemHandler>);
            workItemHandlers.Register<SetProjectIsConfiguredWorkItem>(container.GetInstance<SetProjectIsConfiguredWorkItemHandler>);
            workItemHandlers.Register<StackWorkItem>(container.GetInstance<StackWorkItemHandler>);
            workItemHandlers.Register<ThrottleBotsWorkItem>(container.GetInstance<ThrottleBotsWorkItemHandler>);
            workItemHandlers.Register<OrganizationMaintenanceWorkItem>(container.GetInstance<OrganizationMaintenanceWorkItemHandler>);
            workItemHandlers.Register<OrganizationNotificationWorkItem>(container.GetInstance<OrganizationNotificationWorkItemHandler>);
            workItemHandlers.Register<ProjectMaintenanceWorkItem>(container.GetInstance<ProjectMaintenanceWorkItemHandler>);
            container.RegisterSingleton<WorkItemHandlers>(workItemHandlers);
            container.RegisterSingleton<IQueue<WorkItemData>>(() => new InMemoryQueue<WorkItemData>(behaviors: container.GetAllInstances<IQueueBehavior<WorkItemData>>(), workItemTimeout: TimeSpan.FromHours(1)));

            container.RegisterSingleton<IMessageBus, InMemoryMessageBus>();
            container.RegisterSingleton<IMessagePublisher>(container.GetInstance<IMessageBus>);
            container.RegisterSingleton<IMessageSubscriber>(container.GetInstance<IMessageBus>);

            if (!String.IsNullOrEmpty(Settings.Current.StorageFolder))
                container.RegisterSingleton<IFileStorage>(new FolderFileStorage(Settings.Current.StorageFolder));
            else
                container.RegisterSingleton<IFileStorage>(new InMemoryFileStorage());

            container.RegisterSingleton<IStackRepository, StackRepository>();
            container.RegisterSingleton<IEventRepository, EventRepository>();
            container.RegisterSingleton<IOrganizationRepository, OrganizationRepository>();
            container.RegisterSingleton<IProjectRepository, ProjectRepository>();
            container.RegisterSingleton<IUserRepository, UserRepository>();
            container.RegisterSingleton<IWebHookRepository, WebHookRepository>();
            container.RegisterSingleton<ITokenRepository, TokenRepository>();
            container.RegisterSingleton<IApplicationRepository, ApplicationRepository>();

            container.RegisterSingleton<IGeoIpService, MaxMindGeoIpService>();
            container.RegisterSingleton<IGeocodeService, NullGeocodeService>();

            container.Register(typeof(IValidator<>), new[] { typeof(Bootstrapper).Assembly }, Lifestyle.Singleton);

            container.RegisterSingleton<IEmailGenerator>(() => new RazorEmailGenerator(@"Mail\Templates"));
            container.RegisterSingleton<IMailer, Mailer>();
            if (Settings.Current.WebsiteMode != WebsiteMode.Dev)
                container.RegisterSingleton<IMailSender, SmtpMailSender>();
            else
                container.RegisterSingleton<IMailSender>(() => new InMemoryMailSender());

            container.RegisterSingleton<ILockProvider, CacheLockProvider>();
            container.Register<StripeEventHandler>();
            container.RegisterSingleton<BillingManager>();
            container.RegisterSingleton<SampleDataService>();
            container.RegisterSingleton<EventStats>();
            container.RegisterSingleton<EventPipeline>();
            container.RegisterSingleton<EventPluginManager>();
            container.RegisterSingleton<FormattingPluginManager>();
            container.RegisterSingleton<UserAgentParser>();

            container.RegisterSingleton<SystemHealthChecker>();

            container.RegisterSingleton<ICoreLastReferenceIdManager, NullCoreLastReferenceIdManager>();

            container.Register<IDomainLoginProvider, ActiveDirectoryLoginProvider>();
            
            container.RegisterSingleton<IMapper>(() => {
                var profiles = container.GetAllInstances<Profile>();
                var config = new MapperConfiguration(cfg => {
                    cfg.ConstructServicesUsing(container.GetInstance);

                    foreach (var profile in profiles)
                        cfg.AddProfile(profile);
                });

                return config.CreateMapper();
            });
        }
    }
}
