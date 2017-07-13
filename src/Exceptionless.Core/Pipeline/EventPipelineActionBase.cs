﻿using System;
using Exceptionless.Core.Plugins.EventProcessor;
using Foundatio.Logging;

namespace Exceptionless.Core.Pipeline {
    public abstract class EventPipelineActionBase : PipelineActionBase<EventContext> {
        public EventPipelineActionBase(ILoggerFactory loggerFactory = null) : base(loggerFactory) {}

        protected virtual bool IsCritical => false;
        protected virtual string[] ErrorTags => new string[0];
        protected virtual string ErrorMessage => null;

        public override bool HandleError(Exception ex, EventContext ctx) {
            string message = ErrorMessage ?? $"Error processing action: {GetType().Name} Message: {ex.Message}";
            _logger.Error()
                .Project(ctx.Event.ProjectId)
                .Message(message)
                .Exception(ex)
                .Property("Event", new { ctx.Event.Date, ctx.Event.StackId, ctx.Event.Type, ctx.Event.Source, ctx.Event.Message, ctx.Event.Value, ctx.Event.Geo, ctx.Event.ReferenceId, ctx.Event.Tags })
                .Tag(ErrorTags)
                .Critical(IsCritical)
                .Write();

            return ContinueOnError;
        }
    }
}