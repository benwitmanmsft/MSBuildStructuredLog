using System;
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
            var graphStartAnalysis = new StartAnalysis(graph);
            var graphCriticalPathAnalysis = new CriticalPathAnalysis(graph);

            StringBuilder simulationSteps = new StringBuilder();
            var graphSimulationAnalysis = new SimulationAnalysis(graph, graphCriticalPathAnalysis, graphStartAnalysis.NodeEvaluations.Count, c => simulationSteps.AppendLine($"{c.Time:G}: {c.StepType} {c.Walk.Node.ToPrettyString()} (Remaining: {c.RemainingCriticalPath:G}"));
            File.WriteAllText($"{build.LogFilePath}.simulationsteps.txt", simulationSteps.ToString());

            StringBuilder criticalPathString = new();
            StringBuilder criticalPathAbbreviated = new();
            DependencyAnalysis.PrintCriticalPath(graphCriticalPathAnalysis.CriticalPath, "actual", "critical", criticalPathString, criticalPathAbbreviated, TimeSpan.FromMilliseconds(100));

            StringBuilder criticalPathSummary = new();
            criticalPathSummary.AppendLine("Path Summary:");
            graphCriticalPathAnalysis.CriticalPathStats.WriteSummaries(criticalPathSummary);

            StringBuilder simulationPathString = new();
            StringBuilder simulationPathAbbreviated = new();
            DependencyAnalysis.PrintCriticalPath(graphSimulationAnalysis.SimulatedPath, "critical", "sim", simulationPathString, simulationPathAbbreviated, TimeSpan.FromMilliseconds(100));

            StringBuilder simulationPathSummary = new();
            simulationPathSummary.AppendLine("Path Summary:");
            graphSimulationAnalysis.SimulatedPathStats.WriteSummaries(simulationPathSummary);

            StringBuilder everythingSummary = new();
            everythingSummary.AppendLine("Everything Summary:");
            graph.AggregateStats.WriteSummaries(everythingSummary);

            StringBuilder nodeSummary = new();
            graphStartAnalysis.PrintStartAnalysis(nodeSummary);

            File.WriteAllText($"{build.LogFilePath}.criticalpath.txt", string.Join(Environment.NewLine,
                nodeSummary.ToString(),
                everythingSummary.ToString(),
                criticalPathSummary.ToString(),
                criticalPathAbbreviated.ToString(),
                simulationPathSummary.ToString(),
                simulationPathAbbreviated.ToString(),
                "---------------------------------",
                criticalPathString.ToString(),
                simulationPathString.ToString()
            ));

            File.WriteAllText($"{build.LogFilePath}.criticalpathtimes.txt", string.Join(Environment.NewLine,
                graphCriticalPathAnalysis.CriticalPaths.Values
                .OrderBy(t => t.CriticalPathTime.Value)
                .Select(t => $"{t.CriticalPathTime.Value:G} {t.Node.ToPrettyString()}")
            ));
        }

    }
}
