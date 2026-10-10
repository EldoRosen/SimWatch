using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimWatch
{
    public enum WaitResult
    {
        Completed,
        TimedOut,
        NoResponse,
    }

    /// <summary>
    /// Collects everything a command responds with. Profiler commands respond immediately
    /// ("Started profiling...") and again when done ("Finished profiling..."), possibly from another thread.
    /// </summary>
    public sealed class CommandCapture
    {
        static readonly TimeSpan NoResponseAfter = TimeSpan.FromSeconds(10);
        static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
        static readonly TimeSpan MarkerSettle = TimeSpan.FromMilliseconds(1500); // results may still arrive in messages after the marker

        readonly object _lock = new object();
        readonly StringBuilder _text = new StringBuilder();
        readonly string _marker;
        int _messageCount;
        bool _markerSeen;
        DateTime _lastMessageUtc = DateTime.UtcNow;

        public CommandCapture(string completionMarker)
        {
            _marker = completionMarker;
        }

        // Takes object so it works whether Torch's response callback hands over a string or a chat message object.
        public void Add(object response)
        {
            var text = Stringify(response);
            if (string.IsNullOrWhiteSpace(text)) return;

            lock (_lock)
            {
                _text.AppendLine(text.TrimEnd());
                _messageCount++;
                _lastMessageUtc = DateTime.UtcNow;

                if (!string.IsNullOrEmpty(_marker) &&
                    text.IndexOf(_marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _markerSeen = true;
                }
            }
        }

        public string GetText()
        {
            lock (_lock) return _text.ToString().TrimEnd();
        }

        /// <param name="untilMarker">
        /// true: done when the completion marker shows up (profile commands).
        /// false: done once the output has been quiet for a few seconds (any other command).
        /// </param>
        public async Task<WaitResult> WaitAsync(bool untilMarker, TimeSpan timeout, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                await Task.Delay(250, ct);

                lock (_lock)
                {
                    if (_messageCount == 0)
                    {
                        if (sw.Elapsed >= NoResponseAfter) return WaitResult.NoResponse;
                        continue;
                    }

                    var done = untilMarker
                        ? _markerSeen && DateTime.UtcNow - _lastMessageUtc >= MarkerSettle
                        : DateTime.UtcNow - _lastMessageUtc >= QuietPeriod;
                    if (done) return WaitResult.Completed;
                }
            }

            lock (_lock)
            {
                if (untilMarker && _markerSeen) return WaitResult.Completed; // marker arrived, only the settle time ran out
            }

            return WaitResult.TimedOut;
        }

        static string Stringify(object response)
        {
            if (response == null) return null;
            if (response is string s) return s;

            // TorchChatMessage keeps its text in a member called Message, which may be a field or a property
            // depending on the Torch version, so look for both.
            var type = response.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            if (type.GetProperty("Message", flags)?.GetValue(response, null) is string fromProperty) return fromProperty;
            if (type.GetField("Message", flags)?.GetValue(response) is string fromField) return fromField;

            // Unknown shape: use every public text member, so the report is never just a type name.
            var parts = new List<string>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length == 0 &&
                    property.PropertyType == typeof(string) &&
                    property.GetValue(response, null) is string text && text.Length > 0) parts.Add(text);
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(string) && field.GetValue(response) is string text && text.Length > 0) parts.Add(text);
            }

            return parts.Count > 0 ? string.Join(" ", parts) : response.ToString();
        }
    }
}
