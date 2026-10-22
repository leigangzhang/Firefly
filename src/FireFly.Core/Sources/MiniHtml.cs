using System.Net;
using System.Text;

namespace FireFly.Core.Sources;

/// <summary>网页里的一个元素或一段文字（Tag 为 "#text"）。</summary>
public sealed class HtmlNode
{
    public string Tag { get; init; } = string.Empty;

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<HtmlNode> Children { get; } = new();

    public HtmlNode? Parent { get; internal set; }

    /// <summary>文字节点的内容（已解码实体）。</summary>
    public string? Value { get; init; }

    public bool IsText => Tag == "#text";

    public string? Attr(string name) => Attributes.TryGetValue(name, out var v) ? v : null;

    public IReadOnlyList<string> Classes =>
        (Attr("class") ?? string.Empty).Split(' ', '\t', '\n', '\r', '\f').Where(c => c.Length > 0).ToList();

    public IEnumerable<HtmlNode> Elements => Children.Where(c => !c.IsText);

    public IEnumerable<HtmlNode> Descendants()
    {
        foreach (var child in Children.Where(c => !c.IsText))
        {
            yield return child;
            foreach (var d in child.Descendants())
            {
                yield return d;
            }
        }
    }

    public IEnumerable<HtmlNode> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent)
        {
            yield return p;
        }
    }

    /// <summary>所有文字拼起来，连续空白压成一个空格。</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            Collect(this, sb);
            return MiniHtml.Collapse(sb.ToString());
        }
    }

    private static void Collect(HtmlNode node, StringBuilder sb)
    {
        if (node.IsText)
        {
            sb.Append(node.Value);
            return;
        }

        if (node.Tag is "br" or "p" or "div" or "li" or "td" or "th" or "tr")
        {
            sb.Append(' ');
        }

        foreach (var c in node.Children)
        {
            Collect(c, sb);
        }

        // span、a 是行内元素，浏览器不在它们边界加空格；加了会把高亮的关键词和标题拆开（"超能路人甲 [全8集]"）
        if (node.Tag is "td" or "th" or "p" or "div" or "li")
        {
            sb.Append(' ');
        }
    }
}

