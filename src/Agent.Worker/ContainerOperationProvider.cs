using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Expressions = Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Expressions;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Handlers;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;
using System.Threading;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
#if OS_WINDOWS
    [ServiceLocator(Default = typeof(WindowsContainerOperationProvider))]
#else
    [ServiceLocator(Default = typeof(LinuxContainerOperationProvider))]
#endif
    public interface IContainerOperationProvider : IAgentService
    {
        IStep GetContainerStartStep(IExecutionContext jobContext, Pipelines.ContainerReference container);
        IStep GetContainerStopStep(IExecutionContext jobContext, Pipelines.ContainerReference container);
        // void GetHandlerContainerExecutionCommandline(ContainerInfo container, string filePath, string arguments, string workingDirectory, IDictionary<string, string> environment, out string containerEnginePath, out string containerExecutionArgs);
    }

#if OS_WINDOWS
    public class WindowsContainerOperationProvider : AgentService, IContainerOperationProvider
    {
        public IStep GetContainerStartStep(IExecutionContext jobContext, Pipelines.ContainerReference container)
        {
            ArgUtil.NotNull(container, nameof(container));
            Dictionary<string, string> data = new Dictionary<string, string>(container.Data, StringComparer.OrdinalIgnoreCase);
            data["image"] = container.Image;
            data["name"] = container.Name;
            return new JobExtensionRunner(data: data, runAsync: StartContainerAsync, condition: ExpressionManager.Succeeded, displayName: StringUtil.Loc("InitializeContainer"));
        }

        public IStep GetContainerStopStep(IExecutionContext jobContext, Pipelines.ContainerReference container)
        {
            Dictionary<string, string> data = new Dictionary<string, string>(container.Data, StringComparer.OrdinalIgnoreCase);
            data["image"] = container.Image;
            data["name"] = container.Name;
            return new JobExtensionRunner(data: data, runAsync: StopContainerAsync, condition: ExpressionManager.Always, displayName: StringUtil.Loc("StopContainer"));
        }

        private Task StartContainerAsync(IExecutionContext executionContext, Dictionary<string, string> data)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            executionContext.Output($"Create container {data["image"]}");
            return Task.CompletedTask;
        }

        private Task StopContainerAsync(IExecutionContext executionContext, Dictionary<string, string> data)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            executionContext.Output($"Stop container {data["name"]}");
            return Task.CompletedTask;
        }
    }
