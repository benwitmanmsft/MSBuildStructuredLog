using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.Msagl.Drawing;

namespace StructuredLogViewer.DependencyGraph
{
    public class NodeActivity
    {
        public Graph graph;
        public Dictionary<int, List<(TimeSpan Start, TimeSpan End, BaseNode First, BaseNode Last)>> Timelines = new();

        public NodeActivity(Graph graph, DateTime absoluteStart)
        {
            this.graph = graph;

            var byNodes = graph.Nodes.Where(t => !t.IsSentinel).GroupBy(t => t.TheEvaluation.NodeId).ToDictionary(t => t.Key, t => t.OrderBy(u => u.GetEnd()).ToList());

            foreach (var byNode in byNodes)
            {
                Timelines[byNode.Key] = new();

                DateTime? start = null;
                BaseNode first = null;
                BaseNode last = null;
                void Flush()
                {
                    if (last != null)
                    {
                        Timelines[byNode.Key].Add(new(start.Value - absoluteStart, last.GetEnd() - absoluteStart, first, last));
                        last = null;
                        start = null;
                    }
                }

                foreach (var node in byNode.Value)
                {
                    if (node.TheEvaluation != last?.TheEvaluation)
                    {
                        Flush();

                        start = node.GetEnd() - node.GetDuration();
                        first = node;
                    }

                    last = node;
                }

                Flush();
            }
        }
    }
}
