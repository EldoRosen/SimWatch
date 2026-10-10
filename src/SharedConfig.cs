using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace SimWatch
{
    public sealed class SharePullResult
    {
        public bool Ok;
        public string Error;
        public int Changed;
        public List<string> ChangedNames = new List<string>();
        public string PublishedBy = "?";
        public string PublishedUtc = "?";
        public bool Retry;   // failed in a way that may pass by itself (file busy), so try again at the next tick
    }

    /// <summary>
    /// Shares the settings between several servers through one small XML file on a path they can all reach (network share,
    /// synced folder). One server publishes, the others follow. Local-only settings never travel.
    /// </summary>
    public static class SharedConfig
    {
        public const string DefaultFileName = "SimWatch.shared.cfg";

        // differ per server, or control the sharing itself
        static readonly HashSet<string> LocalOnly = new HashSet<string>
        {
            nameof(SimWatchConfig.SectorName),
            nameof(SimWatchConfig.SharedConfigMode),
            nameof(SimWatchConfig.SharedConfigPath),
            nameof(SimWatchConfig.SharedIncludeDiscord),
        };

        // secrets, shared only when "Share Discord settings" is on (on both sides)
        static readonly HashSet<string> DiscordSettings = new HashSet<string>
        {
            nameof(SimWatchConfig.DiscordWebhookUrl),
            nameof(SimWatchConfig.GalaxyWebhookUrl),
            nameof(SimWatchConfig.DiscordPingRoleId),
        };

        /// <summary>The configured path is either the file itself or a folder; a folder gets the default file name.</summary>
        public static string ResolvePath(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured)) return null;

            try
            {
                var path = configured.Trim().Trim('"');
                var isFolder = path.EndsWith("\\") || path.EndsWith("/") || Directory.Exists(path) || string.IsNullOrEmpty(Path.GetExtension(path));
                return isFolder ? Path.Combine(path, DefaultFileName) : path;
            }
            catch (ArgumentException)
            {
                return null; // illegal characters in the path (.NET Framework throws for them)
            }
        }

        /// <summary>Changes whenever the file is rewritten.</summary>
        public static string Signature(string path)
        {
            var file = new FileInfo(path);
            return file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + file.Length.ToString(CultureInfo.InvariantCulture);
        }

        static IEnumerable<PropertyInfo> SharedProperties(SimWatchConfig config)
        {
            foreach (var property in typeof(SimWatchConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite) continue;
                if (property.GetCustomAttribute<XmlIgnoreAttribute>() != null) continue;
                if (LocalOnly.Contains(property.Name)) continue;
                if (!config.SharedIncludeDiscord && DiscordSettings.Contains(property.Name)) continue;

                var type = property.PropertyType;
                if (type == typeof(bool) || type == typeof(int) || type == typeof(float) || type == typeof(string) || type == typeof(List<string>))
                {
                    yield return property;
                }
            }
        }

        public static bool IsShared(SimWatchConfig config, string propertyName)
        {
            return SharedProperties(config).Any(p => p.Name == propertyName);
        }

        static string Format(object value)
        {
            switch (value)
            {
                case bool b: return b ? "true" : "false";
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                default: return value as string ?? "";
            }
        }

        static object Parse(Type type, string text)
        {
            text = text ?? "";
            if (type == typeof(bool)) return bool.Parse(text.Trim());
            if (type == typeof(int)) return int.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            return text;
        }

        static bool Same(object current, object incoming)
        {
            if (current is List<string> a && incoming is List<string> b) return a.SequenceEqual(b);
            return Equals(current, incoming);
        }

        static XDocument Build(SimWatchConfig config)
        {
            var version = typeof(SharedConfig).Assembly.GetName().Version?.ToString(3) ?? "?";

            var root = new XElement("SimWatchShared");
            root.Add(new XElement("Meta",
                new XAttribute("publishedBy", Environment.MachineName),
                new XAttribute("publishedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
                new XAttribute("plugin", version)));

            foreach (var property in SharedProperties(config).OrderBy(p => p.Name))
            {
                var value = property.GetValue(config);
                if (value is List<string> list)
                {
                    root.Add(new XElement("List", new XAttribute("name", property.Name), list.Select(item => new XElement("Item", item))));
                }
                else
                {
                    root.Add(new XElement("Setting", new XAttribute("name", property.Name), Format(value)));
                }
            }

            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        /// <returns>The file that was written, or null with <paramref name="error"/> set.</returns>
        public static string Publish(SimWatchConfig config, out string error)
        {
            error = null;

            var path = ResolvePath(config.SharedConfigPath);
            if (path == null)
            {
                error = "no shared path set";
                return null;
            }

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // write next to the target and swap, so a following server never reads a half-written file
                var temp = path + ".tmp";
                Build(config).Save(temp);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);

                return path;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        /// <summary>Applies the shared settings that differ from the local ones; unknown or local-only entries are ignored.</summary>
        public static SharePullResult Pull(SimWatchConfig config, string path)
        {
            var result = new SharePullResult();
            XDocument document = null;

            for (var attempt = 0; attempt < 3 && document == null; attempt++)
            {
                try
                {
                    document = XDocument.Load(path);
                }
                catch (IOException)
                {
                    Thread.Sleep(200); // being replaced by the publishing server
                }
                catch (Exception e)
                {
                    result.Error = "unreadable: " + e.Message;
                    return result;
                }
            }

            if (document == null)
            {
                result.Error = "the file is busy";
                result.Retry = true;
                return result;
            }

            var root = document.Root;
            if (root == null || root.Name != "SimWatchShared")
            {
                result.Error = "not a SimWatch shared config file";
                return result;
            }

            var meta = root.Element("Meta");
            if (meta != null)
            {
                result.PublishedBy = (string)meta.Attribute("publishedBy") ?? "?";
                if (DateTime.TryParse((string)meta.Attribute("publishedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var published))
                {
                    result.PublishedUtc = published.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
            }

            var allowed = SharedProperties(config).ToDictionary(p => p.Name);

            foreach (var element in root.Elements().Where(e => e.Name == "Setting" || e.Name == "List"))
            {
                var name = (string)element.Attribute("name");
                if (name == null || !allowed.TryGetValue(name, out var property)) continue;

                try
                {
                    var incoming = property.PropertyType == typeof(List<string>)
                        ? element.Elements("Item").Select(i => i.Value).ToList()
                        : Parse(property.PropertyType, element.Value);

                    if (Same(property.GetValue(config), incoming)) continue;

                    property.SetValue(config, incoming);
                    result.Changed++;
                    result.ChangedNames.Add(name);
                }
                catch (Exception)
                {
                    // a value this version can't read is skipped; the local value stays
                }
            }

            result.Ok = true;
            return result;
        }
    }
}
