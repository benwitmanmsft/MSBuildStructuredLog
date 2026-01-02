using System.Text;

namespace Microsoft.Build.Logging.StructuredLogger
{
    public class StringWriter
    {
        public static int MaxStringLength = 100_000_000;

        public static string GetString(BaseNode rootNode, bool visibleOnly = false)
        {
            var sb = new StringBuilder();

            WriteNode(rootNode, sb, indent: 0, visibleOnly);

            return sb.ToString();
        }

        private static void WriteNode(BaseNode node, StringBuilder sb, int indent, bool visibleOnly)
        {
            if (node == null)
            {
                return;
            }

            if (sb.Length > MaxStringLength)
            {
                return;
            }

            Indent(sb, indent);

            var text = node.GetFullText();

            sb.AppendLine(text);

            if (node is TreeNode { HasChildren: true } treeNode)
            {
                // Only recurse into children if we're not in visibleOnly mode, or if we are and the node is expanded
                if (!visibleOnly || treeNode.IsExpanded)
                {
                    foreach (var child in treeNode.Children)
                    {
                        WriteNode(child, sb, indent + 1, visibleOnly);
                    }
                }
            }
        }

        public static string GetParentString(BaseNode leafNode)
        {
            var sb = new StringBuilder();

            WriteParent(leafNode, sb);

            return sb.ToString();
        }

        private static int WriteParent(BaseNode node, StringBuilder sb)
        {
            if (node == null)
            {
                return 0;
            }

            if (sb.Length > MaxStringLength)
            {
                return 0;
            }

            var indent = WriteParent(node.Parent, sb);

            Indent(sb, indent);

            var text = node.GetFullText();

            sb.AppendLine(text);

            return indent + 1;
        }

        private static void Indent(StringBuilder sb, int indent)
        {
            sb.Append(' ', indent * 4);
        }
    }
}
