using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System.Linq;
using Agent.Sdk;
using System.Text.RegularExpressions;
using System;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Container.ContainerHooks
{
    public class HookInput
    {
        public HookCommand Command { get; set; }
        public string ResponseFile { get; set; }
        public IHookArgs Args { get; set; }
        public JObject State { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum HookCommand
    {
        [EnumMember(Value = "prepare_job")]
        PrepareJob,
        [EnumMember(Value = "cleanup_job")]
        CleanupJob,
        [EnumMember(Value = "run_script_step")]
        RunScriptStep,
        [EnumMember(Value = "run_container_step")]
        RunContainerStep,
    }

    [ServiceLocator(Default = typeof(NullArgs))]
    public interface IHookArgs
    {
        bool IsRequireAlpineInResponse();
    }

    // to keep the ServiceLocator happy for testing
    public class NullArgs : IHookArgs
    {
        public bool IsRequireAlpineInResponse() => false;
    }

    public class PrepareJobArgs : IHookArgs
    {
        public HookContainer Container { get; set; }
        public IList<HookContainer> Services { get; set; }
        public bool IsRequireAlpineInResponse() => Container != null;
    }

    public class ScriptStepArgs : IHookArgs
    {
        public IEnumerable<string> EntryPointArgs { get; set; }
        public string EntryPoint { get; set; }
        public IDictionary<string, string> EnvironmentVariables { get; set; }
        public IEnumerable<string> PrependPath { get; set; }
        public string WorkingDirectory { get; set; }
        public bool IsRequireAlpineInResponse() => false;
    }

    public class ContainerStepArgs : HookContainer, IHookArgs
    {
        public bool IsRequireAlpineInResponse() => false;
        public ContainerStepArgs(ContainerInfo container) : base(container) { }
    }
    public class CleanupJobArgs : IHookArgs
    {
        public bool IsRequireAlpineInResponse() => false;
    }

    public class ContainerRegistry
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string ServerUrl { get; set; }
    }

    public class HookContainer
    {
        public string Image { get; set; }
        public string Dockerfile { get; set; }
        public IEnumerable<string> EntryPointArgs { get; set; } = new List<string>();
        public string EntryPoint { get; set; }
        public string WorkingDirectory { get; set; }
        public string CreateOptions { get; private set; }
        public ContainerRegistry Registry { get; set; }
        public IDictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
        public IEnumerable<string> PortMappings { get; set; } = new List<string>();
        public IEnumerable<MountVolume> SystemMountVolumes { get; set; } = new List<MountVolume>();
        public IEnumerable<MountVolume> UserMountVolumes { get; set; } = new List<MountVolume>();
        public HookContainer() { } // For Json deserializer
        public HookContainer(ContainerInfo container)
        {
            Image = container.ContainerImage;

            var (modifiedOptions, entrypoint, arguments) = ParseDockerCommand(container.ContainerCreateOptions);

            EntryPointArgs = entrypoint != null ? arguments : new string[] { "-f", "/dev/null" };
            EntryPoint = entrypoint != null ? entrypoint : "tail";
            CreateOptions = modifiedOptions;
            //WorkingDirectory = container.ContainerWorkDirectory;

            if (!string.IsNullOrEmpty(container.RegistryAuthUsername))
            {
                Registry = new ContainerRegistry
                {
                    Username = container.RegistryAuthUsername,
                    Password = container.RegistryAuthPassword,
                    ServerUrl = container.RegistryServer,
                };
            }

            EnvironmentVariables = container.ContainerEnvironmentVariables;
            PortMappings = container.UserPortMappings.Select(p => p.Value).ToList();
            SystemMountVolumes = container.MountVolumes;
            UserMountVolumes = container.UserMountVolumes.Select(p => new MountVolume(p.Value));
        }

        public static (string ModifiedOptions, string Entrypoint, string[] Arguments) ParseDockerCommand(string options)
        {
            // Patterns to match "--entrypoint <value>" and "-- <args>"
            string entrypointPattern = @"--entrypoint\s+([^\s]+)";
            string argsPattern = @"--\s+(.+)$";

            string entrypoint = null;
            string[] arguments = Array.Empty<string>();
            string modifiedOptions = options;

            Match entrypointMatch = Regex.Match(options, entrypointPattern);
            if (entrypointMatch.Success)
            {
                entrypoint = entrypointMatch.Groups[1].Value;
                modifiedOptions = Regex.Replace(modifiedOptions, entrypointPattern, "").Trim();
            }

            Match argsMatch = Regex.Match(options, argsPattern);
            if (argsMatch.Success)
            {
                arguments = argsMatch.Groups[1].Value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                modifiedOptions = Regex.Replace(modifiedOptions, argsPattern, "").Trim();
            }

            return (modifiedOptions, entrypoint, arguments);
        }
    }

    public static class ContainerInfoExtensions
    {
        public static HookContainer GetHookContainer(this ContainerInfo containerInfo)
        {
            return new HookContainer(containerInfo);
        }
    }
}
