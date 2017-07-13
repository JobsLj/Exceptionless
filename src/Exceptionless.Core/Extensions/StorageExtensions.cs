﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless.Core.Queues.Models;
using Foundatio.Logging;
using Foundatio.Storage;

namespace Exceptionless.Core.Extensions {
    public static class StorageExtensions {
        public static async Task<EventPostInfo> GetEventPostAsync(this IFileStorage storage, string path, ILogger logger, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                return null;

            EventPostInfo eventPostInfo;
            try {
                eventPostInfo = await storage.GetObjectAsync<EventPostInfo>(path, cancellationToken).AnyContext();
            } catch (Exception ex) {
                logger.Error(ex, "Error retrieving event post data \"{0}\".", path);
                return null;
            }

            return eventPostInfo;
        }

        public static async Task<bool> CompleteEventPostAsync(this IFileStorage storage, string path, string projectId, DateTime created, ILogger logger, bool shouldArchive = true) {
            if (String.IsNullOrEmpty(path))
                return false;

            // don't move files that are already in the archive
            if (path.StartsWith("archive"))
                return true;

            try {
                if (shouldArchive) {
                    string archivePath = $"archive\\{created:yy\\\\MM\\\\dd\\\\HH}\\{projectId}\\{Path.GetFileName(path)}";
                    return await storage.RenameFileAsync(path, archivePath).AnyContext();
                }

                return await storage.DeleteFileAsync(path).AnyContext();
            } catch (Exception ex) {
                logger.Error(ex, "Error archiving event post data \"{0}\".", path);
                return false;
            }
        }
    }
}
