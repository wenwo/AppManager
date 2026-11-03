using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AppManager
{
    /// <summary>
    /// 轻量 Markdown → RTF 转换，供文本查看器的 Markdown 预览使用。
    /// 覆盖标题、段落、强调、行内代码、代码块、引用、有序/无序/任务列表、表格、分隔线、链接与图片占位；
    /// 不支持的语法按原文显示，不会丢失内容。
    /// </summary>
    internal static class MarkdownRtf
    {
        // 颜色表下标（与 Header 中 \colortbl 顺序一致）
        private const int ColorText = 1, ColorLink = 2, ColorMuted = 3, ColorCodeBg = 4,
            ColorBorder = 5, ColorQuoteBar = 6, ColorHeaderBg = 7, ColorPaper = 8;
        private const int BodySize = 21;       // 半磅：10.5pt
        private const int CodeSize = 19;
        private static readonly int[] HeadingSizes = { 34, 29, 25, 22, 21, 20 };

        private static readonly Regex Fence = new Regex(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex Rule = new Regex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex SetextH1 = new Regex(@"^ {0,3}=+[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex SetextH2 = new Regex(@"^ {0,3}-+[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex Quote = new Regex(@"^ {0,3}> ?", RegexOptions.Compiled);
        private static readonly Regex ListItem = new Regex(@"^([ \t]*)([-*+]|\d{1,9}[.)])(?:[ \t]+(.*))?$", RegexOptions.Compiled);
        private static readonly Regex TaskBox = new Regex(@"^\[([ xX])\][ \t]+", RegexOptions.Compiled);
        private static readonly Regex TableDelimiter = new Regex(@"^[ \t]*\|?[ \t]*:?-+:?[ \t]*(?:\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex HtmlComment = new Regex(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex LineBreakTag = new Regex(@"\G<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AutoLink = new Regex(@"\G<((?:https?|ftp|mailto):[^\s<>]+)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BareUrl = new Regex(@"\Ghttps?://[^\s<>()\[\]]+[^\s<>()\[\].,;:!?'""，。；：！？）】]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>把 Markdown 文本转换为 RTF；widthTwips 为正文可用宽度，用于表格与代码块的列宽。</summary>
        public static string Render(string markdown, int widthTwips)
        {
            return Render(markdown, widthTwips, System.Threading.CancellationToken.None);
        }

        /// <summary>同上；后台渲染时传入取消令牌，切换文件后不必等旧文件渲染完。</summary>
        public static string Render(string markdown, int widthTwips, System.Threading.CancellationToken cancellation)
        {
            string text = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (text.IndexOf("<!--", StringComparison.Ordinal) >= 0) text = HtmlComment.Replace(text, string.Empty);
            Renderer renderer = new Renderer(Math.Max(widthTwips, 2400), text.Length, cancellation);
            renderer.Blocks(new List<string>(text.Split('\n')), 0);
            return renderer.Finish();
        }

        // 嵌套与长度上限：引用 “>>>…” 或链接 “[[[…](u)](u)…” 每层都会递归，构造文件能把调用栈压到上万层，
        // StackOverflowException 无法捕获会直接结束进程。超出上限的部分按纯文本输出。
        internal const int MaxBlockDepth = 32;
        internal const int MaxInlineDepth = 32;
        // 未闭合的 “[”“*” 会让每个起始符都扫描到行尾（O(n²)）；超长段落只转义不解析行内格式。
        internal const int MaxInlineLength = 8192;

        private sealed class Renderer
        {
            private readonly StringBuilder rtf;
            private readonly int width;
            private readonly System.Threading.CancellationToken cancellation;
            private int blockDepth;
            private int inlineDepth;
            // RichEdit 不绘制段落边框：引用块的竖线用单元格左边框实现，段落逐行放入同一列
            private bool inQuote;
            private int quoteLeft;

            public Renderer(int width, int length, System.Threading.CancellationToken cancellation)
            {
                this.width = width;
                this.cancellation = cancellation;
                rtf = new StringBuilder(length * 2 + 1024);
                rtf.Append(@"{\rtf1\ansi\ansicpg936\deff0\uc1");
                rtf.Append(@"{\fonttbl{\f0\fswiss\fcharset134 ").Append(Theme.FontName)
                    .Append(@";}{\f1\fmodern\fcharset0 Consolas;}}");
                rtf.Append(@"{\colortbl ;")
                    .Append(Color(32, 33, 36)).Append(Color(9, 88, 217)).Append(Color(95, 99, 104))
                    .Append(Color(246, 248, 250)).Append(Color(218, 220, 224)).Append(Color(208, 215, 222))
                    .Append(Color(242, 244, 247)).Append(Color(255, 255, 255)).Append('}');
                rtf.Append(@"\viewkind4\f0\fs").Append(BodySize).Append(@"\cf1").Append('\n');
            }

            private static string Color(int r, int g, int b)
            {
                return @"\red" + r + @"\green" + g + @"\blue" + b + ";";
            }

            public string Finish()
            {
                rtf.Append('}');
                return rtf.ToString();
            }

            // ===== 块级 =====

            public void Blocks(List<string> lines, int indent)
            {
                List<string> paragraph = new List<string>();
                ListState list = new ListState();
                int i = 0;
                while (i < lines.Count)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string line = lines[i];
                    if (line.Trim().Length == 0)
                    {
                        Paragraph(paragraph, indent);
                        list.AfterBlank = list.Active;
                        i++;
                        continue;
                    }
                    Match fence = Fence.Match(line);
                    if (fence.Success)
                    {
                        Paragraph(paragraph, indent);
                        list.Reset();
                        string marker = fence.Groups[1].Value;
                        List<string> code = new List<string>();
                        i++;
                        while (i < lines.Count && !IsFenceClose(lines[i], marker)) code.Add(lines[i++]);
                        if (i < lines.Count) i++;
                        CodeBlock(code, indent);
                        continue;
                    }
                    if (paragraph.Count > 0 && !list.Active && (SetextH1.IsMatch(line) || SetextH2.IsMatch(line)))
                    {
                        HeadingBlock(SetextH1.IsMatch(line) ? 1 : 2, JoinLines(paragraph), indent);
                        paragraph.Clear();
                        i++;
                        continue;
                    }
                    Match heading = Heading.Match(line);
                    if (heading.Success)
                    {
                        Paragraph(paragraph, indent);
                        list.Reset();
                        HeadingBlock(heading.Groups[1].Length, heading.Groups[2].Value, indent);
                        i++;
                        continue;
                    }
                    if (Rule.IsMatch(line))
                    {
                        Paragraph(paragraph, indent);
                        list.Reset();
                        RuleBlock(indent);
                        i++;
                        continue;
                    }
                    if (Quote.IsMatch(line))
                    {
                        Paragraph(paragraph, indent);
                        list.Reset();
                        List<string> inner = new List<string>();
                        while (i < lines.Count && Quote.IsMatch(lines[i]))
                            inner.Add(Quote.Replace(lines[i++], string.Empty, 1));
                        if (blockDepth >= MaxBlockDepth)
                        {
                            // 嵌套过深：剩余内容（含其中的 “>”）按普通段落原样显示，不再递归
                            Paragraph(inner, indent);
                            continue;
                        }
                        bool outer = !inQuote;
                        if (outer) { inQuote = true; quoteLeft = indent; }
                        blockDepth++;
                        try { Blocks(inner, outer ? indent : indent + 300); }
                        finally { blockDepth--; }
                        if (outer) inQuote = false;
                        continue;
                    }
                    if (line.IndexOf('|') >= 0 && i + 1 < lines.Count && TableDelimiter.IsMatch(lines[i + 1])
                        && lines[i + 1].IndexOf('-') >= 0 && (lines[i + 1].IndexOf('|') >= 0 || line.Trim().StartsWith("|")))
                    {
                        Paragraph(paragraph, indent);
                        list.Reset();
                        List<string> rows = new List<string>();
                        rows.Add(line);
                        string delimiter = lines[i + 1];
                        i += 2;
                        while (i < lines.Count && lines[i].Trim().Length > 0 && lines[i].IndexOf('|') >= 0) rows.Add(lines[i++]);
                        Table(rows, delimiter, indent);
                        continue;
                    }
                    Match item = ListItem.Match(line);
                    if (item.Success && (paragraph.Count == 0 || list.Active || item.Groups[3].Value.Length > 0))
                    {
                        Paragraph(paragraph, indent);
                        int column = Columns(item.Groups[1].Value);
                        string text = item.Groups[3].Value;
                        i++;
                        // 紧随其后的非空、非块级行是同一列表项的软换行
                        while (i < lines.Count && lines[i].Trim().Length > 0 && !StartsBlock(lines[i]))
                            text += Joiner(text, lines[i].Trim()) + lines[i++].Trim();
                        ListBlock(list, column, item.Groups[2].Value, text, indent);
                        continue;
                    }
                    if (list.Active && list.AfterBlank && Columns(LeadingWhitespace(line)) >= 2)
                    {
                        // 列表项内空行后的缩进段落：沿用列表缩进
                        List<string> lazy = new List<string>();
                        while (i < lines.Count && lines[i].Trim().Length > 0 && !StartsBlock(lines[i])) lazy.Add(lines[i++]);
                        Paragraph(lazy, indent + list.TextIndent);
                        continue;
                    }
                    if (paragraph.Count == 0 && Columns(LeadingWhitespace(line)) >= 4)
                    {
                        list.Reset();
                        List<string> code = new List<string>();
                        while (i < lines.Count && (lines[i].Trim().Length == 0 || Columns(LeadingWhitespace(lines[i])) >= 4))
                            code.Add(StripColumns(lines[i++], 4));
                        while (code.Count > 0 && code[code.Count - 1].Trim().Length == 0) code.RemoveAt(code.Count - 1);
                        CodeBlock(code, indent);
                        continue;
                    }
                    if (paragraph.Count == 0) list.Reset();
                    paragraph.Add(line);
                    i++;
                }
                Paragraph(paragraph, indent);
            }

            private static bool StartsBlock(string line)
            {
                return Fence.IsMatch(line) || Heading.IsMatch(line) || Rule.IsMatch(line)
                    || Quote.IsMatch(line) || ListItem.IsMatch(line);
            }

            private static bool IsFenceClose(string line, string marker)
            {
                string t = line.Trim();
                if (t.Length < marker.Length || Columns(LeadingWhitespace(line)) > 3) return false;
                for (int k = 0; k < t.Length; k++) if (t[k] != marker[0]) return false;
                return true;
            }

            private void Paragraph(List<string> lines, int indent)
            {
                if (lines.Count == 0) return;
                StartParagraph(indent, 0, 0, 140);
                for (int k = 0; k < lines.Count; k++)
                {
                    string line = lines[k];
                    bool hardBreak = line.EndsWith("  ") || (line.EndsWith("\\") && !line.EndsWith("\\\\"));
                    string trimmed = line.Trim();
                    if (hardBreak && trimmed.EndsWith("\\")) trimmed = trimmed.Substring(0, trimmed.Length - 1);
                    Inline(trimmed);
                    if (k < lines.Count - 1)
                    {
                        if (hardBreak) rtf.Append(@"\line ");
                        else if (NeedsSpace(trimmed, lines[k + 1].Trim())) rtf.Append(' ');
                    }
                }
                EndParagraph();
                lines.Clear();
            }

            /// <summary>开始一个正文段落，返回段落实际左缩进（引用块内相对单元格）。</summary>
            private int StartParagraph(int indent, int firstIndent, int spaceBefore, int spaceAfter)
            {
                int left = indent;
                if (inQuote)
                {
                    rtf.Append(@"\pard\plain\trowd\trgaph0\trleft").Append(quoteLeft)
                        .Append(@"\trpaddl260\trpaddfl3\clbrdrl\brdrs\brdrw45\brdrcf").Append(ColorQuoteBar);
                    HiddenBorders(true, true, true);
                    rtf.Append(@"\cellx").Append(width).Append(@"\pard\intbl");
                    left = Math.Max(0, indent - quoteLeft);
                }
                else rtf.Append(@"\pard");
                rtf.Append(@"\plain\f0\fs").Append(BodySize).Append(@"\cf").Append(inQuote ? ColorMuted : ColorText)
                    .Append(@"\li").Append(left).Append(@"\fi").Append(firstIndent)
                    .Append(@"\sb").Append(spaceBefore).Append(@"\sa").Append(spaceAfter).Append(' ');
                return left;
            }

            private void EndParagraph()
            {
                rtf.Append(inQuote ? @"\cell\row" : @"\par").Append('\n');
            }

            private void HeadingBlock(int level, string text, int indent)
            {
                level = Math.Min(Math.Max(level, 1), 6);
                if (inQuote)
                {
                    StartParagraph(indent, 0, 60, 100);
                    rtf.Append(@"{\b ");
                    Inline((text ?? string.Empty).Trim());
                    rtf.Append('}');
                    EndParagraph();
                    return;
                }
                rtf.Append(@"\pard\plain\f0\keepn\cf").Append(level == 6 ? ColorMuted : ColorText)
                    .Append(@"\li").Append(indent).Append(@"\sb").Append(level <= 2 ? 320 : 220)
                    .Append(@"\sa").Append(level <= 2 ? 80 : 100)
                    .Append(@"\b\fs").Append(HeadingSizes[level - 1]).Append(' ');
                Inline((text ?? string.Empty).Trim());
                EndParagraph();
                // 一、二级标题下方细分隔线（与常见 Markdown 渲染一致）
                if (level <= 2) Line(indent, 10, 120);
            }

            private void RuleBlock(int indent)
            {
                if (inQuote) return;
                rtf.Append(@"\pard\plain\f0\fs8\li").Append(indent).Append(@"\sa80 \par").Append('\n');
                Line(indent, 15, 200);
            }

            /// <summary>RichEdit 会给无边框的单元格边画网格线：不需要的边显式画成底色。</summary>
            private void HiddenBorders(bool top, bool bottom, bool right)
            {
                if (top) rtf.Append(@"\clbrdrt\brdrs\brdrw10\brdrcf").Append(ColorPaper);
                if (bottom) rtf.Append(@"\clbrdrb\brdrs\brdrw10\brdrcf").Append(ColorPaper);
                if (right) rtf.Append(@"\clbrdrr\brdrs\brdrw10\brdrcf").Append(ColorPaper);
            }

            /// <summary>RichEdit 不绘制段落边框：用只有下边框的空单元格画一条水平线。</summary>
            private void Line(int indent, int thickness, int spaceAfter)
            {
                rtf.Append(@"\pard\plain\trowd\trgaph0\trleft").Append(indent)
                    .Append(@"\clbrdrb\brdrs\brdrw").Append(thickness).Append(@"\brdrcf").Append(ColorBorder)
                    .Append(@"\clbrdrl\brdrs\brdrw10\brdrcf").Append(ColorPaper);
                HiddenBorders(true, false, true);
                rtf.Append(@"\cellx").Append(width)
                    .Append(@"\pard\intbl\plain\f0\fs2 \cell\row").Append('\n');
                rtf.Append(@"\pard\plain\f0\fs2\li").Append(indent).Append(@"\sa").Append(spaceAfter).Append(@" \par").Append('\n');
            }

            private void CodeBlock(List<string> code, int indent)
            {
                if (inQuote)
                {
                    StartParagraph(indent, 0, 0, 140);
                    rtf.Append(@"{\f1\fs").Append(CodeSize).Append(' ');
                    for (int k = 0; k < code.Count; k++)
                    {
                        if (k > 0) rtf.Append(@"\line ");
                        Escape(code[k].Replace("\t", "    "));
                    }
                    rtf.Append('}');
                    EndParagraph();
                    return;
                }
                rtf.Append(@"\pard\plain\trowd\trgaph160\trleft").Append(indent)
                    .Append(@"\trpaddl160\trpaddr160\trpaddt100\trpaddb100\trpaddfl3\trpaddfr3\trpaddft3\trpaddfb3")
                    .Append(@"\clcbpat").Append(ColorCodeBg)
                    .Append(@"\clbrdrt\brdrs\brdrw10\brdrcf").Append(ColorBorder)
                    .Append(@"\clbrdrl\brdrs\brdrw10\brdrcf").Append(ColorBorder)
                    .Append(@"\clbrdrb\brdrs\brdrw10\brdrcf").Append(ColorBorder)
                    .Append(@"\clbrdrr\brdrs\brdrw10\brdrcf").Append(ColorBorder)
                    .Append(@"\cellx").Append(width).Append('\n');
                rtf.Append(@"\pard\intbl\plain\f1\fs").Append(CodeSize).Append(@"\cf1 ");
                for (int k = 0; k < code.Count; k++)
                {
                    if (k > 0) rtf.Append(@"\line ");
                    Escape(code[k].Replace("\t", "    "));
                }
                if (code.Count == 0) rtf.Append(' ');
                rtf.Append(@"\cell\row").Append('\n');
                Spacer(indent);
            }

            /// <summary>表格/代码块后的空白段：RichEdit 中表格行无段后距，补一小段间隔。</summary>
            private void Spacer(int indent)
            {
                if (inQuote) return;
                rtf.Append(@"\pard\plain\f0\fs10\li").Append(indent).Append(@"\sa60 \par").Append('\n');
            }

            private void Table(List<string> rows, string delimiter, int indent)
            {
                List<string> aligns = SplitRow(delimiter);
                List<List<string>> cells = new List<List<string>>();
                int columns = aligns.Count;
                foreach (string row in rows)
                {
                    List<string> split = SplitRow(row);
                    cells.Add(split);
                    columns = Math.Max(columns, split.Count);
                }
                columns = Math.Max(1, Math.Min(columns, 32));
                double[] weights = new double[columns];
                for (int c = 0; c < columns; c++) weights[c] = 4;
                foreach (List<string> row in cells)
                    for (int c = 0; c < columns && c < row.Count; c++)
                        weights[c] = Math.Max(weights[c], Math.Min(48, DisplayWidth(row[c])));
                double total = 0;
                foreach (double w in weights) total += w;
                int available = Math.Max(width - indent, columns * 600);
                int[] edges = new int[columns];
                int edge = indent;
                for (int c = 0; c < columns; c++)
                {
                    edge = c == columns - 1 ? Math.Max(edge + 600, indent + available)
                        : edge + Math.Max(600, (int)(available * weights[c] / total));
                    edges[c] = edge;
                }
                for (int r = 0; r < cells.Count; r++)
                {
                    bool header = r == 0;
                    rtf.Append(@"\pard\plain\trowd\trgaph100\trleft").Append(indent)
                        .Append(@"\trpaddl100\trpaddr100\trpaddt60\trpaddb60\trpaddfl3\trpaddfr3\trpaddft3\trpaddfb3");
                    for (int c = 0; c < columns; c++)
                    {
                        if (header) rtf.Append(@"\clcbpat").Append(ColorHeaderBg);
                        rtf.Append(@"\clbrdrt\brdrs\brdrw10\brdrcf").Append(ColorBorder)
                            .Append(@"\clbrdrl\brdrs\brdrw10\brdrcf").Append(ColorBorder)
                            .Append(@"\clbrdrb\brdrs\brdrw10\brdrcf").Append(ColorBorder)
                            .Append(@"\clbrdrr\brdrs\brdrw10\brdrcf").Append(ColorBorder)
                            .Append(@"\cellx").Append(edges[c]);
                    }
                    rtf.Append('\n');
                    for (int c = 0; c < columns; c++)
                    {
                        string align = c < aligns.Count ? aligns[c].Trim() : string.Empty;
                        string q = align.StartsWith(":") && align.EndsWith(":") ? @"\qc" : align.EndsWith(":") ? @"\qr" : @"\ql";
                        rtf.Append(@"\pard\intbl").Append(q).Append(@"\plain\f0\fs").Append(BodySize).Append(@"\cf1");
                        if (header) rtf.Append(@"\b");
                        rtf.Append(' ');
                        if (c < cells[r].Count) Inline(cells[r][c].Trim());
                        rtf.Append(@"\cell");
                    }
                    rtf.Append(@"\row").Append('\n');
                }
                Spacer(indent);
            }

            private void ListBlock(ListState list, int column, string marker, string text, int indent)
            {
                bool ordered = char.IsDigit(marker[0]);
                int level = list.Enter(column, ordered, ordered ? ParseNumber(marker) : 0);
                string bullet;
                if (ordered) bullet = list.NextNumber(level) + marker.Substring(marker.Length - 1);
                else bullet = level == 0 ? "•" : level == 1 ? "◦" : "▪";
                Match task = TaskBox.Match(text);
                if (task.Success)
                {
                    bullet = task.Groups[1].Value == " " ? "☐" : "☑";
                    text = text.Substring(task.Length);
                }
                int textIndent = indent + 360 * (level + 1);
                list.TextIndent = 360 * (level + 1);
                int left = StartParagraph(textIndent, -300, 0, 80);
                rtf.Append(@"\tx").Append(left).Append(' ');
                Escape(bullet);
                rtf.Append(@"\tab ");
                Inline(text.Trim());
                EndParagraph();
                list.AfterBlank = false;
            }

            private static int ParseNumber(string marker)
            {
                int value;
                return int.TryParse(marker.Substring(0, marker.Length - 1), out value) ? value : 1;
            }

            // ===== 行内 =====

            private void Inline(string s)
            {
                if (inlineDepth >= MaxInlineDepth || s.Length > MaxInlineLength)
                {
                    Escape(s);
                    return;
                }
                inlineDepth++;
                try { InlineCore(s); }
                finally { inlineDepth--; }
            }

            private void InlineCore(string s)
            {
                int i = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '\\' && i + 1 < s.Length && IsPunctuation(s[i + 1]))
                    {
                        Escape(s[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '`')
                    {
                        int run = Run(s, i, '`');
                        int close = FindRun(s, i + run, '`', run);
                        if (close < 0) { Escape(s.Substring(i, run)); i += run; continue; }
                        string code = s.Substring(i + run, close - i - run);
                        if (code.Length > 2 && code[0] == ' ' && code[code.Length - 1] == ' ') code = code.Substring(1, code.Length - 2);
                        rtf.Append(@"{\f1\fs").Append(CodeSize).Append(@"\highlight").Append(ColorHeaderBg).Append(' ');
                        Escape(code);
                        rtf.Append('}');
                        i = close + run;
                        continue;
                    }
                    string label, url;
                    int end;
                    if (c == '!' && i + 1 < s.Length && s[i + 1] == '[' && TryLink(s, i + 1, out label, out url, out end))
                    {
                        rtf.Append(@"{\cf").Append(ColorMuted).Append(' ');
                        Escape("[图片" + (label.Length > 0 ? "：" + label : string.Empty) + "]");
                        rtf.Append('}');
                        i = end;
                        continue;
                    }
                    if (c == '[' && TryLink(s, i, out label, out url, out end))
                    {
                        rtf.Append(@"{\cf").Append(ColorLink).Append(@"\ul ");
                        Inline(label);
                        rtf.Append('}');
                        i = end;
                        continue;
                    }
                    if (c == '<')
                    {
                        Match br = LineBreakTag.Match(s, i);
                        if (br.Success)
                        {
                            rtf.Append(@"\line ");
                            i += br.Length;
                            while (i < s.Length && s[i] == ' ') i++;
                            continue;
                        }
                        Match auto = AutoLink.Match(s, i);
                        if (auto.Success)
                        {
                            rtf.Append(@"{\cf").Append(ColorLink).Append(@"\ul ");
                            Escape(auto.Groups[1].Value);
                            rtf.Append('}');
                            i += auto.Length;
                            continue;
                        }
                    }
                    if ((c == 'h' || c == 'H') && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
                    {
                        Match bare = BareUrl.Match(s, i);
                        if (bare.Success)
                        {
                            rtf.Append(@"{\cf").Append(ColorLink).Append(@"\ul ");
                            Escape(bare.Value);
                            rtf.Append('}');
                            i += bare.Length;
                            continue;
                        }
                    }
                    if ((c == '*' || c == '_' || c == '~') && TryEmphasis(s, ref i)) continue;
                    Escape(c);
                    i++;
                }
            }

            private bool TryEmphasis(string s, ref int i)
            {
                char c = s[i];
                int run = Run(s, i, c);
                int start = i + run;
                if (start >= s.Length || char.IsWhiteSpace(s[start])) return false;
                if (c == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1])) return false;
                string open;
                if (c == '~') { if (run != 2) return false; open = @"{\strike "; }
                else if (run >= 3) { run = 3; open = @"{\b\i "; }
                else open = run == 2 ? @"{\b " : @"{\i ";
                int close = start;
                while (true)
                {
                    close = FindRun(s, close, c, run);
                    if (close < 0) return false;
                    bool valid = close > start && !char.IsWhiteSpace(s[close - 1])
                        && !(c == '_' && close + run < s.Length && char.IsLetterOrDigit(s[close + run]));
                    if (valid) break;
                    close += run;
                }
                rtf.Append(open);
                Inline(s.Substring(start, close - start));
                rtf.Append('}');
                i = close + run;
                return true;
            }

            /// <summary>查找恰好 count 个连续 c 组成的分隔符（前后不能紧邻同一字符）。</summary>
            private static int FindRun(string s, int from, char c, int count)
            {
                for (int j = from; j < s.Length; j++)
                {
                    if (s[j] == '\\' && c != '`') { j++; continue; }
                    if (s[j] != c) continue;
                    int run = Run(s, j, c);
                    if (run == count) return j;
                    j += run - 1;
                }
                return -1;
            }

            private static int Run(string s, int i, char c)
            {
                int j = i;
                while (j < s.Length && s[j] == c) j++;
                return j - i;
            }

            private static bool TryLink(string s, int open, out string label, out string url, out int end)
            {
                label = url = null;
                end = open;
                int depth = 0, close = -1;
                for (int j = open; j < s.Length; j++)
                {
                    if (s[j] == '\\') { j++; continue; }
                    if (s[j] == '[') depth++;
                    else if (s[j] == ']' && --depth == 0) { close = j; break; }
                }
                if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(') return false;
                depth = 0;
                int paren = -1;
                for (int j = close + 1; j < s.Length; j++)
                {
                    if (s[j] == '\\') { j++; continue; }
                    if (s[j] == '(') depth++;
                    else if (s[j] == ')' && --depth == 0) { paren = j; break; }
                }
                if (paren < 0) return false;
                label = s.Substring(open + 1, close - open - 1);
                url = s.Substring(close + 2, paren - close - 2).Trim();
                end = paren + 1;
                return true;
            }

            private void Escape(string s)
            {
                foreach (char c in s) Escape(c);
            }

            private void Escape(char c)
            {
                if (c == '\\' || c == '{' || c == '}') rtf.Append('\\').Append(c);
                else if (c == '\t') rtf.Append(@"\tab ");
                else if (c < 0x20) { }
                else if (c < 0x80) rtf.Append(c);
                else rtf.Append(@"\u").Append((short)c).Append('?');
            }

            // ===== 表格与文本工具 =====

            private static List<string> SplitRow(string row)
            {
                string t = row.Trim();
                if (t.StartsWith("|")) t = t.Substring(1);
                if (t.EndsWith("|") && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);
                List<string> cells = new List<string>();
                StringBuilder cell = new StringBuilder();
                bool code = false;
                for (int j = 0; j < t.Length; j++)
                {
                    char c = t[j];
                    if (c == '\\' && j + 1 < t.Length && t[j + 1] == '|') { cell.Append('|'); j++; continue; }
                    if (c == '`') code = !code;
                    if (c == '|' && !code) { cells.Add(cell.ToString()); cell.Length = 0; continue; }
                    cell.Append(c);
                }
                cells.Add(cell.ToString());
                return cells;
            }

            private static int DisplayWidth(string s)
            {
                int w = 0;
                foreach (char c in s) w += IsWide(c) ? 2 : 1;
                return w;
            }

            private static bool IsWide(char c)
            {
                return c >= 0x2E80 && !(c >= 0xD800 && c <= 0xDFFF);
            }

            private static bool NeedsSpace(string left, string right)
            {
                if (left.Length == 0 || right.Length == 0) return false;
                return !IsWide(left[left.Length - 1]) && !IsWide(right[0]);
            }

            private static string Joiner(string left, string right)
            {
                return NeedsSpace(left, right) ? " " : string.Empty;
            }

            private static string JoinLines(List<string> lines)
            {
                StringBuilder sb = new StringBuilder();
                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (sb.Length > 0 && NeedsSpace(sb.ToString(), t)) sb.Append(' ');
                    sb.Append(t);
                }
                return sb.ToString();
            }

            private static bool IsPunctuation(char c)
            {
                return c < 0x80 && char.IsPunctuation(c) || "`*_{}[]()#+-.!|<>~\\$^=".IndexOf(c) >= 0;
            }

            private static string LeadingWhitespace(string line)
            {
                int j = 0;
                while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
                return line.Substring(0, j);
            }

            private static int Columns(string whitespace)
            {
                int col = 0;
                foreach (char c in whitespace) col = c == '\t' ? (col / 4 + 1) * 4 : col + 1;
                return col;
            }

            private static string StripColumns(string line, int count)
            {
                int col = 0, j = 0;
                while (j < line.Length && col < count && (line[j] == ' ' || line[j] == '\t'))
                {
                    col = line[j] == '\t' ? (col / 4 + 1) * 4 : col + 1;
                    j++;
                }
                return line.Substring(j);
            }
        }

        /// <summary>列表嵌套：按标记所在列维护层级栈，有序列表逐层计数。</summary>
        private sealed class ListState
        {
            private readonly List<int> columns = new List<int>();
            private readonly List<int> numbers = new List<int>();
            public bool AfterBlank;
            public int TextIndent;

            public bool Active { get { return columns.Count > 0; } }

            public int Enter(int column, bool ordered, int start)
            {
                while (columns.Count > 0 && column < columns[columns.Count - 1]) Pop();
                if ((columns.Count == 0 || column > columns[columns.Count - 1]) && columns.Count < 6)
                {
                    columns.Add(column);
                    numbers.Add(ordered ? start - 1 : 0);
                }
                return columns.Count - 1;
            }

            public int NextNumber(int level)
            {
                numbers[level]++;
                return numbers[level];
            }

            private void Pop()
            {
                columns.RemoveAt(columns.Count - 1);
                numbers.RemoveAt(numbers.Count - 1);
            }

            public void Reset()
            {
                columns.Clear();
                numbers.Clear();
                AfterBlank = false;
                TextIndent = 0;
            }
        }
    }
}
