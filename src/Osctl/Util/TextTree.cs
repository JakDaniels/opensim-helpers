// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Util;

/// <summary>One entry in a <see cref="TextTree"/>: a label, an optional note shown beside it, and its children.</summary>
public sealed class TreeNode(string label, string? note = null)
{
    public string Label { get; } = label;

    public string? Note { get; set; } = note;

    public List<TreeNode> Children { get; } = [];

    public TreeNode Add(string label, string? note = null)
    {
        var child = new TreeNode(label, note);
        Children.Add(child);
        return child;
    }

    public TreeNode Add(TreeNode child)
    {
        Children.Add(child);
        return child;
    }
}

/// <summary>Draws a tree the way <c>tree</c> does (├── └── │), with the notes lined up in a column.</summary>
public static class TextTree
{
    /// <summary>Notes start no further right than this, so one long label does not push them all off screen.</summary>
    private const int MaxNoteColumn = 44;

    public static List<string> Render(TreeNode root)
    {
        var rows = new List<(string Text, string? Note)> { (root.Label, root.Note) };
        Walk(root, "", rows);
        var column = Math.Min(MaxNoteColumn, rows.Where(r => r.Note is not null).Select(r => r.Text.Length).DefaultIfEmpty(0).Max());
        return rows.Select(r => r.Note is null ? r.Text : $"{r.Text.PadRight(column)}  {r.Note}").ToList();
    }

    private static void Walk(TreeNode node, string indent, List<(string, string?)> rows)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var last = i == node.Children.Count - 1;
            rows.Add((indent + (last ? "└── " : "├── ") + child.Label, child.Note));
            Walk(child, indent + (last ? "    " : "│   "), rows);
        }
    }
}
