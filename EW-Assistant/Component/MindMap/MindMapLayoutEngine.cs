using System;
using System.Collections.Generic;
using System.Linq;

namespace EW_Assistant.Component.MindMap
{
    /// <summary>
    /// 思维导图布局器：负责把树状结构转换为二维坐标，并自动避免同层遮挡。
    /// </summary>
    public static class MindMapLayoutEngine
    {
        public static (IReadOnlyList<MindMapVisualNode> Nodes, IReadOnlyList<MindMapEdge> Edges) Layout(
            MindMapNode root,
            double horizontalGap = 320,
            double verticalGap = 160,
            double marginX = 160,
            double marginY = 160,
            Func<MindMapNode, double> nodeHeightProvider = null,
            Func<MindMapNode, double> verticalGapProvider = null)
        {
            if (root == null) return (Array.Empty<MindMapVisualNode>(), Array.Empty<MindMapEdge>());

            var nodes = new List<MindMapVisualNode>();
            var edges = new List<MindMapEdge>();
            double cursorY = marginY;

            LayoutInternal(root, 0, ref cursorY, nodes, edges, null, horizontalGap, verticalGap, marginX, nodeHeightProvider, verticalGapProvider);

            var minY = nodes.Count > 0 ? nodes.Min(n => n.Y) : marginY;
            if (minY < marginY)
            {
                var offset = marginY - minY;
                foreach (var node in nodes)
                    node.Y += offset;
            }

            double EvalGap(MindMapVisualNode visual)
            {
                var gap = verticalGapProvider?.Invoke(visual.Node) ?? verticalGap;
                return gap < 24 ? 24 : gap;
            }

            var normalized = nodes.ToList();
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < normalized.Count; i++)
                {
                    for (int j = i + 1; j < normalized.Count; j++)
                    {
                        var a = normalized[i];
                        var b = normalized[j];
                        if (a.Depth != b.Depth) continue;
                        if (a.Bounds.IntersectsWith(b.Bounds))
                        {
                            var padding = (EvalGap(a) + EvalGap(b)) / 2.0;
                            if (a.Y <= b.Y)
                                b.Y = a.Y + a.Height + padding;
                            else
                                a.Y = b.Y + b.Height + padding;
                            changed = true;
                        }
                    }
                }
            } while (changed);

            return (normalized, edges);
        }

        private static MindMapVisualNode LayoutInternal(
            MindMapNode node,
            int depth,
            ref double cursorY,
            List<MindMapVisualNode> nodes,
            List<MindMapEdge> edges,
            MindMapVisualNode parent,
            double hGap,
            double vGap,
            double marginX,
            Func<MindMapNode, double> nodeHeightProvider,
            Func<MindMapNode, double> gapProvider)
        {
            var visual = new MindMapVisualNode(node, depth);
            if (nodeHeightProvider != null)
            {
                var height = nodeHeightProvider(node);
                if (height > 0)
                    visual.Height = height;
            }
            visual.X = parent == null ? marginX : parent.RightX + hGap;

            double GapFor(MindMapNode target)
            {
                var value = gapProvider?.Invoke(target) ?? vGap;
                return value < 24 ? 24 : value;
            }

            nodes.Add(visual);

            var visibleChildren = node.IsExpanded
                ? node.Children?.ToList() ?? new List<MindMapNode>()
                : new List<MindMapNode>();

            double y;
            if (visibleChildren.Count == 0)
            {
                y = cursorY;
                cursorY += visual.Height + GapFor(node);
            }
            else
            {
                double subtreeTop = double.MaxValue;
                double subtreeBottom = double.MinValue;

                foreach (var child in visibleChildren)
                {
                    var childVisual = LayoutInternal(child, depth + 1, ref cursorY, nodes, edges, visual, hGap, vGap, marginX, nodeHeightProvider, gapProvider);
                    subtreeTop = Math.Min(subtreeTop, childVisual.Y);
                    subtreeBottom = Math.Max(subtreeBottom, childVisual.Y + childVisual.Height);
                }

                var center = (subtreeTop + subtreeBottom) / 2.0;
                y = center - visual.Height / 2.0;
            }

            visual.Y = y;
            if (parent != null)
                edges.Add(new MindMapEdge(parent, visual));

            return visual;
        }
    }
}