#else
    public class LinuxContainerOperationProvider : AgentService, IContainerOperationProvider
    {
        private IDockerCommandManager _dockerManger;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _dockerManger = HostContext.GetService<IDockerCommandManager>();
        }
        public IStep GetContainerStartStep(IExecutionContext context, Pipelines.ContainerReference container)
        {
            ArgUtil.NotNull(container, nameof(container));
            Dictionary<string, string> data = new Dictionary<string, string>(container.Data, StringComparer.OrdinalIgnoreCase);
            data["image"] = container.Image;
            data["name"] = container.Name;
            return new JobExtensionRunner(data: data, runAsync: StartContainerAsync, condition: ExpressionManager.Succeeded, displayName: StringUtil.Loc("InitializeContainer"));
        }

        public IStep GetContainerStopStep(IExecutionContext ontext, Pipelines.ContainerReference container)
        {
            Dictionary<string, string> data = new Dictionary<string, string>(container.Data, StringComparer.OrdinalIgnoreCase);
            data["image"] = container.Image;
            data["name"] = container.Name;
            return new JobExtensionRunner(data: data, runAsync: StopContainerAsync, condition: ExpressionManager.Always, displayName: StringUtil.Loc("StopContainer"));
        }

        public void GetHandlerContainerExecutionCommandline(
            ContainerInfo container,
            string filePath,
            string arguments,
            string workingDirectory,
            IDictionary<string, string> environment,
            out string containerEnginePath,
            out string containerExecutionArgs)
        {
            string envOptions = "";
            foreach (var env in environment)
            {
                envOptions += $" -e \"{env.Key}={env.Value.Replace("\"", "\\\"")}\"";
            }

            // we need cd to the workingDir then run the executable with args.
            // bash -c "cd \"workingDirectory\"; \"filePath\" \"arguments\""
            string workingDirectoryEscaped = StringUtil.Format(@"\""{0}\""", workingDirectory.Replace(@"""", @"\\\"""));
            string filePathEscaped = StringUtil.Format(@"\""{0}\""", filePath.Replace(@"""", @"\\\"""));
            string argumentsEscaped = arguments.Replace(@"\", @"\\").Replace(@"""", @"\""");
            string bashCommandLine = $"bash -c \"cd {workingDirectoryEscaped}&{filePathEscaped} {argumentsEscaped}\"";

            arguments = $"exec -u {container.CurrentUserId} {envOptions} {container.ContainerId} {bashCommandLine}";

            containerEnginePath = _dockerManger.DockerPath;
            containerExecutionArgs = arguments;
        }

        private async Task StartContainerAsync(IExecutionContext executionContext, Dictionary<string, string> data)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(data, nameof(data));

            data.TryGetValue("name", out string containerName);
            data.TryGetValue("image", out string containerImage);
            data.TryGetValue("localimage", out string localImage);
            data.TryGetValue("registry", out string containerRegistry);
            data.TryGetValue("options", out string containerOptions);

            ArgUtil.NotNullOrEmpty(containerName, nameof(containerName));
            ArgUtil.NotNullOrEmpty(containerImage, nameof(containerImage));

            // Check docker client/server version
            DockerVersion dockerVersion = await _dockerManger.DockerVersion(executionContext);
            ArgUtil.NotNull(dockerVersion.ServerVersion, nameof(dockerVersion.ServerVersion));
            ArgUtil.NotNull(dockerVersion.ClientVersion, nameof(dockerVersion.ClientVersion));
            Version requiredDockerVersion = new Version(17, 3);
            if (dockerVersion.ServerVersion < requiredDockerVersion)
            {
                throw new NotSupportedException(StringUtil.Loc("MinRequiredDockerServerVersion", requiredDockerVersion, _dockerManger.DockerPath, dockerVersion.ServerVersion));
            }
            if (dockerVersion.ClientVersion < requiredDockerVersion)
            {
                throw new NotSupportedException(StringUtil.Loc("MinRequiredDockerClientVersion", requiredDockerVersion, _dockerManger.DockerPath, dockerVersion.ClientVersion));
            }

            // Login to private docker registry
            string registryServer = string.Empty;
            if (!string.IsNullOrEmpty(containerRegistry))
            {
                Trace.Info(containerRegistry);
                foreach (var e in executionContext.Endpoints)
                {
                    Trace.Info(e.Name);
                    Trace.Info(e.Type);
                }

                var registryEndpoint = executionContext.Endpoints.FirstOrDefault(x => x.Id.ToString() == containerRegistry && x.Type == "dockerregistry");
                ArgUtil.NotNull(registryEndpoint, nameof(registryEndpoint));

                string username = string.Empty;
                string password = string.Empty;
                registryEndpoint.Authorization?.Parameters?.TryGetValue("registry", out registryServer);
                registryEndpoint.Authorization?.Parameters?.TryGetValue("username", out username);
                registryEndpoint.Authorization?.Parameters?.TryGetValue("password", out password);

                ArgUtil.NotNullOrEmpty(registryServer, nameof(registryServer));
                ArgUtil.NotNullOrEmpty(username, nameof(username));
                ArgUtil.NotNullOrEmpty(password, nameof(password));

                int loginExitCode = await _dockerManger.DockerLogin(executionContext, registryServer, username, password);
                if (loginExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker login fail with exit code {loginExitCode}");
                }
            }

            ContainerInfo container = new ContainerInfo();

            // keep tracking container
            executionContext.Containers[containerName] = container;

            container.ContainerName = containerName; // TODO: remove invalid chars.
            container.ContainerImage = containerImage;

            bool skipDockerPull = StringUtil.ConvertToBoolean(localImage, false);
            if (!skipDockerPull)
            {
                string imageName = container.ContainerImage;
                if (!string.IsNullOrEmpty(registryServer) && registryServer.IndexOf("index.docker.io", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    imageName = $"{registryServer}/{imageName}";
                }

                // Pull down docker image
                int pullExitCode = await _dockerManger.DockerPull(executionContext, imageName);
                if (pullExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker pull failed with exit code {pullExitCode}");
                }
            }

            // Mount folder into container
            container.MountVolumes.Add(new MountVolume(Path.GetDirectoryName(executionContext.Variables.System_DefaultWorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            container.MountVolumes.Add(new MountVolume(executionContext.Variables.Agent_TempDirectory));
            container.MountVolumes.Add(new MountVolume(executionContext.Variables.Agent_ToolsDirectory));
            container.MountVolumes.Add(new MountVolume(HostContext.GetDirectory(WellKnownDirectory.Tasks)));
            container.MountVolumes.Add(new MountVolume(HostContext.GetDirectory(WellKnownDirectory.Externals), true));

            // Ensure .taskkey file exist so we can mount it.
            string taskKeyFile = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), ".taskkey");
            if (!File.Exists(taskKeyFile))
            {
                File.WriteAllText(taskKeyFile, string.Empty);
            }
            container.MountVolumes.Add(new MountVolume(taskKeyFile));

            try
            {
                container.ContainerId = await _dockerManger.DockerCreate(executionContext, container, containerOptions);
                ArgUtil.NotNullOrEmpty(container.ContainerId, nameof(container.ContainerId));

                // Start container
                int startExitCode = await _dockerManger.DockerStart(executionContext, container.ContainerId);
                if (startExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker start fail with exit code {startExitCode}");
                }
            }
            finally
            {
                // Logout for private registry
                if (!string.IsNullOrEmpty(registryServer))
                {
                    int logoutExitCode = await _dockerManger.DockerLogout(executionContext, registryServer);
                    if (logoutExitCode != 0)
                    {
                        executionContext.Error($"Docker logout fail with exit code {logoutExitCode}");
                    }
                }
            }

            // Ensure bash exist in the image
            int execWhichBashExitCode = await _dockerManger.DockerExec(executionContext, container.ContainerId, string.Empty, $"which bash");
            if (execWhichBashExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execWhichBashExitCode}");
            }

            // Get current username
            container.CurrentUserName = (await ExecuteCommandAsync(executionContext, "whoami", string.Empty)).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(container.CurrentUserName, nameof(container.CurrentUserName));

            // Get current userId
            container.CurrentUserId = (await ExecuteCommandAsync(executionContext, "id", $"-u {container.CurrentUserName}")).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(container.CurrentUserId, nameof(container.CurrentUserId));

            executionContext.Output(StringUtil.Loc("CreateUserWithSameUIDInsideContainer", container.CurrentUserId));

            // Create an user with same uid as the agent run as user inside the container.
            // All command execute in docker will run as Root by default, 
            // this will cause the agent on the host machine doesn't have permission to any new file/folder created inside the container.
            // So, we create a user account with same UID inside the container and let all docker exec command run as that user.
            string containerUserName = string.Empty;

            // We need to find out whether there is a user with same UID inside the container
            List<string> userNames = new List<string>();
            int execGrepExitCode = await _dockerManger.DockerExec(executionContext, container.ContainerId, string.Empty, $"bash -c \"grep {container.CurrentUserId} /etc/passwd | cut -f1 -d:\"", userNames);
            if (execGrepExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execGrepExitCode}");
            }

            if (userNames.Count > 0)
            {
                // check all potential username that might match the UID.
                foreach (string username in userNames)
                {
                    int execIdExitCode = await _dockerManger.DockerExec(executionContext, container.ContainerId, string.Empty, $"id -u {username}");
                    if (execIdExitCode == 0)
                    {
                        containerUserName = username;
                        break;
                    }
                }
            }

            // Create a new user with same UID
            if (string.IsNullOrEmpty(containerUserName))
            {
                containerUserName = $"{container.CurrentUserName}_VSTSContainer";
                int execUseraddExitCode = await _dockerManger.DockerExec(executionContext, container.ContainerId, string.Empty, $"useradd -m -u {container.CurrentUserId} {containerUserName}");
                if (execUseraddExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker exec fail with exit code {execUseraddExitCode}");
                }
            }

            executionContext.Output(StringUtil.Loc("GrantContainerUserSUDOPrivilege", containerUserName));

            // Create a new vsts_sudo group for giving sudo permission
            int execGroupaddExitCode = await _dockerManger.DockerExec(executionContext, container.ContainerId, string.Empty, $"groupadd VSTS_Container_SUDO");
            if (execGroupaddExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execGroupaddExitCode}");
            }

            // Add the new created user to the new created VSTS_SUDO group.
            int execUsermodExitCode = await _dockerManger.DockerExec(executionContext, container.ContainerId, string.Empty, $"usermod -a -G VSTS_Container_SUDO {containerUserName}");
            if (execUsermodExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execUsermodExitCode}");
            }

            // Allow the new vsts_sudo group run any sudo command without providing password.
            int execEchoExitCode = await _dockerManger.DockerExec(executionContext, container.ContainerId, string.Empty, $"su -c \"echo '%VSTS_Container_SUDO ALL=(ALL:ALL) NOPASSWD:ALL' >> /etc/sudoers\"");
            if (execUsermodExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execEchoExitCode}");
            }
        }

        private async Task StopContainerAsync(IExecutionContext executionContext, Dictionary<string, string> data)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(data, nameof(data));

            data.TryGetValue("name", out string containerName);

            if (executionContext.Containers.TryGetValue(containerName, out ContainerInfo container) &&
                !string.IsNullOrEmpty(container?.ContainerId))
            {
                executionContext.Output($"Stop container: {container.ContainerName}");

                int stopExitCode = await _dockerManger.DockerStop(executionContext, container.ContainerId);
                if (stopExitCode != 0)
                {
                    executionContext.Error($"Docker stop fail with exit code {stopExitCode}");
                }
            }
        }

        private async Task<List<string>> ExecuteCommandAsync(IExecutionContext context, string command, string arg)
        {
            context.Command($"{command} {arg}");

            List<string> outputs = new List<string>();
            object outputLock = new object();
            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        outputs.Add(message.Data);
                    }
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        outputs.Add(message.Data);
                    }
                }
            };

            await processInvoker.ExecuteAsync(
                            workingDirectory: context.Variables.Agent_WorkFolder,
                            fileName: command,
                            arguments: arg,
                            environment: null,
                            requireExitCodeZero: true,
                            outputEncoding: null,
                            cancellationToken: CancellationToken.None);

            foreach (var outputLine in outputs)
            {
                context.Output(outputLine);
            }

            return outputs;
        }
    }
#endif
}
