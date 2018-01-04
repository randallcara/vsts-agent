using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Expressions;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public interface IJobExtension : IExtension
    {
        HostTypes HostType { get; }
        Task<List<IStep>> InitializeJob(IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message);
        string GetRootedPath(IExecutionContext context, string path);
        void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath);
    }

    public sealed class JobInitializeResult
    {
        private List<IStep> _preJobSteps = new List<IStep>();
        private List<IStep> _jobSteps = new List<IStep>();
        private List<IStep> _postJobSteps = new List<IStep>();

        public List<IStep> PreJobSteps => _preJobSteps;
        public List<IStep> JobSteps => _jobSteps;
        public List<IStep> PostJobStep => _postJobSteps;
    }

    public abstract class JobExtension : AgentService, IJobExtension
    {
        private Dictionary<Guid, Variables> _intraTaskVariablesMapping = new Dictionary<Guid, Variables>();

        public abstract HostTypes HostType { get; }

        public abstract Type ExtensionType { get; }

        // Anything job extension want to do before building the steps list. This will be deprecated when GetSource move to a task.
        public abstract void InitializeJobExtension(IExecutionContext context);

        // Anything job extension want to add to pre-job steps list. This will be deprecated when GetSource move to a task.
        public abstract IStep GetExtensionPreJobStep(IExecutionContext context);

        // Anything job extension want to add to post-job steps list. This will be deprecated when GetSource move to a task.
        public abstract IStep GetExtensionPostJobStep(IExecutionContext context);

        // public abstract IStep GetExtensionStep(Guid stepId, Dictionary<string, string> data);

        public abstract string GetRootedPath(IExecutionContext context, string path);

        public abstract void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath);

        // download all required tasks.
        // make sure all task's condition inputs are valid.
        // build up a list of steps for jobrunner.
        public async Task<List<IStep>> InitializeJob(IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message)
        {
            Trace.Entering();
            ArgUtil.NotNull(jobContext, nameof(jobContext));
            ArgUtil.NotNull(message, nameof(message));

            // create a new timeline record node for 'Initialize job'
            IExecutionContext context = jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("InitializeJob"), nameof(JobExtension));

            List<IStep> jobSteps = new List<IStep>();
            using (var register = jobContext.CancellationToken.Register(() => { context.CancelToken(); }))
            {
                try
                {
                    context.Start();
                    context.Section(StringUtil.Loc("StepStarting", StringUtil.Loc("InitializeJob")));

                    // Give job extension a chance to initialize
                    Trace.Info($"Run initial step from extension {this.GetType().Name}.");
                    InitializeJobExtension(context);

                    // Download tasks if not already in the cache
                    Trace.Info("Downloading task definitions.");
                    var taskManager = HostContext.GetService<ITaskManager>();
                    await taskManager.DownloadAsync(context, message.Steps);

                    // In order to create a flat list of timeline record for each step,
                    // we need expand all GroupStep/WrapperTask/ContainerStep first,
                    // then assign ExecutionContext for them.
                    jobSteps = BuildJobSteps(context, message.Steps);

                    // create task execution context for all job steps
                    foreach (var step in jobSteps)
                    {
                        ArgUtil.NotNull(step, step.DisplayName);
                        step.InitializeStep(jobContext, _intraTaskVariablesMapping);
                    }

                    return jobSteps;
                }
                catch (OperationCanceledException ex) when (jobContext.CancellationToken.IsCancellationRequested)
                {
                    // Log the exception and cancel the JobExtension Initialization.
                    Trace.Error($"Caught cancellation exception from JobExtension Initialization: {ex}");
                    context.Error(ex);
                    context.Result = TaskResult.Canceled;
                    throw;
                }
                catch (Exception ex)
                {
                    // Log the error and fail the JobExtension Initialization.
                    Trace.Error($"Caught exception from JobExtension Initialization: {ex}");
                    context.Error(ex);
                    context.Result = TaskResult.Failed;
                    throw;
                }
                finally
                {
                    context.Section(StringUtil.Loc("StepFinishing", StringUtil.Loc("InitializeJob")));
                    context.Complete();
                }
            }
        }

        private List<IStep> BuildJobSteps(IExecutionContext context, IList<Pipelines.JobStep> steps)
        {
            StepsBuilder jobStepsBuilder = new StepsBuilder();

            // Parse all steps' condition, throw exception if condition contains invalid syntax.
            Trace.Info("Parsing all steps' condition inputs.");
            var expression = HostContext.GetService<IExpressionManager>();
            Dictionary<Guid, INode> stepConditionMap = new Dictionary<Guid, INode>();
            foreach (var step in steps)
            {
                INode condition;
                if (!string.IsNullOrEmpty(step.Condition))
                {
                    context.Debug($"{step.Type.ToString()} step '{step.DisplayName}' has following condition: '{step.Condition}'.");
                    condition = expression.Parse(context, step.Condition);
                }
                else
                {
                    condition = ExpressionManager.Succeeded;
                }

                stepConditionMap[step.Id] = condition;

                // expand tasks' condition within a GroupStep.
                if (step.Type == Pipelines.StepType.Group)
                {
                    foreach (var task in (step as Pipelines.GroupStep).Steps)
                    {
                        if (!string.IsNullOrEmpty(task.Condition))
                        {
                            context.Debug($"Group '{step.DisplayName}': step '{task.DisplayName}' has following condition: '{task.Condition}'.");
                            condition = expression.Parse(context, task.Condition);
                        }
                        else
                        {
                            condition = ExpressionManager.Succeeded;
                        }

                        stepConditionMap[task.Id] = condition;
                    }
                }
            }

#if OS_WINDOWS
            // This is for internal testing and is not publicly supported. This will be removed from the agent at a later time.
            var prepareScript = Environment.GetEnvironmentVariable("VSTS_AGENT_INIT_INTERNAL_TEMP_HACK");
            if (!string.IsNullOrEmpty(prepareScript))
            {
                var prepareStep = new ManagementScriptStep(
                    scriptPath: prepareScript,
                    condition: ExpressionManager.Succeeded,
                    displayName: "Agent Initialization");

                Trace.Verbose($"Adding agent init script step.");
                prepareStep.Initialize(HostContext);
                ServiceEndpoint systemConnection = context.Endpoints.Single(x => string.Equals(x.Name, ServiceEndpoints.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
                prepareStep.AccessToken = systemConnection.Authorization.Parameters["AccessToken"];
                jobStepsBuilder.AddPreStep(prepareStep);
            }

            // Add script post steps.
            // This is for internal testing and is not publicly supported. This will be removed from the agent at a later time.
            var finallyScript = Environment.GetEnvironmentVariable("VSTS_AGENT_CLEANUP_INTERNAL_TEMP_HACK");
            if (!string.IsNullOrEmpty(finallyScript))
            {
                var finallyStep = new ManagementScriptStep(
                    scriptPath: finallyScript,
                    condition: ExpressionManager.Always,
                    displayName: "Agent Cleanup");

                Trace.Verbose($"Adding agent cleanup script step.");
                finallyStep.Initialize(HostContext);
                ServiceEndpoint systemConnection = context.Endpoints.Single(x => string.Equals(x.Name, ServiceEndpoints.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
                finallyStep.AccessToken = systemConnection.Authorization.Parameters["AccessToken"];
                jobStepsBuilder.AddPostStep(finallyStep);
            }
#endif

            // Build up a basic list of steps for the job, expand group step, expand warpper task
            var taskManager = HostContext.GetService<ITaskManager>();
            foreach (var step in steps)
            {
                if (step.Type == Pipelines.StepType.Task)
                {
                    var task = step as Pipelines.TaskStep;
                    var taskLoadResult = taskManager.Load(context, task);
                    List<string> warnings;
                    _intraTaskVariablesMapping[step.Id] = new Variables(HostContext, new Dictionary<string, VariableValue>(), out warnings);

                    // Add pre-job steps from Tasks
                    if (taskLoadResult.PreScopeStep != null)
                    {
                        Trace.Info($"Adding Pre-Job task step {step.DisplayName}.");
                        jobStepsBuilder.AddPreStep(taskLoadResult.PreScopeStep);
                    }

                    // Add execution steps from Tasks
                    if (taskLoadResult.MainScopeStep != null)
                    {
                        Trace.Verbose($"Adding task step {step.DisplayName}.");
                        jobStepsBuilder.AddMainStep(taskLoadResult.MainScopeStep);
                    }

                    // Add post-job steps from Tasks
                    if (taskLoadResult.PostScopeStep != null)
                    {
                        Trace.Verbose($"Adding Post-Job task step {step.DisplayName}.");
                        jobStepsBuilder.AddPostStep(taskLoadResult.PostScopeStep);
                    }
                }
                else if (step.Type == Pipelines.StepType.Group)
                {
                    var group = step as Pipelines.GroupStep;
                    Trace.Verbose($"Adding group step {step.DisplayName}.");
                    var groupRunner = HostContext.CreateService<IGroupRunner>();
                    groupRunner.Group = group;
                    groupRunner.Condition = stepConditionMap[step.Id];

                    StepsBuilder groupStepsBuilder = new StepsBuilder();
                    foreach (var groupStepTask in group.Steps)
                    {
                        var taskLoadResult = taskManager.Load(context, groupStepTask);
                        List<string> warnings;
                        _intraTaskVariablesMapping[groupStepTask.Id] = new Variables(HostContext, new Dictionary<string, VariableValue>(), out warnings);

                        // Add pre-group steps from Tasks
                        if (taskLoadResult.PreScopeStep != null)
                        {
                            Trace.Info($"Adding Pre-Group task step {groupStepTask.DisplayName}.");
                            var taskRunner = HostContext.CreateService<ITaskRunner>();
                            groupStepsBuilder.AddPreStep(taskLoadResult.PreScopeStep);
                        }

                        // Add group execution steps from Tasks
                        if (taskLoadResult.MainScopeStep != null)
                        {
                            Trace.Verbose($"Adding group task step {groupStepTask.DisplayName}.");
                            groupStepsBuilder.AddMainStep(taskLoadResult.MainScopeStep);
                        }

                        // Add post-group steps from Tasks
                        if (taskLoadResult.PostScopeStep != null)
                        {
                            Trace.Verbose($"Adding Post-Group task step {step.DisplayName}.");
                            groupStepsBuilder.AddPostStep(taskLoadResult.PostScopeStep);
                        }
                    }

                    groupRunner.Steps.AddRange(groupStepsBuilder.Result);
                    jobStepsBuilder.AddMainStep(groupRunner);
                }
            }

            // Add pre-job step from Extension
            Trace.Info("Adding pre-job step from extension.");
            var extensionPreJobStep = GetExtensionPreJobStep(context);
            if (extensionPreJobStep != null)
            {
                jobStepsBuilder.AddPreStep(extensionPreJobStep);
            }

            // Add post-job step from Extension
            Trace.Info("Adding post-job step from extension.");
            var extensionPostJobStep = GetExtensionPostJobStep(context);
            if (extensionPostJobStep != null)
            {
                jobStepsBuilder.AddPostStep(extensionPostJobStep);
            }

            // Inject container create/start steps to the jobSteps list.
            // tracking how many different containers will be used and how many times each container will be used in multiple steps.      
            // we will create required container just in time and also shutdown container as soon as the last step that need the container is finished.                      
            Dictionary<string, int> containerUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var containerProvider = HostContext.GetService<IContainerOperationProvider>();
            List<IStep> jobStepsWithContainerCreated = new List<IStep>();
            foreach (var step in jobStepsBuilder.Result)
            {
                if (!string.IsNullOrEmpty(step.Container?.Name))
                {
                    if (!containerUsage.ContainsKey(step.Container.Name))
                    {
                        containerUsage[step.Container.Name] = 1;
                        if (step is ITaskRunner)
                        {
                            jobStepsWithContainerCreated.Add(containerProvider.GetContainerStartStep(context, step.Container));
                        }
                        else if (step is IGroupRunner)
                        {
                            var groupRunner = step as IGroupRunner;
                            groupRunner.Steps.Insert(0, containerProvider.GetContainerStartStep(context, step.Container));
                        }
                    }
                    else
                    {
                        containerUsage[step.Container.Name]++;
                    }
                }

                jobStepsWithContainerCreated.Add(step);
            }

            // Tracing
            foreach (var container in containerUsage)
            {
                Trace.Verbose($"Container: '{container.Key}' --- {container.Value} times");
            }

            // Inject container stop steps to the jobSteps list.
            List<IStep> jobStepsWithContainerShutdown = new List<IStep>();
            foreach (var step in jobStepsWithContainerCreated)
            {
                if (!string.IsNullOrEmpty(step.Container?.Name))
                {
                    containerUsage[step.Container.Name]--;
                    if (containerUsage[step.Container.Name] == 0)
                    {
                        // Last one
                        if (step is ITaskRunner)
                        {
                            jobStepsWithContainerShutdown.Add(step);
                            jobStepsWithContainerShutdown.Add(containerProvider.GetContainerStopStep(context, step.Container));
                        }
                        else if (step is IGroupRunner)
                        {
                            var groupRunner = step as IGroupRunner;
                            groupRunner.Steps.Add(containerProvider.GetContainerStopStep(context, step.Container));
                            jobStepsWithContainerShutdown.Add(groupRunner);
                        }
                    }
                    else
                    {
                        jobStepsWithContainerShutdown.Add(step);
                    }
                }
                else
                {
                    jobStepsWithContainerShutdown.Add(step);
                }
            }

            return jobStepsWithContainerShutdown;
        }
    }

    public class StepsBuilder
    {
        private readonly List<IStep> _steps = new List<IStep>();

        Int32 _preInjectIndex = 0;
        Int32 _mainInjectIndex = 0;
        Int32 _postInjectIndex = 0;

        public List<IStep> Result => _steps;

        public void AddPreStep(IStep step)
        {
            _steps.Insert(_preInjectIndex, step);
            _preInjectIndex++;
            _mainInjectIndex++;
            _postInjectIndex++;
        }

        public void AddMainStep(IStep step)
        {
            _steps.Insert(_mainInjectIndex, step);
            _mainInjectIndex++;
            _postInjectIndex++;
        }

        public void AddPostStep(IStep step)
        {
            _steps.Insert(_postInjectIndex, step);
        }
    }
}