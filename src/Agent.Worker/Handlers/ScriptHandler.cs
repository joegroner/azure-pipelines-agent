using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Agent.Sdk.Knob;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker;
using Microsoft.VisualStudio.Services.Agent.Worker.Handlers;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    [ServiceLocator(Default = typeof(ScriptHandler))]
    public interface IScriptHandler : IHandler
    {
        ScriptHandlerData Data { get; set; }
    }

    public sealed class ScriptHandler : Handler, IScriptHandler
    {
        public ScriptHandlerData Data { get; set; }

        public async Task RunAsync()
        {
            // Validate args
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));

            Inputs.TryGetValue("script", out var contents);
            contents = contents ?? string.Empty;

            string workingDirectory = Data.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDirectory))
            {
                workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
            }

            string shell = Inputs.GetValueOrDefault("shell");

            var isContainerStepHost = StepHost is ContainerStepHost;

            string prependPath = string.Join(Path.PathSeparator.ToString(), ExecutionContext.PrependPath.Reverse<string>());
            string commandPath, argFormat, shellCommand;
            // Set up default command and arguments
            if (string.IsNullOrEmpty(shell))
            {
#if OS_WINDOWS
                shellCommand = "pwsh";
                commandPath = WhichUtil.Which(shellCommand, require: false, Trace, prependPath);
                if (string.IsNullOrEmpty(commandPath))
                {
                    shellCommand = "powershell";
                    Trace.Info($"Defaulting to {shellCommand}");
                    commandPath = WhichUtil.Which(shellCommand, require: true, Trace, prependPath);
                }
                ArgUtil.NotNullOrEmpty(commandPath, "Default Shell");
#else
                shellCommand = "sh";
                commandPath = WhichUtil.Which("bash", false, Trace, prependPath) ?? WhichUtil.Which("sh", true, Trace, prependPath);
#endif
                argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat(shellCommand);
            }
            else
            {
                // For these shells, we want to use system binaries
                var systemShells = new string[] { "bash", "sh", "powershell", "pwsh" };
                if (systemShells.Contains(shell))
                {
                    shellCommand = shell;
                    commandPath = WhichUtil.Which(shell, !isContainerStepHost, Trace, prependPath);
                    if (shell == "bash")
                    {
                        argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat("sh");
                    }
                    else
                    {
                        argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat(shell);
                    }
                }
                else
                {
                    var parsed = ScriptHandlerHelpers.ParseShellOptionString(shell);
                    shellCommand = parsed.shellCommand;

                    if(Path.IsPathFullyQualified(shellCommand) && File.Exists(shellCommand))
                    {
                        commandPath = shellCommand;
                        var command = Path.GetFileName(shellCommand);
                        argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat(command);
                    }
                    else
                    {
                        // For non-ContainerStepHost, the command must be located on the host by Which
                        commandPath = WhichUtil.Which(parsed.shellCommand, !isContainerStepHost, Trace, prependPath);
                        argFormat = $"{parsed.shellArgs}".TrimStart();
                        if (string.IsNullOrEmpty(argFormat))
                        {
                            argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat(shellCommand);
                        }
                    }
                }
            }

            // No arg format was given, shell must be a built-in
            if (string.IsNullOrEmpty(argFormat) || !argFormat.Contains("{0}"))
            {
                throw new ArgumentException("Invalid shell option. Shell must be a valid built-in (bash, sh, cmd, powershell, pwsh) or a format string containing '{0}'");
            }
            string scriptFilePath, resolvedScriptPath;
            // JobExtensionRunners run a script file, we load that from the inputs here
            if (!Inputs.ContainsKey("path"))
            {
                throw new ArgumentException("Expected 'path' input to be set");
            }
            scriptFilePath = Inputs["path"];
            ArgUtil.NotNullOrEmpty(scriptFilePath, "path");
            resolvedScriptPath = Inputs["path"].Replace("\"", "\\\"");

            // Format arg string with script path
            var arguments = string.Format(argFormat, resolvedScriptPath);

            // Fix up and write the script
            contents = ScriptHandlerHelpers.FixUpScriptContents(shellCommand, contents);
#if OS_WINDOWS
            // Normalize Windows line endings
            contents = contents.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var encoding = ExecutionContext.Global.Variables.Retain_Default_Encoding && Console.InputEncoding.CodePage != 65001
                ? Console.InputEncoding
                : new UTF8Encoding(false);
#else
            // Don't add a BOM. It causes the script to fail on some operating systems (e.g. on Ubuntu 14).
            var encoding = new UTF8Encoding(false);
#endif
            // Prepend PATH
            AddVariablesToEnvironment(excludeNames: true, excludeSecrets: true);
            AddPrependPathToEnvironment();

            // dump out the command
            var fileName = commandPath;
#if OS_OSX
            if (Environment.ContainsKey("DYLD_INSERT_LIBRARIES"))  // We don't check `isContainerStepHost` because we don't support container on macOS
            {
                // launch `node macOSRunInvoker.js shell args` instead of `shell args` to avoid macOS SIP remove `DYLD_INSERT_LIBRARIES` when launch process
                string node = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Externals), NodeUtil.GetInternalNodeVersion(), "bin", $"node{IOUtil.ExeExtension}");
                string macOSRunInvoker = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), "macos-run-invoker.js");
                arguments = $"\"{macOSRunInvoker.Replace("\"", "\\\"")}\" \"{fileName.Replace("\"", "\\\"")}\" {arguments}";
                fileName = node;
            }
#endif
            AddEndpointsToEnvironment();

            ExecutionContext.Debug($"{fileName} {arguments}");

            Inputs.TryGetValue("standardInInput", out var standardInInput);

            ExecutionContext.Debug(standardInInput);
            ExecutionContext.Debug(Environment["RUNNER_TEMP"]);

            StepHost.OutputDataReceived += OnDataReceived;
            StepHost.ErrorDataReceived += OnDataReceived;

            try
            {
                // Execute
                int exitCode = await StepHost.ExecuteAsync(
                                            ExecutionContext,
                                            workingDirectory: StepHost.ResolvePathForStepHost(workingDirectory),
                                            fileName: fileName,
                                            arguments: arguments,
                                            environment: Environment,
                                            requireExitCodeZero: false,
                                            outputEncoding: null,
                                            killProcessOnCancel: false,
                                            inheritConsoleHandler: !ExecutionContext.Variables.Retain_Default_Encoding,
                                            continueAfterCancelProcessTreeKillAttempt: AgentKnobs.ContinueAfterCancelProcessTreeKillAttempt.GetValue(ExecutionContext).AsBoolean(),
                                            standardInInput: standardInInput,
                                            cancellationToken: ExecutionContext.CancellationToken);
                
                // Error
                if (exitCode != 0)
                {
                    ExecutionContext.Error($"Process completed with exit code {exitCode}.");
                    ExecutionContext.Result = TaskResult.Failed;
                }
            }
            finally
            {
                StepHost.OutputDataReceived -= OnDataReceived;
                StepHost.ErrorDataReceived -= OnDataReceived;
            }
        }

        private void OnDataReceived(object sender, ProcessDataReceivedEventArgs e)
        {
            // This does not need to be inside of a critical section.
            // The logging queues and command handlers are thread-safe.
            if (!CommandManager.TryProcessCommand(ExecutionContext, e.Data))
            {
                ExecutionContext.Output(e.Data);
            }
        }
    }
}
