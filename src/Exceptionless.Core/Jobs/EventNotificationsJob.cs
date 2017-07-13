﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Mail;
using Exceptionless.Core.Queues.Models;
using Exceptionless.Core.Repositories;
using Exceptionless.DateTimeExtensions;
using Exceptionless.Core.Models;
using Exceptionless.Core.Models.WorkItems;
using Exceptionless.Core.Services;
using Exceptionless.Core.Utility;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Repositories;
using Foundatio.Utility;

#pragma warning disable 1998

namespace Exceptionless.Core.Jobs {
    [Job(Description = "Queues event notification emails.", InitialDelay = "5s")]
    public class EventNotificationsJob : QueueJobBase<EventNotificationWorkItem> {
        private readonly SlackService _slackService;
        private readonly IMailer _mailer;
        private readonly IProjectRepository _projectRepository;
        private readonly IUserRepository _userRepository;
        private readonly IEventRepository _eventRepository;
        private readonly ICacheClient _cache;
        private readonly UserAgentParser _parser;

        public EventNotificationsJob(IQueue<EventNotificationWorkItem> queue, SlackService slackService, IMailer mailer, IProjectRepository projectRepository, IUserRepository userRepository, IEventRepository eventRepository, ICacheClient cacheClient, UserAgentParser parser, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) {
            _slackService = slackService;
            _mailer = mailer;
            _projectRepository = projectRepository;
            _userRepository = userRepository;
            _eventRepository = eventRepository;
            _cache = cacheClient;
            _parser = parser;
        }

        protected override async Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<EventNotificationWorkItem> context) {
            var wi = context.QueueEntry.Value;
            var ev = await _eventRepository.GetByIdAsync(wi.EventId).AnyContext();
            if (ev == null || ev.IsDeleted)
                return JobResult.SuccessWithMessage($"Could not load event: {wi.EventId}");

            bool shouldLog = ev.ProjectId != Settings.Current.InternalProjectId;
            int sent = 0;
            _logger.Trace().Message(() => $"Process notification: project={ev.ProjectId} event={ev.Id} stack={ev.StackId}").WriteIf(shouldLog);

            var project = await _projectRepository.GetByIdAsync(ev.ProjectId, o => o.Cache()).AnyContext();
            if (project == null)
                return JobResult.SuccessWithMessage($"Could not load project: {ev.ProjectId}.");
            _logger.Trace().Message(() => $"Loaded project: name={project.Name}").WriteIf(shouldLog);

            // after the first 2 occurrences, don't send a notification for the same stack more then once every 30 minutes
            var lastTimeSentUtc = await _cache.GetAsync<DateTime>(String.Concat("notify:stack-throttle:", ev.StackId), DateTime.MinValue).AnyContext();
            if (wi.TotalOccurrences > 2
                && !wi.IsRegression
                && lastTimeSentUtc != DateTime.MinValue
                && lastTimeSentUtc > SystemClock.UtcNow.AddMinutes(-30)) {
                _logger.Info().Message("Skipping message because of stack throttling: last sent={0} occurrences={1}", lastTimeSentUtc, wi.TotalOccurrences).WriteIf(shouldLog);
                return JobResult.Success;
            }

            if (context.CancellationToken.IsCancellationRequested)
                return JobResult.Cancelled;

            // don't send more than 10 notifications for a given project every 30 minutes
            var projectTimeWindow = TimeSpan.FromMinutes(30);
            string cacheKey = String.Concat("notify:project-throttle:", ev.ProjectId, "-", SystemClock.UtcNow.Floor(projectTimeWindow).Ticks);
            double notificationCount = await _cache.IncrementAsync(cacheKey, 1, projectTimeWindow).AnyContext();
            if (notificationCount > 10 && !wi.IsRegression) {
                _logger.Info().Project(ev.ProjectId).Message("Skipping message because of project throttling: count={0}", notificationCount).WriteIf(shouldLog);
                return JobResult.Success;
            }

            foreach (var kv in project.NotificationSettings) {
                var settings = kv.Value;
                _logger.Trace().Message(() => $"Processing notification: {kv.Key}").WriteIf(shouldLog);

                bool isCritical = ev.IsCritical();
                bool shouldReportNewError = settings.ReportNewErrors && wi.IsNew && ev.IsError();
                bool shouldReportCriticalError = settings.ReportCriticalErrors && isCritical && ev.IsError();
                bool shouldReportRegression = settings.ReportEventRegressions && wi.IsRegression;
                bool shouldReportNewEvent = settings.ReportNewEvents && wi.IsNew;
                bool shouldReportCriticalEvent = settings.ReportCriticalEvents && isCritical;
                bool shouldReport = shouldReportNewError || shouldReportCriticalError || shouldReportRegression || shouldReportNewEvent || shouldReportCriticalEvent;

                _logger.Trace().Message(() => $"Settings: new error={settings.ReportNewErrors} critical error={settings.ReportCriticalErrors} regression={settings.ReportEventRegressions} new={settings.ReportNewEvents} critical={settings.ReportCriticalEvents}").WriteIf(shouldLog);
                _logger.Trace().Message(() => $"Should process: new error={shouldReportNewError} critical error={shouldReportCriticalError} regression={shouldReportRegression} new={shouldReportNewEvent} critical={shouldReportCriticalEvent}").WriteIf(shouldLog);

                var request = ev.GetRequestInfo();
                // check for known bots if the user has elected to not report them
                if (shouldReport && !String.IsNullOrEmpty(request?.UserAgent)) {
                    var botPatterns = project.Configuration.Settings.ContainsKey(SettingsDictionary.KnownKeys.UserAgentBotPatterns)
                        ? project.Configuration.Settings.GetStringCollection(SettingsDictionary.KnownKeys.UserAgentBotPatterns).ToList()
                        : new List<string>();

                    var info = await _parser.ParseAsync(request.UserAgent, ev.ProjectId).AnyContext();
                    if (info != null && info.Device.IsSpider || request.UserAgent.AnyWildcardMatches(botPatterns)) {
                        shouldReport = false;
                        _logger.Info().Message("Skipping because event is from a bot \"{0}\".", request.UserAgent).WriteIf(shouldLog);
                    }
                }

                if (!shouldReport)
                    continue;

                bool processed;
                switch (kv.Key) {
                    case Project.NotificationIntegrations.Slack:
                        processed = await _slackService.SendEventNoticeAsync(ev, project, wi.IsNew, wi.IsRegression, wi.TotalOccurrences).AnyContext();
                        break;
                    default:
                        processed = await SendEmailNotificationAsync(kv.Key, project, ev, wi, shouldLog).AnyContext();
                        break;
                }

                _logger.Trace().Message(() => $"Finished processing notification: {kv.Key}").WriteIf(shouldLog);
                if (processed)
                    sent++;
            }

            // if we sent any notifications, mark the last time a notification for this stack was sent.
            if (sent > 0) {
                await _cache.SetAsync(String.Concat("notify:stack-throttle:", ev.StackId), SystemClock.UtcNow, SystemClock.UtcNow.AddMinutes(15)).AnyContext();
                _logger.Info().Message("Notifications sent: event={0} stack={1} count={2}", ev.Id, ev.StackId, sent).WriteIf(shouldLog);
            }

            return JobResult.Success;
        }

