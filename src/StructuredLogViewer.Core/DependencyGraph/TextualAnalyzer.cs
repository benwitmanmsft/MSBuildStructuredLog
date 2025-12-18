using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{
    public static class TextualAnalyzer
    {
        public static void Run(Build build)
        {
            var graph = new Graph(build);
            graph.Write(graph.Build.LogFilePath + ".graph.txt");

            var graphStartAnalysis = new StartAnalysis(graph);
            var graphCriticalPathAnalysis = new CriticalPathAnalysis(graph);

            void Simulate(int count, Dictionary<RealProjectEvaluationNode, int> affinities, string fileName)
            {
                StringBuilder simulationSteps = new StringBuilder();
                var graphSimulationAnalysis = new SimulationAnalysis(
                    graph,
                    graphCriticalPathAnalysis,
                    count,
                    affinities,
                    c => simulationSteps.AppendLine($"{c.Time:G}: {c.StepType} {c.Walk.Node.ToPrettyString()} (Remaining: {c.RemainingCriticalPath:G}"));
                File.WriteAllText($"{build.LogFilePath}.simulation.{fileName}.steps.txt", simulationSteps.ToString());

                StringBuilder simulationPathString = new();
                StringBuilder simulationPathAbbreviated = new();
                DependencyAnalysis.PrintCriticalPath(
                    graphSimulationAnalysis.SimulatedPath, "critical", "sim", simulationPathString, simulationPathAbbreviated,
                    baselineThreshold: TimeSpan.FromMilliseconds(500), bestThreshold: TimeSpan.FromMilliseconds(500),
                    canGroup: true, canGroupEvaluations: true, canGroupCopies: true);

                StringBuilder simulationPathSummary = new();
                simulationPathSummary.AppendLine("Path Summary:");
                graphSimulationAnalysis.SimulatedPathStats.WriteSummaries(simulationPathSummary);

                File.WriteAllText($"{build.LogFilePath}.simulation.{fileName}.txt", string.Join(Environment.NewLine,
                    simulationPathSummary.ToString(),
                    simulationPathAbbreviated.ToString()
                ));
            }

            Simulate(graphStartAnalysis.NodeEvaluations.Count, affinities: null, "n");
            Simulate(graphStartAnalysis.NodeEvaluations.Count, graphStartAnalysis.NodeEvaluations.SelectMany(worker => worker.Value.Select(t => new { WorkerId = worker.Key, Eval = t })).ToDictionary(t => t.Eval, t => t.WorkerId), "n.affinity");
            Simulate(graphStartAnalysis.NodeEvaluations.Count * 2, affinities: null, "2n");
            Simulate(graphStartAnalysis.NodeEvaluations.Count * 2, graphStartAnalysis.NodeEvaluations.SelectMany(worker => worker.Value.Select((t, i) => new { WorkerId = worker.Key + (graphStartAnalysis.NodeEvaluations.Count * (i % 2)), Eval = t })).ToDictionary(t => t.Eval, t => t.WorkerId), "2n.affinity");

            StringBuilder criticalPathString = new();
            StringBuilder criticalPathAbbreviated = new();
            DependencyAnalysis.PrintCriticalPath(
                graphCriticalPathAnalysis.CriticalPath, "actual", "critical", criticalPathString, criticalPathAbbreviated,
                    baselineThreshold: TimeSpan.FromMilliseconds(500), bestThreshold: TimeSpan.FromMilliseconds(500),
                    canGroup: true, canGroupEvaluations: true, canGroupCopies: true);

            StringBuilder criticalPathSummary = new();
            criticalPathSummary.AppendLine("Path Summary:");
            graphCriticalPathAnalysis.CriticalPathStats.WriteSummaries(criticalPathSummary);

            StringBuilder everythingSummary = new();
            everythingSummary.AppendLine("Everything Summary:");
            graph.AggregateStats.WriteSummaries(everythingSummary);

            StringBuilder nodeSummary = new();
            graphStartAnalysis.PrintStartAnalysis(nodeSummary);

            File.WriteAllText($"{build.LogFilePath}.criticalpath.txt", string.Join(Environment.NewLine,
                nodeSummary.ToString(),
                everythingSummary.ToString(),
                criticalPathSummary.ToString(),
                criticalPathAbbreviated.ToString()
            ));

            File.WriteAllText($"{build.LogFilePath}.criticalpathtimes.txt", string.Join(Environment.NewLine,
                graphCriticalPathAnalysis.CriticalPaths.Values
                .OrderBy(t => t.CriticalPathTime.Value)
                .Select(t => $"{t.CriticalPathTime.Value:G} {t.Node.ToPrettyString()}")
            ));
        }

    }
}
