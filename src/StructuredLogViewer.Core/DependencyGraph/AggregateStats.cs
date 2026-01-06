using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{
    public class AggregateStats
    {
        private Dictionary<string, List<TimeSpan>> TaskNameToDurations = new();
        private Dictionary<string, Dictionary<Target, List<TimeSpan>>> TargetDurations = new();
        private Dictionary<string, Dictionary<Target, List<TimeSpan>>> TargetTaskDurations = new();
        private Dictionary<string, Dictionary<Target, List<TimeSpan>>> EvaluationTargetDurations = new();
        private Dictionary<string, Dictionary<Target, List<TimeSpan>>> NodeTargetDurations = new();
        private Dictionary<string, List<TimeSpan>> EvaluationDurations = new();

        public Dictionary<string, List<TimeSpan>> GetTaskNameToDurations() => TaskNameToDurations;
        public Dictionary<string, List<TimeSpan>> GetTargetDurations() => TargetDurations.ToDictionary(t => t.Key, v => FlattenTargetTimeSpans(v.Value));
        public Dictionary<string, List<TimeSpan>> GetTargetTaskDurations() => TargetTaskDurations.ToDictionary(t => t.Key, v => FlattenTargetTimeSpans(v.Value));
        public Dictionary<string, List<TimeSpan>> GetEvaluationDurations() => EvaluationDurations;
        public Dictionary<string, List<TimeSpan>> GetEvaluationTargetDurations() => EvaluationTargetDurations.ToDictionary(t => t.Key, v => FlattenTargetTimeSpans(v.Value));
        public Dictionary<string, List<TimeSpan>> GetNodeTargetDurations() => NodeTargetDurations.ToDictionary(t => t.Key, v => FlattenTargetTimeSpans(v.Value));

        public void AddNode(BaseNode node)
        {
            if (node is ProjectEvaluationNode evaluationNode)
            {
                EvaluationDurations.GetOrAddNew(evaluationNode.ToPrettyString()).Add(evaluationNode.GetDuration());
            }

            if (node is TargetTaskNode taskNode)
            {
                TimeSpan tasksDuration = TimeSpan.Zero;

                foreach (var task in taskNode.Tasks)
                {
                    TaskNameToDurations.GetOrAddNew(task.Name).Add(task.Duration);

                    tasksDuration += task.Duration;
                }

                TargetDurations.GetOrAddNew(taskNode.Target.Name).GetOrAddNew(taskNode.Target).Add(taskNode.GetDuration());
                TargetTaskDurations.GetOrAddNew(taskNode.Target.Name).GetOrAddNew(taskNode.Target).Add(tasksDuration);
                EvaluationTargetDurations.GetOrAddNew(taskNode.EvaluationNode.PrettyName).GetOrAddNew(taskNode.Target).Add(taskNode.GetDuration());
                NodeTargetDurations.GetOrAddNew(taskNode.EvaluationNode.NodeId.ToString()).GetOrAddNew(taskNode.Target).Add(tasksDuration);
            }
        }

        private void WriteSummary(StringBuilder summaries, string type, Dictionary<string, List<TimeSpan>> keyValuePairs)
        {
            int top = 10;

            var values = keyValuePairs
                .Select(kvp => (Name: kvp.Key, List: kvp.Value.OrderByDescending(t => t).ToList(), Total: kvp.Value.Aggregate(TimeSpan.Zero, (a, b) => a + b)))
                .OrderByDescending(t => t.Total)
                .ToList();

            summaries.AppendLine($"### {type}");

            summaries.AppendLine($"| Name | Duration | Instances | Worst | Median | Best |");
            summaries.AppendLine($"| :- | -: | -: | -: | -: | -: |");
            foreach (var item in values.Take(top))
            {
                summaries.AppendLine($"| `{item.Name.Replace("|", "\\|")}` | {item.Total.TotalSeconds:F3} | {item.List.Count} | {item.List.First().TotalSeconds:F3} | {item.List[item.List.Count / 2].TotalSeconds:F3} | {item.List.Last().TotalSeconds:F3} |");
            }

            var remaining = values.Skip(top);
            if (remaining.Any())
            {
                var remainingTogether = remaining.Aggregate((Total: TimeSpan.Zero, Occurrences: 0), (r, item) => (r.Total + item.Total, r.Occurrences + item.List.Count));
                summaries.AppendLine($"| (and {remaining.Count()} others) | {remainingTogether.Total.TotalSeconds:F3} | {remainingTogether.Occurrences} | | | |");
            }

            summaries.AppendLine($"| All ({keyValuePairs.Keys.Count}) | {values.Aggregate(TimeSpan.Zero, (a, b) => a + b.Total).TotalSeconds:F3} | {values.Sum(t => t.List.Count)} | | | |");

            summaries.AppendLine();
        }

        private List<TimeSpan> FlattenTargetTimeSpans(Dictionary<Target, List<TimeSpan>> dictionary) => dictionary.Values.Select(u => u.Aggregate((a, t) => a + t)).ToList();

        public void WriteSummaries(StringBuilder summaries)
        {
            WriteSummary(summaries, "Target Duration by Target Name", GetTargetDurations());
            WriteSummary(summaries, "Target Duration By Evaluation", GetEvaluationTargetDurations());
            WriteSummary(summaries, "Target Duration By Node", GetNodeTargetDurations());
            WriteSummary(summaries, "Task Duration by Task Name", GetTaskNameToDurations());
            WriteSummary(summaries, "Task Duration By Target Name", GetTargetTaskDurations());
            WriteSummary(summaries, "Evaluation Durations", GetEvaluationDurations());
        }
    }
}
