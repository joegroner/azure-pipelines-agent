using System;
using Newtonsoft.Json.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public sealed class GlobalContext
    {
        public JObject ContainerHookState { get; set; }
    }
}
