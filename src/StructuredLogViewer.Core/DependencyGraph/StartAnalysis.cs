using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{
    public class StartAnalysis
    {
        public Graph Graph;
        public TimeSpan TimeToFirstUsedEvaluation;
        public Dictionary<int, List<RealProjectEvaluationNode>> NodeEvaluations;
        public HashSet<RealProjectEvaluationNode> UsedEvaluations;
        public Dictionary<int, (TimeSpan StartOffset, int Id, RealProjectEvaluationNode Node)> NodeFirstEvaluation;
        public Dictionary<int, (TimeSpan StartOffset, int Id, RealProjectEvaluationNode Node)> NodeFirstUsedEvaluation;

        public StartAnalysis(Graph graph)
        {
            Graph = graph;

            NodeEvaluations = graph.EvaluationNodes.Values
                .GroupBy(t => t.Evaluation.NodeId)
                .ToDictionary(t => t.Key, t => t.ToList());

            UsedEvaluations = new(
                graph.Nodes
                    .Where(t => t is TargetBaseNode)
                    .Select(t => t.TheEvaluation)
                    .OfType<RealProjectEvaluationNode>()
            );

            NodeFirstEvaluation =
                NodeEvaluations
                .ToDictionary(
                    t => t.Key,
                    t => t.Value
                        .Min(u => (StartOffset: u.Evaluation.StartTime - graph.StartNode.CustomStartTime.Value, u.Evaluation.Id, Node: u)));

            NodeFirstUsedEvaluation =
                NodeEvaluations
                .ToDictionary(
                    t => t.Key,
                    t => t.Value
                        .Where(u => UsedEvaluations.Contains(u.TheEvaluation))
                        .Select(u => (StartOffset: u.Evaluation.StartTime - graph.StartNode.CustomStartTime.Value, u.Evaluation.Id, Node: u))
                        .DefaultIfEmpty()
                        .Min());

            TimeToFirstUsedEvaluation = NodeFirstUsedEvaluation.Values.Min(t => t.StartOffset);
        }

        public void PrintStartAnalysis(StringBuilder output)
        {
            var bestStartInfo = Graph.Nodes
                .Where(t => t is not ProjectEvaluationNode)
                .Where(t => t.TheEvaluation != null)
                .Select(Node => (Start: Node.GetEnd() - Graph.StartNode.CustomStartTime.Value - Node.GetDuration(), Node))
                .Select((t, Index) => (Time: t.Start - t.Node.TheEvaluation.GetDuration(), Index, t.Start, t.Node))
                .Min();

            var bestStart = bestStartInfo.Time;

            output.AppendLine($"Relative will be relative to the first build ({bestStartInfo.Node.ToPrettyString()}) which started at {bestStartInfo.Start.TotalSeconds:F3} but back based on its evaluation to {bestStart.TotalSeconds:F3}");

            output.AppendLine($"### Evaluations Per Node");
            output.AppendLine($"| Node | Count | First | Relative | Evaluation Id | First Evaluation");
            output.AppendLine($"| -: | -: | -: | -: | -: | :- |");
            foreach (var node in NodeEvaluations.OrderBy(t => NodeFirstEvaluation[t.Key].StartOffset))
            {
                var first = NodeFirstEvaluation[node.Key].StartOffset.TotalSeconds;
                var relative = Math.Max(0, first - bestStart.TotalSeconds);

                output.AppendLine($"| {node.Key} | {node.Value.Count} | {first:F3} | {relative:F3} | {NodeFirstEvaluation[node.Key].Id} | {NodeFirstEvaluation[node.Key].Node.PrettyName} |");
            }
            output.AppendLine();

            output.AppendLine($"### Used Evaluations Per Node");
            output.AppendLine($"| Node | Count | First | Relative | Evaluation Id | First Evaluation");
            output.AppendLine($"| -: | -: | -: | -: | -: | :- |");
            foreach (var node in NodeEvaluations.OrderBy(t => NodeFirstUsedEvaluation[t.Key].StartOffset))
            {
                var used = node.Value.Where(t => UsedEvaluations.Contains(t.TheEvaluation)).ToList();

                if (used.Count == 0)
                {
                    continue;
                }

                var first = NodeFirstUsedEvaluation[node.Key].StartOffset.TotalSeconds;
                var relative = Math.Max(0, first - bestStart.TotalSeconds);

                output.AppendLine($"| {node.Key} | {used.Count} | {first:F3} | {relative:F3} | {NodeFirstUsedEvaluation[node.Key].Id} | {NodeFirstUsedEvaluation[node.Key].Node.PrettyName} |");
            }
            output.AppendLine();

            output.AppendLine();
        }
    }
}
