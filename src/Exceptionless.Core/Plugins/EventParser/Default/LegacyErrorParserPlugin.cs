﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Plugins.EventUpgrader;
using Exceptionless.Core.Models;
using Foundatio.Logging;
using Newtonsoft.Json;

namespace Exceptionless.Core.Plugins.EventParser {
    [Priority(10)]
    public class LegacyErrorParserPlugin : PluginBase, IEventParserPlugin {
        private readonly EventUpgraderPluginManager _manager;
        private readonly JsonSerializerSettings _settings;

        public LegacyErrorParserPlugin(EventUpgraderPluginManager manager, JsonSerializerSettings settings, ILoggerFactory loggerFactory) : base(loggerFactory) {
            _manager = manager;
            _settings = settings;
        }

        public async Task<List<PersistentEvent>> ParseEventsAsync(string input, int apiVersion, string userAgent) {
            if (apiVersion != 1)
                return null;

            try {
                var ctx = new EventUpgraderContext(input);
                await _manager.UpgradeAsync(ctx).AnyContext();

                return ctx.Documents.FromJson<PersistentEvent>(_settings);
            } catch (Exception ex) {
                _logger.Error(ex, "Error parsing event: {0}", ex.Message);
                return null;
            }
        }
    }
}