        private async Task<bool> SendEmailNotificationAsync(string userId, Project project, PersistentEvent ev, EventNotificationWorkItem wi, bool shouldLog) {
            var user = await _userRepository.GetByIdAsync(userId, o => o.Cache()).AnyContext();
            if (String.IsNullOrEmpty(user?.EmailAddress)) {
                _logger.Error("Could not load user {0} or blank email address {1}.", userId, user?.EmailAddress ?? "");
                return false;
            }

            if (!user.IsEmailAddressVerified) {
                _logger.Info().Message("User {0} with email address {1} has not been verified.", user.Id, user.EmailAddress).WriteIf(shouldLog);
                return false;
            }

            if (!user.EmailNotificationsEnabled) {
                _logger.Info().Message("User {0} with email address {1} has email notifications disabled.", user.Id, user.EmailAddress).WriteIf(shouldLog);
                return false;
            }

            if (!user.OrganizationIds.Contains(project.OrganizationId)) {
                _logger.Error().Message("Unauthorized user: project={0} user={1} organization={2} event={3}", project.Id, userId, project.OrganizationId, ev.Id).Write();
                return false;
            }

            _logger.Trace().Message(() => $"Loaded user: email={user.EmailAddress}").WriteIf(shouldLog);

            // don't send notifications in non-production mode to email addresses that are not on the outbound email list.
            if (Settings.Current.WebsiteMode != WebsiteMode.Production && !Settings.Current.AllowedOutboundAddresses.Contains(v => user.EmailAddress.ToLowerInvariant().Contains(v))) {
                _logger.Info().Message("Skipping because email is not on the outbound list and not in production mode.").WriteIf(shouldLog);
                return false;
            }

            _logger.Trace("Sending email to {0}...", user.EmailAddress);
            return await _mailer.SendEventNoticeAsync(user, ev, project, wi.IsNew, wi.IsRegression, wi.TotalOccurrences).AnyContext();
        }
    }
}
