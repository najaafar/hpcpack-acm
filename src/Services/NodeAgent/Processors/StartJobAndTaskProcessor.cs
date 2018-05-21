﻿namespace Microsoft.HpcAcm.Services.NodeAgent
{
    using Microsoft.Extensions.Logging;
    using Microsoft.HpcAcm.Services.Common;
    using Microsoft.HpcAcm.Common.Utilities;
    using Microsoft.HpcAcm.Common.Dto;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using T = System.Threading.Tasks;
    using System.Text;
    using Microsoft.WindowsAzure.Storage.Queue;
    using Newtonsoft.Json;
    using Microsoft.WindowsAzure.Storage.Table;

    public class StartJobAndTaskProcessor : JobTaskProcessor
    {
        private string nodePartitionKey;
        private string jobPartitionKey;
        private const int MaxRawResultLength = 4096;

        private CloudTable jobsTable;
        private CloudTable nodesTable;

        private TaskMonitor Monitor { get; }

        public StartJobAndTaskProcessor(TaskMonitor monitor, NodeCommunicator communicator) : base(communicator)
        {
            this.Monitor = monitor;
        }

        private async T.Task<bool> PersistTaskResult(string resultKey, object result, CancellationToken token)
        {
            if (!await this.jobsTable.InsertOrReplaceAsJsonAsync(this.jobPartitionKey, resultKey, result, token)) { return false; }
            this.Logger.LogInformation("Saved task result {0} to jobs table", resultKey);
            if (!await this.nodesTable.InsertOrReplaceAsJsonAsync(this.nodePartitionKey, resultKey, result, token)) { return false; }
            this.Logger.LogInformation("Saved task result {0} to nodes table", resultKey);
            return true;
        }


        public override async T.Task<bool> ProcessAsync(TaskEventMessage message, CancellationToken token)
        {
            this.jobsTable = this.Utilities.GetJobsTable();
            this.nodesTable = this.Utilities.GetNodesTable();
            var nodeName = this.ServerOptions.HostName;

            this.nodePartitionKey = this.Utilities.GetNodePartitionKey(nodeName);
            this.jobPartitionKey = this.Utilities.GetJobPartitionKey(message.JobType, message.JobId);
            var taskKey = this.Utilities.GetTaskKey(message.JobId, message.Id, message.RequeueCount);
            var task = await this.jobsTable.RetrieveAsync<Task>(this.jobPartitionKey, taskKey, token);
            var taskInfoKey = this.Utilities.GetTaskInfoKey(task.JobId, task.Id, task.RequeueCount);
            var taskResultKey = this.Utilities.GetTaskResultKey(task.JobId, task.Id, task.RequeueCount);
            var nodeTaskResultKey = this.Utilities.GetNodeTaskResultKey(nodeName, task.JobId, task.RequeueCount, task.Id);

            using (this.Logger.BeginScope("Do work {0} for Task {1} on node {2}", message.EventVerb, taskKey, nodeName))
            {
                var cmd = task.CommandLine;
                Logger.LogInformation("Executing command {0}", cmd);

                var job = await this.jobsTable.RetrieveAsync<Job>(jobPartitionKey, this.Utilities.JobEntryKey, token);
                if (job.State != JobState.Running)
                {
                    this.Logger.LogWarning("Trying to start a task {0} when {1} Job {2} is in state {3}", taskKey, job.Type, job.Id, job.State);
                    return true;
                }

                var taskResultBlob = await this.Utilities.CreateOrReplaceTaskOutputBlobAsync(task.JobType, taskResultKey, token);

                var rawResult = new StringBuilder();
                using (var monitor = string.IsNullOrEmpty(cmd) ? null : this.Monitor.StartMonitorTask(taskKey, async (output, cancellationToken) =>
                {
                    try
                    {
                        if (rawResult.Length < MaxRawResultLength)
                        {
                            rawResult.Append(output);
                        }

                        await taskResultBlob.AppendTextAsync(output, Encoding.UTF8, null, null, null, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        this.Logger.LogError(ex, "Error happened when append to blob {0}", taskResultBlob.Name);
                    }
                }))
                {
                    this.Logger.LogInformation("Call startjobandtask for task {0}", taskKey);

                    var taskInfo = await this.jobsTable.RetrieveAsync<TaskStartInfo>(jobPartitionKey, taskInfoKey, token);

                    taskInfo.StartInfo.stdout = $"{this.Communicator.Options.AgentUriBase}/output/{taskKey}";

                    try
                    {
                        await this.Communicator.StartJobAndTaskAsync(
                            nodeName,
                            new StartJobAndTaskArg(new int[0], taskInfo.JobId, taskInfo.Id), taskInfo.UserName, taskInfo.Password,
                            taskInfo.StartInfo, taskInfo.PrivateKey, taskInfo.PublicKey, token);
                    }
                    catch (Exception ex)
                    {
                        await this.Utilities.UpdateTaskAsync(jobPartitionKey, taskKey, t => t.State = TaskState.Failed, token);
                        await this.Utilities.UpdateJobAsync(task.JobType, task.JobId, j =>
                        {
                            (j.Events ?? (j.Events = new List<Event>())).Add(new Event()
                            {
                                Type = EventType.Warning,
                                Source = EventSource.Job,
                                Content = $"Task {task.Id} failed to dispatch, exception {ex}",
                            });
                        }, token);

                        return true;
                    }

                    await this.Utilities.UpdateTaskAsync(jobPartitionKey, taskKey, t => t.State = TaskState.Running, token);

                    this.Logger.LogInformation("Wait for response for job {0}, task {1}", task.JobId, taskKey);
                    if (monitor == null) return true;
                    var taskResultArgs = await monitor.Execution;

                    this.Logger.LogInformation("Saving result for job {0}, task {1}", task.JobId, taskKey);

                    var taskResult = taskResultArgs.TaskInfo;
                    await this.Utilities.UpdateTaskAsync(jobPartitionKey, taskKey,
                        t => t.State = taskResult?.ExitCode == 0 ? TaskState.Finished : TaskState.Failed,
                        token);

                    if (taskResultArgs.TaskInfo != null)
                    {
                        taskResult.Message = rawResult.Length > MaxRawResultLength ? rawResult.ToString(0, MaxRawResultLength) : rawResult.ToString();
                        taskResult.CommandLine = cmd;
                        taskResult.JobId = task.JobId;
                        taskResult.TaskId = task.Id;
                        taskResult.NodeName = nodeName;
                        taskResult.ResultKey = taskResultKey;

                        if (!await this.PersistTaskResult(nodeTaskResultKey, taskResult, token)) { return false; }
                        if (!await this.PersistTaskResult(taskResultKey, taskResult, token)) { return false; }
                    }

                    var queue = await this.Utilities.GetOrCreateTaskCompletionQueueAsync(token);
                    await queue.AddMessageAsync(new CloudQueueMessage(JsonConvert.SerializeObject(new TaskCompletionMessage()
                    {
                        JobId = task.JobId,
                        Id = task.Id,
                        ExitCode = taskResultArgs.TaskInfo?.ExitCode,
                        JobType = task.JobType,
                        RequeueCount = task.RequeueCount,
                    }, Formatting.Indented)), null, null, null, null, token);
                }

                return true;
            }
        }
    }
}