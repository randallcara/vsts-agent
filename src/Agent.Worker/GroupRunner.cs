using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Expressions;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Handlers;
using System.Threading;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(GroupRunner))]
    public interface IGroupRunner : IStep, IAgentService
    {
        Pipelines.GroupStep Group { get; set; }
        List<IStep> Steps { get; }
    }

    public sealed class GroupRunner : AgentService, IGroupRunner
    {
        private List<IStep> _steps = new List<IStep>();

        public Pipelines.GroupStep Group { get; set; }
        public Pipelines.ContainerReference Container => Group?.Container;
        public List<IStep> Steps => _steps;

        public INode Condition { get; set; }

        public bool ContinueOnError => Group?.ContinueOnError ?? default(bool);

        public string DisplayName => Group?.DisplayName;

        public bool Enabled => Group?.Enabled ?? default(bool);

        public IExecutionContext ExecutionContext { get; set; }

        public TimeSpan? Timeout => (Group?.TimeoutInMinutes ?? 0) > 0 ? (TimeSpan?)TimeSpan.FromMinutes(Group.TimeoutInMinutes) : null;

        public void InitializeStep(IExecutionContext jobExecutionContext, Dictionary<Guid, Variables> intraStepVariables = null)
        {
            ExecutionContext = jobExecutionContext.CreateChild(Group.Id, Group.DisplayName, Group.Name);

            foreach (var step in Steps)
            {
                if (step is ITaskRunner)
                {
                    ITaskRunner taskStep = step as ITaskRunner;
                    ArgUtil.NotNull(taskStep, taskStep.DisplayName);
                    if (taskStep.Stage == JobRunStage.PreScope)
                    {
                        taskStep.ExecutionContext = jobExecutionContext.CreateChild(Guid.NewGuid(), $"{DisplayName}::{StringUtil.Loc("PreGroup", taskStep.Task.DisplayName)}", taskStep.Task.Name, intraStepVariables[taskStep.Task.Id]);
                    }
                    else if (taskStep.Stage == JobRunStage.PostScope)
                    {
                        taskStep.ExecutionContext = jobExecutionContext.CreateChild(Guid.NewGuid(), $"{DisplayName}::{StringUtil.Loc("PostGroup", taskStep.Task.DisplayName)}", taskStep.Task.Name, intraStepVariables[taskStep.Task.Id]);
                    }
                    else
                    {
                        taskStep.ExecutionContext = jobExecutionContext.CreateChild(taskStep.Task.Id, $"{DisplayName}::{taskStep.Task.DisplayName}", taskStep.Task.Name, intraStepVariables[taskStep.Task.Id]);
                    }
                }
                else if (step is JobExtensionRunner)
                {
                    JobExtensionRunner extensionRunner = step as JobExtensionRunner;
                    extensionRunner.ExecutionContext = jobExecutionContext.CreateChild(Guid.NewGuid(), $"{DisplayName}::{extensionRunner.DisplayName}", extensionRunner.DisplayName);
                }
            }
        }

        public async Task RunAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Group, nameof(Group));
            ArgUtil.NotNull(Steps, nameof(Steps));

            // TaskResult:
            //  Abandoned (Server set this.)
            //  Canceled
            //  Failed
            //  Skipped
            //  Succeeded
            //  SucceededWithIssues
            CancellationTokenRegistration? jobCancelRegister = null;
            foreach (IStep step in Steps)
            {
                Trace.Info($"Processing step: DisplayName='{step.DisplayName}', ContinueOnError={step.ContinueOnError}, Enabled={step.Enabled}");
                ArgUtil.Equal(true, step.Enabled, nameof(step.Enabled));
                ArgUtil.NotNull(step.ExecutionContext, nameof(step.ExecutionContext));
                ArgUtil.NotNull(step.ExecutionContext.Variables, nameof(step.ExecutionContext.Variables));

                // Start.
                step.ExecutionContext.Start();

                // Variable expansion.
                List<string> expansionWarnings;
                step.ExecutionContext.Variables.RecalculateExpanded(out expansionWarnings);
                expansionWarnings?.ForEach(x => step.ExecutionContext.Warning(x));

                var expressionManager = HostContext.GetService<IExpressionManager>();
                try
                {
                    // Register job cancellation call back only if job cancellation token not been fire before each step run
                    if (!ExecutionContext.CancellationToken.IsCancellationRequested)
                    {
                        // Test the condition again. The job was canceled after the condition was originally evaluated.
                        jobCancelRegister = ExecutionContext.CancellationToken.Register(() =>
                        {
                            // mark group as cancelled
                            ExecutionContext.Result = TaskResult.Canceled;

                            step.ExecutionContext.Debug($"Re-evaluate condition on job cancellation for step: '{step.DisplayName}'.");
                            bool conditionReTestResult;
                            if (HostContext.AgentShutdownToken.IsCancellationRequested)
                            {
                                step.ExecutionContext.Debug($"Skip Re-evaluate condition on agent shutdown.");
                                conditionReTestResult = false;
                            }
                            else
                            {
                                try
                                {
                                    conditionReTestResult = expressionManager.Evaluate(step.ExecutionContext, step.Condition, hostTracingOnly: true);
                                }
                                catch (Exception ex)
                                {
                                    // Cancel the step since we get exception while re-evaluate step condition.
                                    Trace.Info("Caught exception from expression when re-test condition on job cancellation.");
                                    step.ExecutionContext.Error(ex);
                                    conditionReTestResult = false;
                                }
                            }

                            if (!conditionReTestResult)
                            {
                                // Cancel the step.
                                Trace.Info("Cancel current running step.");
                                step.ExecutionContext.CancelToken();
                            }
                        });
                    }
                    else
                    {
                        if (ExecutionContext.Result != TaskResult.Canceled)
                        {
                            // mark group as cancelled
                            ExecutionContext.Result = TaskResult.Canceled;
                        }
                    }

                    // Evaluate condition.
                    step.ExecutionContext.Debug($"Evaluating condition for step: '{step.DisplayName}'");
                    Exception conditionEvaluateError = null;
                    bool conditionResult;
                    if (HostContext.AgentShutdownToken.IsCancellationRequested)
                    {
                        step.ExecutionContext.Debug($"Skip evaluate condition on agent shutdown.");
                        conditionResult = false;
                    }
                    else
                    {
                        try
                        {
                            conditionResult = expressionManager.Evaluate(step.ExecutionContext, step.Condition);
                        }
                        catch (Exception ex)
                        {
                            Trace.Info("Caught exception from expression.");
                            Trace.Error(ex);
                            conditionResult = false;
                            conditionEvaluateError = ex;
                        }
                    }

                    // no evaluate error but condition is false
                    if (!conditionResult && conditionEvaluateError == null)
                    {
                        // Condition == false
                        Trace.Info("Skipping step due to condition evaluation.");
                        step.ExecutionContext.Complete(TaskResult.Skipped);
                        continue;
                    }

                    if (conditionEvaluateError != null)
                    {
                        // fail the step since there is an evaluate error.
                        step.ExecutionContext.Error(conditionEvaluateError);
                        step.ExecutionContext.Complete(TaskResult.Failed);
                    }
                    else
                    {
                        // Run the step.
                        await RunStepAsync(step, ExecutionContext.CancellationToken);
                    }
                }
                finally
                {
                    if (jobCancelRegister != null)
                    {
                        jobCancelRegister?.Dispose();
                        jobCancelRegister = null;
                    }
                }

                // Update the job result.
                if (step.ExecutionContext.Result == TaskResult.SucceededWithIssues ||
                    step.ExecutionContext.Result == TaskResult.Failed)
                {
                    Trace.Info($"Update group result with current step result '{step.ExecutionContext.Result}'.");
                    ExecutionContext.Result = TaskResultUtil.MergeTaskResults(ExecutionContext.Result, step.ExecutionContext.Result.Value);
                }
                else
                {
                    Trace.Info($"No need for updating group result with current step result '{step.ExecutionContext.Result}'.");
                }

                Trace.Info($"Current state: group state = '{ExecutionContext.Result}'");
            }

            // map group output variables
            if (Group.Outputs.Count > 0)
            {
                foreach (var output in Group.Outputs)
                {
                    ExecutionContext.Debug($"Mapping task output '{output.Value}' to group output '{output.Key}'.");
                    Variable taskOutput = ExecutionContext.Variables.GetRaw(output.Value);
                    ExecutionContext.SetVariable(output.Key, taskOutput.Value, taskOutput.Secret, true);
                }
            }
        }

        private async Task RunStepAsync(IStep step, CancellationToken jobCancellationToken)
        {
            // Start the step.
            Trace.Info("Starting the step.");
            step.ExecutionContext.Section(StringUtil.Loc("StepStarting", step.DisplayName));
            step.ExecutionContext.SetTimeout(timeout: step.Timeout);
            try
            {
                await step.RunAsync();
            }
            catch (OperationCanceledException ex)
            {
                if (step.ExecutionContext.CancellationToken.IsCancellationRequested &&
                    !jobCancellationToken.IsCancellationRequested)
                {
                    Trace.Error($"Caught timeout exception from step: {ex.Message}");
                    step.ExecutionContext.Error(StringUtil.Loc("StepTimedOut"));
                    step.ExecutionContext.Result = TaskResult.Failed;
                }
                else
                {
                    // Log the exception and cancel the step.
                    Trace.Error($"Caught cancellation exception from step: {ex}");
                    step.ExecutionContext.Error(ex);
                    step.ExecutionContext.Result = TaskResult.Canceled;
                }
            }
            catch (Exception ex)
            {
                // Log the error and fail the step.
                Trace.Error($"Caught exception from step: {ex}");
                step.ExecutionContext.Error(ex);
                step.ExecutionContext.Result = TaskResult.Failed;
            }

            // Wait till all async commands finish.
            foreach (var command in step.ExecutionContext.AsyncCommands ?? new List<IAsyncCommandContext>())
            {
                try
                {
                    // wait async command to finish.
                    await command.WaitAsync();
                }
                catch (OperationCanceledException ex)
                {
                    if (step.ExecutionContext.CancellationToken.IsCancellationRequested &&
                        !jobCancellationToken.IsCancellationRequested)
                    {
                        // Log the timeout error, set step result to failed if the current result is not canceled.
                        Trace.Error($"Caught timeout exception from async command {command.Name}: {ex}");
                        step.ExecutionContext.Error(StringUtil.Loc("StepTimedOut"));

                        // if the step already canceled, don't set it to failed.
                        step.ExecutionContext.CommandResult = TaskResultUtil.MergeTaskResults(step.ExecutionContext.CommandResult, TaskResult.Failed);
                    }
                    else
                    {
                        // log and save the OperationCanceledException, set step result to canceled if the current result is not failed.
                        Trace.Error($"Caught cancellation exception from async command {command.Name}: {ex}");
                        step.ExecutionContext.Error(ex);

                        // if the step already failed, don't set it to canceled.
                        step.ExecutionContext.CommandResult = TaskResultUtil.MergeTaskResults(step.ExecutionContext.CommandResult, TaskResult.Canceled);
                    }
                }
                catch (Exception ex)
                {
                    // Log the error, set step result to failed if the current result is not canceled.
                    Trace.Error($"Caught exception from async command {command.Name}: {ex}");
                    step.ExecutionContext.Error(ex);

                    // if the step already canceled, don't set it to failed.
                    step.ExecutionContext.CommandResult = TaskResultUtil.MergeTaskResults(step.ExecutionContext.CommandResult, TaskResult.Failed);
                }
            }

            // Merge executioncontext result with command result
            if (step.ExecutionContext.CommandResult != null)
            {
                step.ExecutionContext.Result = TaskResultUtil.MergeTaskResults(step.ExecutionContext.Result, step.ExecutionContext.CommandResult.Value);
            }

            // Fixup the step result if ContinueOnError.
            if (step.ExecutionContext.Result == TaskResult.Failed && step.ContinueOnError)
            {
                step.ExecutionContext.Result = TaskResult.SucceededWithIssues;
                Trace.Info($"Updated step result: {step.ExecutionContext.Result}");
            }
            else
            {
                Trace.Info($"Step result: {step.ExecutionContext.Result}");
            }

            // Complete the step context.
            step.ExecutionContext.Section(StringUtil.Loc("StepFinishing", step.DisplayName));
            step.ExecutionContext.Complete();
        }
    }
}
