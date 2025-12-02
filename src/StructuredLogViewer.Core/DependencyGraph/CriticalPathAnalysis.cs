using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{
    public class CriticalPathAnalysis
    {
        public Graph graph;
        public Dictionary<BaseNode, GraphWalk> CriticalPaths;
        public GraphWalk CriticalPath;
        public AggregateStats CriticalPathStats = new();

        public CriticalPathAnalysis(Graph graph)
        {
            this.graph = graph;

            List<GraphWalk> discoveryQueue = new();
            Stack<GraphWalk> stack = new();

            var dependencyAnalysis = new DependencyAnalysis(graph, stack.Push);

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

            foreach(var walk in CriticalPath.Enumerate())
            {
                CriticalPathStats.AddNode(walk.Node);
            }

            var missing = CriticalPaths.Values
                .Where(t => !t.CriticalPathTime.HasValue)
                .ToDictionary(t => t.Node, t => new MissingCriticalPathInfo { Walk = t });

            foreach (var missingInfo in missing.Values)
            {
                missingInfo.DiscoveriesRemaining = missingInfo.Walk.Node.DiscoveredBy
                    .Where(missing.ContainsKey)
                    .Select(t => missing[t])
                    .ToList();

                missingInfo.DependenciesRemaining = missingInfo.Walk.Node.GetDependencies()
                    .Where(missing.ContainsKey)
                    .Select(t => missing[t])
                    .ToList();
            }

            if (missing.Values.Any())
            {
                HashSet<BaseNode> seen = new();
                var current = missing.First().Value;

                while (current != null && seen.Add(current.Walk.Node))
                {
                    Debug.WriteLine($"Missing Critical Path for {current.Walk.Node.ToPrettyString()}");
                    current = current.DependenciesRemaining.Concat(current.DiscoveriesRemaining).FirstOrDefault();
                }

                if (current != null)
                {
                    Debug.WriteLine($"Missing Critical Path for {current.Walk.Node.ToPrettyString()}");
                }
            }

            Contract.Assert(missing.Count == 0);
        }

        class MissingCriticalPathInfo
        {
            public GraphWalk Walk;
            public List<MissingCriticalPathInfo> DependenciesRemaining;
            public List<MissingCriticalPathInfo> DiscoveriesRemaining;
        }

    }

}