/// <summary>
/// 够用的 HTML 解析和 CSS 选择器：读磁力站的搜索结果页、详情页。
/// 不执行脚本；容忍没闭合的标签。选择器支持 tag、.class、#id、[attr]、[attr=v]、[attr^=v]、[attr*=v]、[attr$=v]、
/// 后代（空格）、子元素（&gt;）、:first-child 和逗号分组。字段写法 "选择器@属性"，不写属性就取文字。
/// </summary>
public static class MiniHtml
{
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "textarea", "title" };

    private static readonly HashSet<string> ClosesP = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "div", "dl", "fieldset", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
    };

    public static HtmlNode Parse(string html)
    {
        var root = new HtmlNode { Tag = "#document" };
        var stack = new List<HtmlNode> { root };
        var i = 0;
        var n = html.Length;
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length == 0)
            {
                return;
            }

            Append(stack[^1], new HtmlNode { Tag = "#text", Value = WebUtility.HtmlDecode(text.ToString()) });
            text.Clear();
        }

        while (i < n)
        {
            var c = html[i];
            if (c != '<')
            {
                text.Append(c);
                i++;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                FlushText();
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }

            if (i + 1 < n && (html[i + 1] == '!' || html[i + 1] == '?'))
            {
                FlushText();
                var end = html.IndexOf('>', i);
                i = end < 0 ? n : end + 1;
                continue;
            }

            if (i + 1 < n && html[i + 1] == '/')
            {
                var end = html.IndexOf('>', i);
                if (end < 0)
                {
                    text.Append(c);
                    i++;
                    continue;
                }

                FlushText();
                var name = html[(i + 2)..end].Trim().ToLowerInvariant();
                var at = stack.FindLastIndex(x => x.Tag == name);
                if (at > 0)
                {
                    stack.RemoveRange(at, stack.Count - at);
                }

                i = end + 1;
                continue;
            }

            if (i + 1 >= n || !char.IsLetter(html[i + 1]))
            {
                text.Append(c);
                i++;
                continue;
            }

            FlushText();
            var (node, selfClosing, next) = ReadTag(html, i + 1);
            i = next;
            ImplicitClose(stack, node.Tag);
            Append(stack[^1], node);
            if (RawText.Contains(node.Tag) && !selfClosing)
            {
                var close = html.IndexOf("</" + node.Tag, i, StringComparison.OrdinalIgnoreCase);
                var content = close < 0 ? html[i..] : html[i..close];
                if (node.Tag is "title" or "textarea")
                {
                    Append(node, new HtmlNode { Tag = "#text", Value = WebUtility.HtmlDecode(content) });
                }

                if (close < 0)
                {
                    i = n;
                }
                else
                {
                    var gt = html.IndexOf('>', close);
                    i = gt < 0 ? n : gt + 1;
                }

                continue;
            }

            if (!selfClosing && !Void.Contains(node.Tag))
            {
                stack.Add(node);
            }
        }

        FlushText();
        return root;
    }

    private static void Append(HtmlNode parent, HtmlNode child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
    }

    /// <summary>常见的省略结束标签：新的 li 关掉上一个 li，块元素关掉 p，等等。</summary>
    private static void ImplicitClose(List<HtmlNode> stack, string tag)
    {
        void CloseTo(string name, params string[] stopAt)
        {
            for (var k = stack.Count - 1; k > 0; k--)
            {
                if (stack[k].Tag == name)
                {
                    stack.RemoveRange(k, stack.Count - k);
                    return;
                }

                if (stopAt.Contains(stack[k].Tag))
                {
                    return;
                }
            }
        }

        switch (tag)
        {
            case "li":
                CloseTo("li", "ul", "ol");
                break;
            case "dt" or "dd":
                CloseTo("dt", "dl");
                CloseTo("dd", "dl");
                break;
            case "tr":
                CloseTo("tr", "table", "tbody", "thead", "tfoot");
                break;
            case "td" or "th":
                CloseTo("td", "tr", "table");
                CloseTo("th", "tr", "table");
                break;
            case "option":
                CloseTo("option", "select");
                break;
        }

        if (ClosesP.Contains(tag))
        {
            CloseTo("p", "div", "td", "li", "section", "article", "body");
        }
    }

    private static (HtmlNode Node, bool SelfClosing, int Next) ReadTag(string html, int i)
    {
        var n = html.Length;
        var start = i;
        while (i < n && !char.IsWhiteSpace(html[i]) && html[i] != '>' && html[i] != '/')
        {
            i++;
        }

        var node = new HtmlNode { Tag = html[start..i].ToLowerInvariant() };
        var selfClosing = false;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i >= n)
            {
                break;
            }

            if (html[i] == '>')
            {
                i++;
                break;
            }

            if (html[i] == '/')
            {
                selfClosing = true;
                i++;
                continue;
            }

            var nameStart = i;
            while (i < n && !char.IsWhiteSpace(html[i]) && html[i] != '=' && html[i] != '>' && html[i] != '/')
            {
                i++;
            }

            var name = html[nameStart..i];
            while (i < n && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            var value = string.Empty;
            if (i < n && html[i] == '=')
            {
                i++;
                while (i < n && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i < n && (html[i] == '"' || html[i] == '\''))
                {
                    var quote = html[i++];
                    var end = html.IndexOf(quote, i);
                    end = end < 0 ? n : end;
                    value = html[i..end];
                    i = Math.Min(n, end + 1);
                }
                else
                {
                    var vs = i;
                    while (i < n && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }

                    value = html[vs..i];
                }
            }

            if (name.Length > 0)
            {
                node.Attributes.TryAdd(name, WebUtility.HtmlDecode(value));
            }
        }

        return (node, selfClosing, i);
    }

    internal static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == ' ')
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    // ---------- 选择器 ----------

    public static IReadOnlyList<HtmlNode> QueryAll(HtmlNode scope, string selector)
    {
        var groups = selector.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Select(ParseSelector).ToList();
        if (groups.Count == 0)
        {
            return Array.Empty<HtmlNode>();
        }

        return scope.Descendants().Where(node => groups.Any(g => Matches(node, g, g.Count - 1, scope))).ToList();
    }

    public static HtmlNode? Query(HtmlNode scope, string selector) => QueryAll(scope, selector).FirstOrDefault();

    /// <summary>按"选择器@属性"取值：选择器为空表示 scope 自己；不写属性取文字。</summary>
    public static string? Value(HtmlNode scope, string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return null;
        }

        var at = field.LastIndexOf('@');
        var selector = at >= 0 ? field[..at].Trim() : field.Trim();
        var attr = at >= 0 ? field[(at + 1)..].Trim() : null;
        var node = selector.Length == 0 ? scope : Query(scope, selector);
        if (node is null)
        {
            return null;
        }

        var value = string.IsNullOrEmpty(attr) ? node.Text : node.Attr(attr);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record Step(char Combinator, string? Tag, string? Id, List<string> Classes, List<(string Name, string Op, string Value)> Attrs, bool FirstChild);

    private static List<Step> ParseSelector(string selector)
    {
        var steps = new List<Step>();
        var i = 0;
        var combinator = ' ';
        while (i < selector.Length)
        {
            var c = selector[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '>')
            {
                combinator = '>';
                i++;
                continue;
            }

            string? tag = null, id = null;
            var classes = new List<string>();
            var attrs = new List<(string, string, string)>();
            var firstChild = false;
            while (i < selector.Length && !char.IsWhiteSpace(selector[i]) && selector[i] != '>')
            {
                var ch = selector[i];
                if (ch == '.' || ch == '#')
                {
                    var s = ++i;
                    while (i < selector.Length && IsNameChar(selector[i]))
                    {
                        i++;
                    }

                    if (ch == '.')
                    {
                        classes.Add(selector[s..i]);
                    }
                    else
                    {
                        id = selector[s..i];
                    }
                }
                else if (ch == '[')
                {
                    var end = FindClosingBracket(selector, i);
                    var body = selector[(i + 1)..end];
                    i = end + 1;
                    var opAt = body.IndexOfAny(new[] { '=', '^', '*', '$', '~', '|' });
                    if (opAt < 0)
                    {
                        attrs.Add((body.Trim(), string.Empty, string.Empty));
                        continue;
                    }

                    var op = body[opAt] == '=' ? "=" : body.Substring(opAt, 2);
                    var name = body[..opAt].Trim();
                    var value = body[(opAt + op.Length)..].Trim().Trim('"', '\'');
                    attrs.Add((name, op, value));
                }
                else if (ch == ':')
                {
                    var s = ++i;
                    while (i < selector.Length && (IsNameChar(selector[i]) || selector[i] == '(' || selector[i] == ')'))
                    {
                        i++;
                    }

                    firstChild |= selector[s..i] == "first-child";
                }
                else if (ch == '*')
                {
                    i++;
                }
                else
                {
                    var s = i;
                    while (i < selector.Length && IsNameChar(selector[i]))
                    {
                        i++;
                    }

                    if (i == s)
                    {
                        i++;
                        continue;
                    }

                    tag = selector[s..i].ToLowerInvariant();
                }
            }

            steps.Add(new Step(combinator, tag, id, classes, attrs, firstChild));
            combinator = ' ';
        }

        return steps;
    }

    private static int FindClosingBracket(string s, int open)
    {
        var quote = '\0';
        for (var i = open + 1; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ']')
            {
                return i;
            }
        }

        return s.Length - 1;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    private static bool Matches(HtmlNode node, List<Step> steps, int index, HtmlNode scope)
    {
        if (!MatchesStep(node, steps[index]))
        {
            return false;
        }

        if (index == 0)
        {
            return true;
        }

        var combinator = steps[index].Combinator;
        if (combinator == '>')
        {
            var parent = node.Parent;
            return parent is not null && parent != scope && Matches(parent, steps, index - 1, scope);
        }

        for (var p = node.Parent; p is not null && p != scope; p = p.Parent)
        {
            if (Matches(p, steps, index - 1, scope))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesStep(HtmlNode node, Step step)
    {
        if (node.IsText || (step.Tag is not null && node.Tag != step.Tag))
        {
            return false;
        }

        if (step.Id is not null && !string.Equals(node.Attr("id"), step.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (step.Classes.Count > 0)
        {
            var classes = node.Classes;
            if (!step.Classes.All(c => classes.Contains(c)))
            {
                return false;
            }
        }

        foreach (var (name, op, value) in step.Attrs)
        {
            var actual = node.Attr(name);
            if (actual is null)
            {
                return false;
            }

            var ok = op switch
            {
                "" => true,
                "=" => actual == value,
                "^=" => actual.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                "$=" => actual.EndsWith(value, StringComparison.OrdinalIgnoreCase),
                "*=" => actual.Contains(value, StringComparison.OrdinalIgnoreCase),
                "~=" => actual.Split(' ').Contains(value),
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        if (step.FirstChild && node.Parent?.Elements.FirstOrDefault() != node)
        {
            return false;
        }

        return true;
    }
}
