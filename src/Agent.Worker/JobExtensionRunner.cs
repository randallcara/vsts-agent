using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Expressions;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public sealed class JobExtensionRunner : IStep
    {
        private readonly Func<IExecutionContext, Dictionary<string, string>, Task> _runAsync;

        private readonly Dictionary<string, string> _data;

        public JobExtensionRunner(
            Dictionary<string, string> data,
            Func<IExecutionContext, Dictionary<string, string>, Task> runAsync,
            INode condition,
            string displayName)
        {
            _data = data;
            _runAsync = runAsync;
            Condition = condition;
            DisplayName = displayName;
        }

        public INode Condition { get; set; }
        public bool ContinueOnError => false;
        public string DisplayName { get; private set; }
        public bool Enabled => true;
        public IExecutionContext ExecutionContext { get; set; }
        public TimeSpan? Timeout => null;
        public Pipelines.ContainerReference Container => null;
        public async Task RunAsync()
        {
            await _runAsync(ExecutionContext, _data);
        }

        public void InitializeStep(IExecutionContext jobExecutionContext, Dictionary<Guid, Variables> intraStepVariables = null)
        {
            ExecutionContext = jobExecutionContext.CreateChild(Guid.NewGuid(), DisplayName, nameof(JobExtensionRunner));
        }
    }
}
