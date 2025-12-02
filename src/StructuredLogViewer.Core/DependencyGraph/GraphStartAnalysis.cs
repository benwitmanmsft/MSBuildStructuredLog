using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{

    public class GraphStartAnalysis
    {
        public Graph Graph;
        public TimeSpan TimeToFirstEvaluation;
        public Dictionary<int, List<ProjectEvaluationNode>> NodeEvaluations;
        public Dictionary<int, (TimeSpan StartTime, int Id, ProjectEvaluationNode Node)> NodeFirstEvaluation;

        public GraphStartAnalysis(Graph graph)
        {
            Graph = graph;

            // All evaluations before the first build are assumed to be automatically discovered - i.e. they are /graph
            var (firstBuildTime, firstProject) = graph.Build.Children.OfType<Project>().Min(p => (p.StartTime, p));
            var firstEvaluationTime = firstProject.GetEvaluation(graph.Build).StartTime;
            foreach (var earlyEvaluations in graph.EvaluationNodes.Values.Where(t => t.Evaluation.EndTime < firstBuildTime))
            {
                foreach (var discoveredBy in earlyEvaluations.DiscoveredBy)
                {
                    discoveredBy.Discovered.Remove(earlyEvaluations);
                }

                earlyEvaluations.DiscoveredBy.Clear();
            }

            TimeToFirstEvaluation = firstEvaluationTime - graph.StartNode.CustomStartTime.Value;

            NodeEvaluations = graph.Nodes
                .OfType<ProjectEvaluationNode>()
                .Where(t => t.Evaluation.StartTime >= firstEvaluationTime)
                .GroupBy(t => t.Evaluation.NodeId)
                .ToDictionary(t => t.Key, t => t.ToList());

            NodeFirstEvaluation =
                NodeEvaluations
                .ToDictionary(t => t.Key, t => t.Value.Min(u => (StartTime: u.Evaluation.StartTime - firstEvaluationTime, u.Evaluation.Id, Node: u)));
        }

        public void PrintStartAnalysis(StringBuilder output)
        {
            output.AppendLine($"Time to first non-graph evaluation: {TimeToFirstEvaluation:G}");
            foreach (var node in NodeFirstEvaluation.OrderBy(t => t.Value.StartTime))
            {
                output.AppendLine($"  Node {node.Key}: {node.Value.StartTime:G} {node.Value.Node.ToPrettyString()} ({node.Value.Id})");
            }
            output.AppendLine();

            output.AppendLine($"Total Evaluations Per Node:");
            foreach (var node in NodeEvaluations.OrderBy(t => t.Key))
            {
                output.AppendLine($"  Node {node.Key}: {node.Value.Count()}");
            }

            output.AppendLine();
        }
    }
}
