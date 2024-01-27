using System;
using System.Collections.ObjectModel;
using Agent.Sdk.Knob;

namespace Microsoft.VisualStudio.Services.Agent.Util
{
    public static class NodeUtil
    {
        private const string _defaultNodeVersion = "node10";
        public static string GetInternalNodeVersion(IKnobValueContext context)
        {
            bool useNode10 = AgentKnobs.UseNode10.GetValue(context).AsBoolean();
            bool useNode20_1 = AgentKnobs.UseNode20_1.GetValue(context).AsBoolean();
            string useNodeKnob = AgentKnobs.UseNode.GetValue(context).AsString();

            if(useNode10) return "node10";
            if(useNode20_1) return "node20_1";
            if(useNodeKnob.ToUpper() == "LTS") return "node16";   

            return _defaultNodeVersion;
        }
    }
}
