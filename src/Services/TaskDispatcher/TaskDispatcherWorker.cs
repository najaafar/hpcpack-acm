﻿namespace Microsoft.HpcAcm.Services.TaskDispatcher
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Microsoft.HpcAcm.Services.Common;
    using Microsoft.HpcAcm.Common.Dto;
    using System.Threading;
    using T = System.Threading.Tasks;
    using Serilog;
    using Microsoft.HpcAcm.Common.Utilities;
    using Microsoft.WindowsAzure.Storage.Table;
    using Microsoft.WindowsAzure.Storage.Queue;
    using Newtonsoft.Json;
    using System.Linq;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Options;
    using System.Diagnostics;
    using System.IO;

    internal class TaskDispatcherWorker : TaskItemWorker, IWorker
    {
        private readonly TaskDispatcherOptions options;

        public TaskDispatcherWorker(IOptions<TaskDispatcherOptions> options) : base(options.Value)
        {
            this.options = options.Value;
        }

        private CloudTable jobsTable;

        public override async T.Task InitializeAsync(CancellationToken token)
        {
            this.jobsTable = await this.Utilities.GetOrCreateJobsTableAsync(token);

            this.Source = new QueueTaskItemSource(
                await this.Utilities.GetOrCreateTaskCompletionQueueAsync(token),
                this.options);

            await base.InitializeAsync(token);
        }

        private async T.Task<bool> VisitAllTasksAsync(
            Job job,
            int taskId,
            Func<Task, T.Task> action,
            CancellationToken token)
        {
            var jobPartitionKey = this.Utilities.GetJobPartitionKey(job.Type, job.Id);
            var task = await this.jobsTable.RetrieveAsync<Task>(
                jobPartitionKey,
                this.Utilities.GetTaskKey(job.Id, taskId, job.RequeueCount),
                token);

            if (job.Type == JobType.Diagnostics &&
                (task.CustomizedData != InternalTask.EndTaskMark && task.CustomizedData != InternalTask.StartTaskMark))
            {
                var diagTest = await this.jobsTable.RetrieveAsync<InternalDiagnosticsTest>(
                    this.Utilities.GetDiagPartitionKey(job.DiagnosticTest.Category),
                    job.DiagnosticTest.Name,
                    token);

                if (diagTest.TaskResultFilterScript?.Name != null)
                {
                    var taskResultKey = this.Utilities.GetTaskResultKey(job.Id, task.Id, job.RequeueCount);
                    var taskResult = await this.jobsTable.RetrieveAsync<ComputeNodeTaskCompletionEventArgs>(jobPartitionKey, taskResultKey, token);
                    var scriptBlob = this.Utilities.GetBlob(diagTest.TaskResultFilterScript.ContainerName, diagTest.TaskResultFilterScript.Name);

                    var filteredResult = await PythonExecutor.ExecuteAsync(scriptBlob, new { Job = job, Task = taskResult }, token);
                    taskResult.TaskInfo.FilteredResult = filteredResult.Item1 == 0 ? filteredResult.Item2 : $"Task result filter script exit code {filteredResult.Item1}, message {filteredResult.Item3}";

                    await this.jobsTable.InsertOrReplaceAsync(jobPartitionKey, taskResultKey, taskResult, token);

                    if (!string.IsNullOrEmpty(filteredResult.Item2))
                    {
                        await this.Utilities.UpdateJobAsync(job.Type, job.Id, j =>
                        {
                            (j.Events ?? (j.Events = new List<Event>())).Add(new Event()
                            {
                                Content = filteredResult.Item2,
                                Source = EventSource.Job,
                                Type = EventType.Alert,
                            });

                            j.State = JobState.Failed;
                        }, token);

                        return true;
                    }
                }
            }

            foreach (var childId in task.ChildIds)
            {
                var childTaskKey = this.Utilities.GetTaskKey(job.Id, childId, job.RequeueCount);

                bool unlocked = false;
                bool isEndTask = false;
                Task childTask = null;
                if (!await this.Utilities.UpdateTaskAsync(jobPartitionKey, childTaskKey, t =>
                {
                    t.RemainingParentIds.Remove(taskId);
                    unlocked = (t.RemainingParentIds.Count == 0);
                    isEndTask = t.Id == int.MaxValue;
                    if (unlocked)
                    {
                        t.State = isEndTask ? TaskState.Finished : TaskState.Dispatching;
                    }

                    childTask = t;
                }, token))
                {
                    await this.Utilities.UpdateJobAsync(job.Type, job.Id, j =>
                    {
                        j.State = JobState.Failed;
                        // TODO: make event separate.
                        (j.Events ?? (j.Events = new List<Event>())).Add(new Event()
                        {
                            Content = $"Unable to update task record {childId}",
                            Source = EventSource.Job,
                            Type = EventType.Alert
                        });
                    }, token);
                    return false;
                }

                if (unlocked)
                {
                    if (isEndTask)
                    {
                        await this.Utilities.UpdateJobAsync(job.Type, job.Id, j => j.State = j.State == JobState.Running ? JobState.Finishing : j.State, token);
                        var jobEventQueue = await this.Utilities.GetOrCreateJobEventQueueAsync(token);
                        await jobEventQueue.AddMessageAsync(
                            new CloudQueueMessage(JsonConvert.SerializeObject(new JobEventMessage() { Id = job.Id, EventVerb = "finish", Type = job.Type })),
                            null, null, null, null,
                            token);
                    }
                    else
                    {
                        await action(childTask);
                    }
                }
            }

            return true;
        }

        public override async T.Task<bool> ProcessTaskItemAsync(TaskItem taskItem, CancellationToken token)
        {
            var message = taskItem.GetMessage<TaskCompletionMessage>();
            this.Logger.Information("Do work for TaskCompletionMessage {0}", message.Id);

            this.Logger.Information("{0} Job {1} task {2} requeuecount {3} completed with exit code {4}", message.JobType, message.JobId, message.Id, message.RequeueCount, message.ExitCode);
            var jobPartitionKey = this.Utilities.GetJobPartitionKey(message.JobType, message.JobId);

            var job = await this.jobsTable.RetrieveAsync<Job>(jobPartitionKey, this.Utilities.JobEntryKey, token);
            if (job == null || job.RequeueCount != message.RequeueCount)
            {
                this.Logger.Warning("Skip processing the task completion of {0}. Job is null = {1}, job.RequeueCount = {2}, message.RequeueCount = {3}", message.Id, job == null, job?.RequeueCount, message.RequeueCount);
                return true;
            }

            if (message.Id != 0 && message.Id != int.MaxValue)
            {
                await this.Utilities.UpdateJobAsync(message.JobType, message.JobId, j =>
                {
                    j.CompletedTaskCount = Math.Min(++j.CompletedTaskCount, j.TaskCount);
                }, token);
            }

            if (job.State == JobState.Running)
            {
                bool result = await this.VisitAllTasksAsync(job, message.Id, async t =>
                {
                    var queue = await this.Utilities.GetOrCreateNodeDispatchQueueAsync(t.Node, token);
                    await queue.AddMessageAsync(
                        new CloudQueueMessage(JsonConvert.SerializeObject(new TaskEventMessage() { EventVerb = "start", Id = t.Id, JobId = t.JobId, JobType = t.JobType, RequeueCount = t.RequeueCount }, Formatting.Indented)),
                        null,
                        null,
                        null,
                        null,
                        token);
                    this.Logger.Information("Dispatched job {0} task {1} to node {2}", t.JobId, t.Id, t.Node);
                }, token);

                return result;
            }

            return true;
        }
    }
}
