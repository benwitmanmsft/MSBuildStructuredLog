using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

namespace StructuredLogViewer.DependencyGraph
{
    public class GraphCriticalPathAnalysis
    {
        public Graph graph;
        public Dictionary<BaseNode, GraphWalk> CriticalPaths;
        public GraphWalk CriticalPath;

        public GraphCriticalPathAnalysis(Graph graph)
        {
            this.graph = graph;

            List<GraphWalk> discoveryQueue = new();
            Stack<GraphWalk> stack = new();

            var dependencyAnalysis = new GraphDependencyAnalysis(graph, stack.Push);

            int BinarySearchResultIndex(int result) => result < 0 ? ~result : result;

            while (stack.Count > 0 || discoveryQueue.Count > 0)
            {
                if (stack.Count == 0)
                {
                    var current = discoveryQueue[0];
                    discoveryQueue.RemoveAt(0);
                    dependencyAnalysis.MakeDiscoveries(current);
                }

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    Contract.Assert(current.DependenciesRemaining == 0 && current.Discovered == true && !current.CriticalPathTime.HasValue);

                    bool isDiscovery = current.DiscoveredTime.HasValue;
                    TimeSpan? limitingTime = current.DiscoveredTime;
                    GraphWalk limitingWalk = current.DiscoveredBy;

                    foreach (var dependency in current.Node.GetDependencies())
                    {
                        var dependencyWalk = dependencyAnalysis.NodeToWalk[dependency];
                        if (!limitingTime.HasValue || dependencyWalk.CriticalPathTime.Value > limitingTime.Value)
                        {
                            limitingTime = dependencyWalk.CriticalPathTime.Value;
                            limitingWalk = dependencyWalk;
                            isDiscovery = false;
                        }
                    }

                    // target from cache returns zero duration but maybe it actually needed to execute in the new order and that isn't accounted for
                    // CallTarget has some odd behaviors since the targets run during the target performing the CallTarget but are actually nodes after it in the graph
                    //   so in that case those nodes (i.e. the CallTarget's executed targets) have an end time before their depndency's end time
                    current.CriticalPathTime = (limitingTime ?? TimeSpan.Zero) + current.Node.GetDuration();
                    current.CriticalPath = limitingWalk;
                    current.CriticalPathIsDiscovery = isDiscovery;
                    current.BaselineTime = (limitingWalk?.BaselineTime.Value ?? TimeSpan.Zero) + (current.Node.GetEnd() - (limitingWalk?.Node.GetEnd() ?? graph.StartNode.GetEnd()));

                    dependencyAnalysis.SatisfyDependents(current);

                    if (current.Node is MSBuildStartNode startNode)
                    {
                        discoveryQueue.Insert(BinarySearchResultIndex(discoveryQueue.BinarySearch(current.CriticalPathTime.Value, w => w.CriticalPathTime.Value)), current);
                    }
                }
            }

            CriticalPaths = dependencyAnalysis.NodeToWalk;
            CriticalPath = CriticalPaths[graph.EndNode];

            //File.WriteAllText($"{graph.Build.LogFilePath}.criticalpathtimes.txt", string.Join(Environment.NewLine,
            //    CriticalPaths.Values
            //    .OrderBy(t => t.CriticalPathTime.Value)
            //    .Select(t => $"{t.CriticalPathTime.Value:G} {t.Node.ToPrettyString()}")
            //));

            Contract.Assert(CriticalPaths.Values.All(t => t.CriticalPathTime.HasValue));
        }
    }

}
