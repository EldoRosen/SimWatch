using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SimWatch
{
    public sealed class ReportSection
    {
        public string Title { get; set; }
        public string Body { get; set; }
        public string Status { get; set; } = "completed";   // completed | timed_out | no_response | skipped
        public double DurationSeconds { get; set; }
    }

    /// <summary>Builds the plain-text report and, in parallel, the list of sections used for Discord.</summary>
    public sealed class ReportBuilder
    {
        public StringBuilder Text { get; } = new StringBuilder();
        public List<ReportSection> Sections { get; } = new List<ReportSection>();

        public void Section(string title, string body, string status = "completed", double durationSeconds = 0)
        {
            Text.AppendLine($"===== {title} =====");
            Text.AppendLine(body);
            Text.AppendLine();
            Sections.Add(new ReportSection { Title = title, Body = body, Status = status, DurationSeconds = durationSeconds });
        }

        /// <summary>A remark about the previous section.</summary>
        public void Note(string note)
        {
            Text.AppendLine(note);
            Text.AppendLine();
            if (Sections.Count > 0)
            {
                var last = Sections.Last();
                last.Body = (last.Body ?? "") + Environment.NewLine + note;
            }
        }
    }

    /// <summary>Everything the Discord notifier needs to lay a report out as embeds.</summary>
    public sealed class DiscordReport
    {
        public string Kind { get; set; }      // e.g. "SIM SPEED ALERT"
        public int Color { get; set; }
        public List<KeyValuePair<string, string>> Header { get; } = new List<KeyValuePair<string, string>>();
        public List<ReportSection> Sections { get; } = new List<ReportSection>();
        public List<KeyValuePair<string, string>> Estimate { get; } = new List<KeyValuePair<string, string>>(); // estimated cause, posted after the header
        public string Closing { get; set; }   // text for the final block
        public string FileName { get; set; }
        public string FullText { get; set; }  // attached as a .txt file only if the option is on
    }

    /// <summary>Facts about the latest alert that the Galaxy Offenders bulletin quotes.</summary>
    public sealed class GalaxyContext
    {
        public float SimAvg { get; set; }
        public float SimMin { get; set; }
        public float FinalSpeed { get; set; }
        public int AlertsInWindow { get; set; }
    }
}
