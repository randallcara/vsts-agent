using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    public interface IHandlerInvoker : IAgentService
    {
        event EventHandler<ProcessDataReceivedEventArgs> OutputDataReceived;
        event EventHandler<ProcessDataReceivedEventArgs> ErrorDataReceived;

        Task<int> ExecuteAsync(
            string workingDirectory,
            string fileName,
            string arguments,
            IDictionary<string, string> environment,
            bool requireExitCodeZero,
            Encoding outputEncoding,
            bool killProcessOnCancel,
            bool enhancedProcessesCleanup,
            CancellationToken cancellationToken);
    }

    [ServiceLocator(Default = typeof(ContainerHandlerInvoker))]
    public interface IContainerHandlerInvoker : IHandlerInvoker
    {
        ContainerInfo Container { get; set; }
    }

    [ServiceLocator(Default = typeof(ProcessHandlerInvoker))]
    public interface IDefaultHandlerInvoker : IHandlerInvoker
    {
    }


    public sealed class ProcessHandlerInvoker : AgentService, IDefaultHandlerInvoker
    {
        public event EventHandler<ProcessDataReceivedEventArgs> OutputDataReceived;
        public event EventHandler<ProcessDataReceivedEventArgs> ErrorDataReceived;

        public async Task<int> ExecuteAsync(
             string workingDirectory,
             string fileName,
             string arguments,
             IDictionary<string, string> environment,
             bool requireExitCodeZero,
             Encoding outputEncoding,
             bool killProcessOnCancel,
             bool enhancedProcessesCleanup,
             CancellationToken cancellationToken)
        {
            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += OutputDataReceived;
                processInvoker.ErrorDataReceived += ErrorDataReceived;

                return await processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                 fileName: fileName,
                                                 arguments: arguments,
                                                 environment: environment,
                                                 requireExitCodeZero: requireExitCodeZero,
                                                 outputEncoding: outputEncoding,
                                                 killProcessOnCancel: killProcessOnCancel,
                                                 enhancedProcessesCleanup: enhancedProcessesCleanup,
                                                 cancellationToken: cancellationToken);
            }
        }
    }

    public sealed class ContainerHandlerInvoker : AgentService, IContainerHandlerInvoker
    {
        public ContainerInfo Container { get; set; }
        public event EventHandler<ProcessDataReceivedEventArgs> OutputDataReceived;
        public event EventHandler<ProcessDataReceivedEventArgs> ErrorDataReceived;

        public async Task<int> ExecuteAsync(
             string workingDirectory,
             string fileName,
             string arguments,
             IDictionary<string, string> environment,
             bool requireExitCodeZero,
             Encoding outputEncoding,
             bool killProcessOnCancel,
             bool enhancedProcessesCleanup,
             CancellationToken cancellationToken)
        {
            // make sure container exist.
            ArgUtil.NotNull(Container, nameof(Container));
            ArgUtil.NotNullOrEmpty(Container.ContainerId, nameof(Container.ContainerId));

            var dockerManger = HostContext.GetService<IDockerCommandManager>();
            string containerEnginePath = dockerManger.DockerPath;

            string envOptions = "";
            foreach (var env in environment)
            {
                envOptions += $" -e \"{env.Key}={env.Value.Replace("\"", "\\\"")}\"";
            }

            // we need cd to the workingDir then run the executable with args.
            // bash -c "cd \"workingDirectory\"; \"filePath\" \"arguments\""
            string workingDirectoryEscaped = StringUtil.Format(@"\""{0}\""", workingDirectory.Replace(@"""", @"\\\"""));
            string filePathEscaped = StringUtil.Format(@"\""{0}\""", fileName.Replace(@"""", @"\\\"""));
            string argumentsEscaped = arguments.Replace(@"\", @"\\").Replace(@"""", @"\""");
            string bashCommandLine = $"bash -c \"cd {workingDirectoryEscaped}&{filePathEscaped} {argumentsEscaped}\"";

            string containerExecutionArgs = $"exec -u {Container.CurrentUserId} {envOptions} {Container.ContainerId} {bashCommandLine}"; ;

            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += OutputDataReceived;
                processInvoker.ErrorDataReceived += ErrorDataReceived;

                return await processInvoker.ExecuteAsync(workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Work),
                                                 fileName: containerEnginePath,
                                                 arguments: containerExecutionArgs,
                                                 environment: null,
                                                 requireExitCodeZero: requireExitCodeZero,
                                                 outputEncoding: outputEncoding,
                                                 killProcessOnCancel: killProcessOnCancel,
                                                 enhancedProcessesCleanup: enhancedProcessesCleanup,
                                                 cancellationToken: cancellationToken);
            }
        }
    }

    [ServiceLocator(Default = typeof(HandlerFactory))]
    public interface IHandlerFactory : IAgentService
    {
        IHandler Create(
            IExecutionContext executionContext,
            IHandlerInvoker handlerInvoker,
            List<ServiceEndpoint> endpoints,
            List<SecureFile> secureFiles,
            HandlerData data,
            Dictionary<string, string> inputs,
            Dictionary<string, string> environment,
            string taskDirectory,
            string filePathInputRootDirectory);
    }

    public sealed class HandlerFactory : AgentService, IHandlerFactory
    {
        public IHandler Create(
            IExecutionContext executionContext,
            IHandlerInvoker handlerInvoker,
            List<ServiceEndpoint> endpoints,
            List<SecureFile> secureFiles,
            HandlerData data,
            Dictionary<string, string> inputs,
            Dictionary<string, string> environment,
            string taskDirectory,
            string filePathInputRootDirectory)
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(handlerInvoker, nameof(handlerInvoker));
            ArgUtil.NotNull(endpoints, nameof(endpoints));
            ArgUtil.NotNull(secureFiles, nameof(secureFiles));
            ArgUtil.NotNull(data, nameof(data));
            ArgUtil.NotNull(inputs, nameof(inputs));
            ArgUtil.NotNull(environment, nameof(environment));
            ArgUtil.NotNull(taskDirectory, nameof(taskDirectory));

            // Create the handler.
            IHandler handler;
            if (data is NodeHandlerData)
            {
                // Node.
                handler = HostContext.CreateService<INodeHandler>();
                (handler as INodeHandler).Data = data as NodeHandlerData;
            }
            else if (data is PowerShell3HandlerData)
            {
                // PowerShell3.
                handler = HostContext.CreateService<IPowerShell3Handler>();
                (handler as IPowerShell3Handler).Data = data as PowerShell3HandlerData;
            }
            else if (data is PowerShellExeHandlerData)
            {
                // PowerShellExe.
                handler = HostContext.CreateService<IPowerShellExeHandler>();
                (handler as IPowerShellExeHandler).Data = data as PowerShellExeHandlerData;
            }
            else if (data is ProcessHandlerData)
            {
                // Process.
                handler = HostContext.CreateService<IProcessHandler>();
                (handler as IProcessHandler).Data = data as ProcessHandlerData;
            }
            else if (data is PowerShellHandlerData)
            {
                // PowerShell.
                handler = HostContext.CreateService<IPowerShellHandler>();
                (handler as IPowerShellHandler).Data = data as PowerShellHandlerData;
            }
            else if (data is AzurePowerShellHandlerData)
            {
                // AzurePowerShell.
                handler = HostContext.CreateService<IAzurePowerShellHandler>();
                (handler as IAzurePowerShellHandler).Data = data as AzurePowerShellHandlerData;
            }
            else
            {
                // This should never happen.
                throw new NotSupportedException();
            }

            handler.Endpoints = endpoints;
            handler.Environment = environment;
            handler.ExecutionContext = executionContext;
            handler.HandlerInvoker = handlerInvoker;
            handler.FilePathInputRootDirectory = filePathInputRootDirectory;
            handler.Inputs = inputs;
            handler.SecureFiles = secureFiles;
            handler.TaskDirectory = taskDirectory;
            return handler;
        }
    }
}