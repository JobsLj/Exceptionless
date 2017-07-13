﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless.Core.Repositories.Configuration;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Repositories.Elasticsearch.Jobs;

namespace Exceptionless.Core.Jobs.Elastic {
    [Job(Description = "Takes an Elasticsearch organizations index snapshot ", IsContinuous = false)]
    public class OrganizationSnapshotJob : SnapshotJob {
        public OrganizationSnapshotJob(ExceptionlessElasticConfiguration configuration, ILockProvider lockProvider, ILoggerFactory loggerFactory) : base(configuration.Client, lockProvider, loggerFactory) {
            Repository = Settings.Current.AppScopePrefix + "ex_organizations";
            IncludedIndexes.Add("organizations*");
        }

        public override Task<JobResult> RunAsync(CancellationToken cancellationToken = new CancellationToken()) {
            if (Settings.Current.DisableSnapshotJobs)
                return Task.FromResult(JobResult.Success);

            return base.RunAsync(cancellationToken);
        }
    }
}