using System;
using System.Collections;
using System.Collections.Generic;
using NLog;
using Torch.API;
using Torch.API.Managers;

namespace SimWatch
{
    public enum ProfilerState
    {
        Installed,
        Missing,
        Unknown,   // could not be determined; the commands are simply tried, as before
    }

    public sealed class ProfilerInfo
    {
        public ProfilerState State { get; set; }
        public string Label { get; set; } = "Profiler"; // which plugin was looked for
        public string Name { get; set; }
        public string Version { get; set; }

        /// <summary>Assembly names of the plugins that were found (to help when the Profiler is reported missing).</summary>
        public List<string> Seen { get; } = new List<string>();

        public string Describe()
        {
            switch (State)
            {
                case ProfilerState.Installed: return $"installed ({Name} {Version})";
                case ProfilerState.Missing: return $"NOT FOUND (install and enable the {Label} plugin)";
                default: return "unknown (could not read the plugin list)";
            }
        }
    }

    /// <summary>Looks for a Torch plugin by its assembly/namespace name: the Profiler (`!profile`) and Concealment (`!conceal`).</summary>
    public static class ProfilerDetector
    {
        static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        // The plugins' assembly / namespace names are "Profiler" and "Concealment".
        static bool Matches(string pluginName, string assemblyName, string ns)
        {
            ns = ns ?? "";
            return string.Equals(assemblyName, pluginName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ns, pluginName, StringComparison.OrdinalIgnoreCase) ||
                   ns.StartsWith(pluginName + ".", StringComparison.OrdinalIgnoreCase);
        }

        public static ProfilerInfo Detect(ITorchBase torch) => DetectPlugin(torch, "Profiler");

        public static ProfilerInfo DetectPlugin(ITorchBase torch, string pluginName)
        {
            var info = new ProfilerInfo { State = ProfilerState.Unknown, Label = pluginName };

            // 1) Torch's plugin list (only loaded, enabled plugins are in it)
            try
            {
                var manager = torch.Managers.GetManager<IPluginManager>();
                if (manager is IEnumerable plugins)
                {
                    info.State = ProfilerState.Missing;

                    foreach (var plugin in plugins)
                    {
                        if (plugin == null) continue;

                        var type = plugin.GetType();
                        var assembly = type.Assembly.GetName();
                        info.Seen.Add(assembly.Name);

                        if (Matches(pluginName, assembly.Name, type.Namespace))
                        {
                            info.State = ProfilerState.Installed;
                            info.Name = assembly.Name;
                            info.Version = assembly.Version?.ToString() ?? "?";
                            return info;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, $"SimWatch: could not read Torch's plugin list for the {pluginName} check");
                info.State = ProfilerState.Unknown;
            }

            // 2) Fallback when the plugin list could not be read: is a Profiler assembly loaded in this process?
            //    Finding it proves the plugin is there; not finding it proves nothing, so the state stays Unknown.
            if (info.State == ProfilerState.Unknown)
            {
                try
                {
                    foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var name = loaded.GetName();
                        if (string.Equals(name.Name, pluginName, StringComparison.OrdinalIgnoreCase))
                        {
                            info.State = ProfilerState.Installed;
                            info.Name = name.Name;
                            info.Version = name.Version?.ToString() ?? "?";
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warn(e, $"SimWatch: assembly scan for the {pluginName} plugin failed");
                }
            }

            return info;
        }
    }
}
