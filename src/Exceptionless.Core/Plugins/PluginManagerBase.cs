﻿using System;
using System.Collections.Generic;
using System.Linq;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Dependency;
using Exceptionless.Core.Helpers;
using Foundatio.Logging;
using Foundatio.Metrics;

namespace Exceptionless.Core.Plugins {
    public abstract class PluginManagerBase<TPlugin> where TPlugin : class, IPlugin {
        protected readonly IDependencyResolver _dependencyResolver;
        protected readonly string _metricPrefix;
        protected readonly IMetricsClient _metricsClient;
        protected readonly ILogger _logger;

        public PluginManagerBase(IDependencyResolver dependencyResolver = null, IMetricsClient metricsClient = null, ILoggerFactory loggerFactory = null) {
            var type = GetType();
            _metricPrefix = String.Concat(type.Name.ToLower(), ".");
            _metricsClient = metricsClient ?? new InMemoryMetricsClient(new InMemoryMetricsClientOptions { LoggerFactory = loggerFactory });
            _logger = loggerFactory.CreateLogger(type);

            _dependencyResolver = dependencyResolver ?? new DefaultDependencyResolver();

            Plugins = new SortedList<int, TPlugin>();
            LoadDefaultPlugins();
        }

        public SortedList<int, TPlugin> Plugins { get; private set; }

        public void AddPlugin(Type pluginType) {
            var attr = pluginType.GetCustomAttributes(typeof(PriorityAttribute), true).FirstOrDefault() as PriorityAttribute;
            int priority = attr?.Priority ?? 0;

            var plugin = (TPlugin)_dependencyResolver.GetService(pluginType);
            Plugins.Add(priority, plugin);
        }

        private void LoadDefaultPlugins() {
            var pluginTypes = TypeHelper.GetDerivedTypes<TPlugin>(new[] { typeof(Bootstrapper).Assembly });

            foreach (var type in pluginTypes) {
                if (Settings.Current.DisabledPlugins.Contains(type.Name, StringComparer.InvariantCultureIgnoreCase)) {
                    _logger.Warn(() => $"Plugin {type.Name} is currently disabled and won't be executed.");
                    continue;
                }

                try {
                    AddPlugin(type);
                } catch (Exception ex) {
                    _logger.Error(ex, "Unable to instantiate plugin of type \"{0}\": {1}", type.FullName, ex.Message);
                    throw;
                }
            }
        }
    }
}