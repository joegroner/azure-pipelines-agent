using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Agent.Sdk;
using Agent.Sdk.Knob;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Handlers;
using Microsoft.VisualStudio.Services.Agent.Worker.Telemetry;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Container.ContainerHooks
{
    [ServiceLocator(Default = typeof(ContainerHookManager))]
    public interface IContainerHookManager : IAgentService
    {
        Task PrepareJobAsync(IExecutionContext context, List<ContainerInfo> containers);
        Task RunContainerStepAsync(IExecutionContext context, ContainerInfo container, string dockerFile);
        Task RunScriptStepAsync(IExecutionContext context, ContainerInfo container, string workingDirectory, string fileName, string arguments, IDictionary<string, string> environment, string prependPath);
        Task CleanupJobAsync(IExecutionContext context, List<ContainerInfo> containers);
        IDictionary<string,string> GetContainerHookData();
    }

    public class ContainerHookManager : AgentService, IContainerHookManager
    {
        private const string ResponseFolderName = "_runner_hook_responses";
        private string HookScriptPath;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            HookScriptPath = AgentKnobs.ContainerHooksPath.GetValue(hostContext).AsString();
        }

        public async Task PrepareJobAsync(IExecutionContext context, List<ContainerInfo> containers)
        {
            Trace.Entering();
            var jobContainer = containers.Where(c => c.IsJobContainer).SingleOrDefault();
            var serviceContainers = containers.Where(c => !c.IsJobContainer).ToList();

            var input = new HookInput
            {
                Command = HookCommand.PrepareJob,
                ResponseFile = GenerateResponsePath(),
                Args = new PrepareJobArgs
                {
                    Container = jobContainer?.GetHookContainer(),
                    Services = serviceContainers.Select(c => c.GetHookContainer()).ToList(),
                }
            };

            var prependPath = GetPrependPath(context);
            var response = await ExecuteHookScript<PrepareJobResponse>(context, input, JobRunStage.PreJob, prependPath);
            if (jobContainer != null)
            {
                jobContainer.IsAlpine = response.IsAlpine.Value;
                // TODO: add code to container hook to test for node 20_1 issues and set NeedsNode16Redirect
            }
            context.Debug(string.Format("response.state={0}", response.State));
            SaveHookState(context, response.State, input);
            UpdateJobContext(context, jobContainer, serviceContainers, response);
        }

        public async Task RunContainerStepAsync(IExecutionContext context, ContainerInfo container, string dockerFile)
        {
            Trace.Entering();
            var hookState = context.GlobalContext.ContainerHookState;
            var containerStepArgs = new ContainerStepArgs(container);
            if (!string.IsNullOrEmpty(dockerFile))
            {
                containerStepArgs.Dockerfile = dockerFile;
                containerStepArgs.Image = null;
            }
            var input = new HookInput
            {
                Args = containerStepArgs,
                Command = HookCommand.RunContainerStep,
                ResponseFile = GenerateResponsePath(),
                State = hookState
            };

            var prependPath = GetPrependPath(context);
            var response = await ExecuteHookScript<HookResponse>(context, input, JobRunStage.PreJob, prependPath);
            if (response == null)
            {
                return;
            }
            SaveHookState(context, response.State, input);
        }

        public async Task RunScriptStepAsync(IExecutionContext context, ContainerInfo container, string workingDirectory, string entryPoint, string entryPointArgs, IDictionary<string, string> environmentVariables, string prependPath)
        {
            Trace.Entering();

            context.Debug(string.Format("context.containerhookstate={0}", context.GlobalContext.ContainerHookState));

            var input = new HookInput
            {
                Command = HookCommand.RunScriptStep,
                ResponseFile = GenerateResponsePath(),
                Args = new ScriptStepArgs
                {
                    EntryPointArgs = entryPointArgs.Split(' ').Select(arg => arg.Trim()),
                    EntryPoint = entryPoint,
                    EnvironmentVariables = environmentVariables,
                    PrependPath = context.PrependPath.Reverse<string>(),
                    WorkingDirectory = workingDirectory,
                },
                State = context.GlobalContext.ContainerHookState
            };

            var response = await ExecuteHookScript<HookResponse>(context, input, JobRunStage.PreJob, prependPath);

            if (response == null)
            {
                return;
            }
            SaveHookState(context, response.State, input);
        }

        public async Task CleanupJobAsync(IExecutionContext context, List<ContainerInfo> containers)
        {
            Trace.Entering();
            var input = new HookInput
            {
                Command = HookCommand.CleanupJob,
                ResponseFile = GenerateResponsePath(),
                Args = new CleanupJobArgs(),
                State = context.GlobalContext.ContainerHookState
            };
            var prependPath = GetPrependPath(context);
            await ExecuteHookScript<HookResponse>(context, input, JobRunStage.PreJob, prependPath);
        }

        public IDictionary<string,string> GetContainerHookData()
        {
            return new Dictionary<string,string> { {"hookScriptPath", HookScriptPath} };
        }

        private async Task<T> ExecuteHookScript<T>(IExecutionContext context, HookInput input, JobRunStage stage, string prependPath) where T : HookResponse
        {
            try
            {
                ValidateHookExecutable();
                PublishTelemetry(context, GetContainerHookData());
                var scriptDirectory = Path.GetDirectoryName(HookScriptPath);
                var stepHost = HostContext.CreateService<IDefaultStepHost>();

                Dictionary<string, string> inputs = new()
                {
                    ["standardInInput"] = JsonUtility.ToString(input),
                    ["path"] = HookScriptPath,
                    ["shell"] = GetDefaultShellForScript(context, HookScriptPath, prependPath)
                };

                var handlerFactory = HostContext.GetService<IHandlerFactory>();
                var handler = handlerFactory.Create(
                                context,
                                null,
                                stepHost,
                                context.Endpoints,
                                context.SecureFiles,
                                new ScriptHandlerData(),
                                inputs,
                                environment: new Dictionary<string, string>(VarUtil.EnvironmentVariableKeyComparer),
                                context.Variables,
                                scriptDirectory) as ScriptHandler;
                //handler.PrepareExecution(stage);

                IOUtil.CreateEmptyFile(input.ResponseFile);
                await handler.RunAsync();
                if (handler.ExecutionContext.Result.HasValue && handler.ExecutionContext.Result.Value == TeamFoundation.DistributedTask.WebApi.TaskResult.Failed)
                {
                    throw new Exception($"The hook script at '{HookScriptPath}' running command '{input.Command}' did not execute successfully");
                }
                var response = GetResponse<T>(input);
                return response;
            }
            catch (Exception ex)
            {
                throw new Exception($"Executing the custom container implementation failed. Please contact your self hosted runner administrator.\n" + ex.ToString(), ex);
            }
        }

        private string GenerateResponsePath() => Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Temp), ResponseFolderName, $"{Guid.NewGuid()}.json");

        private static string GetPrependPath(IExecutionContext context) => string.Join(Path.PathSeparator.ToString(), context.PrependPath.Reverse<string>());

        private void ValidateHookExecutable()
        {
            if (!string.IsNullOrEmpty(HookScriptPath) && !File.Exists(HookScriptPath))
            {
                throw new FileNotFoundException($"File not found at '{HookScriptPath}'. Set {AgentKnobs.ContainerHooksPath.Name} to the path of an existing file.");
            }

            var supportedHookExtensions = new string[] { ".js", ".sh", ".ps1" };
            if (!supportedHookExtensions.Any(extension => HookScriptPath.EndsWith(extension)))
            {
                throw new ArgumentOutOfRangeException($"Invalid file extension at '{HookScriptPath}'. {AgentKnobs.ContainerHooksPath.Name} must be a path to a file with one of the following extensions: {string.Join(", ", supportedHookExtensions)}");
            }
        }

        private T GetResponse<T>(HookInput input) where T : HookResponse
        {
            if (!File.Exists(input.ResponseFile))
            {
                Trace.Info($"Response file for the hook script at '{HookScriptPath}' running command '{input.Command}' not found.");
                if (input.Args.IsRequireAlpineInResponse())
                {
                    throw new Exception($"Response file is required but not found for the hook script at '{HookScriptPath}' running command '{input.Command}'");
                }
                return null;
            }

            T response = IOUtil.LoadObject<T>(input.ResponseFile);
            Trace.Info($"Response file for the hook script at '{HookScriptPath}' running command '{input.Command}' was processed successfully");
            IOUtil.DeleteFile(input.ResponseFile);
            Trace.Info($"Response file for the hook script at '{HookScriptPath}' running command '{input.Command}' was deleted successfully");
            if (response == null && input.Args.IsRequireAlpineInResponse())
            {
                throw new Exception($"Response file could not be read at '{HookScriptPath}' running command '{input.Command}'");
            }
            response?.Validate(input);
            return response;
        }

        private void SaveHookState(IExecutionContext context, JObject hookState, HookInput input)
        {
            if (hookState == null)
            {
                Trace.Info($"No 'state' property found in response file for '{input.Command}'. Global variable for 'ContainerHookState' will not be updated.");
                return;
            }
            context.GlobalContext.ContainerHookState = hookState;
            Trace.Info($"Global variable 'ContainerHookState' updated successfully for '{input.Command}' with data found in 'state' property of the response file.");
        }

        private void UpdateJobContext(IExecutionContext context, ContainerInfo jobContainer, List<ContainerInfo> serviceContainers, PrepareJobResponse response)
        {
            if (response.Context == null)
            {
                Trace.Info(
                    $"The response file does not contain a context. The context variables {0}, {1}, {2} will not be set.", 
                    Constants.Variables.Agent.ContainerNetwork,
                    Constants.Variables.Agent.ContainerMapping,
                    Constants.Variables.Agent.ServicePortPrefix
                );
                return;
            }

            var containerNetwork = response.Context.Container?.Network;
            if (containerNetwork != null)
            {
                jobContainer.ContainerNetwork = containerNetwork;
                context.Variables.Set(Constants.Variables.Agent.ContainerNetwork, containerNetwork);
            }

            // Build JSON to expose docker container name mapping to env
            var containerMapping = new JObject();
            var containerId = response.Context.Container?.Id;
            if (containerId != null)
            {
                var containerInfo = new JObject();
                containerInfo["id"] = containerId;
                containerMapping[jobContainer.ContainerName] = containerInfo;
            }

            for (var i = 0; i < response.Context.Services.Count; i++)
            {
                var responseContainerInfo = response.Context.Services[i];
                var globalContainerInfo = serviceContainers[i];
                globalContainerInfo.ContainerId = responseContainerInfo.Id;
                globalContainerInfo.ContainerNetwork = responseContainerInfo.Network;

                var containerInfo = new JObject();
                containerInfo["id"] = globalContainerInfo.ContainerId;
                containerMapping[globalContainerInfo.ContainerName] = containerInfo;

                foreach (var port in globalContainerInfo.PortMappings)
                {
                    context.Variables.Set(
                        $"{Constants.Variables.Agent.ServicePortPrefix}.{globalContainerInfo.ContainerNetworkAlias}.ports.{port.ContainerPort}",
                        $"{port.HostPort}");
                }
            }

            context.Variables.Set(Constants.Variables.Agent.ContainerMapping, containerMapping.ToString());

            // The container-hooks expect these variables to be in the environment
            context.Variables.Set("github.workspace", context.Variables.Get(Constants.Variables.System.DefaultWorkingDirectory));
            context.Variables.Set("runner.workspace", context.Variables.Get(Constants.Variables.Agent.WorkFolder));
            context.Variables.Set("runner.temp", context.Variables.Get(Constants.Variables.Agent.TempDirectory));
        }

        private void PublishTelemetry(
            IExecutionContext executionContext,
            object telemetryData,
            string feature = nameof(ContainerHookManager)
            )
        {
            var cmd = new Command("telemetry", "publish")
            {
                Data = JsonConvert.SerializeObject(telemetryData, Formatting.None)
            };
            cmd.Properties.Add("area", "PipelinesTasks");
            cmd.Properties.Add("feature", feature);

            var publishTelemetryCmd = new TelemetryCommandExtension();
            publishTelemetryCmd.Initialize(HostContext);
            publishTelemetryCmd.ProcessCommand(executionContext, cmd);
        }

        private string GetDefaultShellForScript(IExecutionContext executionContext, string path, string prependPath)
        {
            switch (Path.GetExtension(path))
            {
                case ".sh":
                    // use 'sh' args but prefer bash
                    if (WhichUtil.Which("bash", false, Trace, prependPath) != null)
                    {
                        return "bash";
                    }
                    return "sh";
                case ".ps1":
                    if (WhichUtil.Which("pwsh", false, Trace, prependPath) != null)
                    {
                        return "pwsh";
                    }
                    return "powershell";
                case ".js":
                    return Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Externals), NodeUtil.GetInternalNodeVersion(executionContext), "bin", $"node{IOUtil.ExeExtension}") + " {0}";
                default:
                    throw new ArgumentException($"{path} is not a valid path to a script. Make sure it ends in '.sh', '.ps1' or '.js'.");
            }
        }
    }
}
