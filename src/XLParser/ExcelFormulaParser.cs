﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Irony.Parsing;

namespace XLParser
{
    /// <summary>
    /// Excel formula parser <br/>
    /// Contains parser and utilities that operate directly on the parse tree, or makes working with the parse tree easier.
    /// </summary>
    public static class ExcelFormulaParser
    {
        /// <summary>
        /// Thread-local singleton parser instance
        /// </summary>
        [ThreadStatic] private static Parser _p;

        /// <summary>
        /// Thread-safe parser
        /// </summary>
        private static Parser P => _p ?? (_p = new Parser(new ExcelFormulaGrammar()));

        /// <summary>
        /// Parse a formula, return the the tree's root node
        /// </summary>
        /// <param name="input">The formula to be parsed.</param>
        /// <exception cref="ArgumentException">
        /// If formula could not be parsed
        /// </exception>
        /// <returns>Parse tree root node</returns>
        public static ParseTreeNode Parse(string input)
        {
            return ParseToTree(input).Root;
        }

        /// <summary>
        /// Parse a formula, return the the tree
        /// </summary>
        /// <param name="input">The formula to be parsed.</param>
        /// <exception cref="ArgumentException">
        /// If formula could not be parsed
        /// </exception>
        /// <returns>Parse tree</returns>
        public static ParseTree ParseToTree(string input)
        {
            var tree = P.Parse(input);

            if (tree.HasErrors())
            {
                throw new ArgumentException("Failed parsing input <<" + input + ">>");
            }

            var intersects = tree.Root.AllNodes().Where(node => node.Is(GrammarNames.TokenIntersect));

            foreach (ParseTreeNode intersect in intersects)
            {
                var newLocation = new SourceLocation(intersect.Span.Location.Position - 1, intersect.Span.Location.Line, intersect.Span.Location.Column - 1);
                intersect.Span = new SourceSpan(newLocation, 1);
            }

            var quotedSheetNodes = tree.Root.AllNodes().Where(node => node.Is(GrammarNames.TokenSheetQuoted));

            foreach (ParseTreeNode quotedSheetNode in quotedSheetNodes)
            {
                PrefixInfo.FixQuotedSheetNodeForWhitespace(quotedSheetNode, input);
            }

            return tree;
        }

        /// <summary>
        /// Non-terminal nodes in depth-first pre-order, with a conditional stop
        /// </summary>
        /// <param name="root">The root node</param>
        /// <param name="stopAt">Don't process the children of a node matching this predicate</param>
        // inspiration taken from https://irony.codeplex.com/discussions/213938
        public static IEnumerable<ParseTreeNode> AllNodesConditional(this ParseTreeNode root, Predicate<ParseTreeNode> stopAt = null)
        {
            var stack = new Stack<ParseTreeNode>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;

                // Check if we don't want to process the children of this node
                if (stopAt != null && stopAt(node)) continue;

                var children = node.ChildNodes;
                // Push children on in reverse order so that they will
                // be evaluated left -> right when popped.
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
        }

        /// <summary>
        /// All non-terminal nodes in depth-first pre-order
        /// </summary>
        public static IEnumerable<ParseTreeNode> AllNodes(this ParseTreeNode root)
        {
            return AllNodesConditional(root);
        }

        /// <summary>
        /// All non-terminal nodes of a certain type in depth-first pre-order
        /// </summary>
        public static IEnumerable<ParseTreeNode> AllNodes(this ParseTreeNode root, string type)
        {
            return AllNodes(root.AllNodes(), type);
        }

        internal static IEnumerable<ParseTreeNode> AllNodes(IEnumerable<ParseTreeNode> allNodes, string type)
        {
            return allNodes.Where(node => node.Is(type));
        }

        /// <summary>
        /// Get the parent node of a node
        /// </summary>
        /// <remarks>
        /// This is an expensive operation, as the whole tree will be searched through
        /// </remarks>
        public static ParseTreeNode Parent(this ParseTreeNode child, ParseTreeNode treeRoot)
        {
            var parent = treeRoot.AllNodes()
                .FirstOrDefault(node => node.ChildNodes.Any(c => c == child));
            if(parent == null) throw new ArgumentException("Child is not part of the tree", nameof(child));
            return parent;
        }

        /// <summary>
        /// The node type/name
        /// </summary>
        public static string Type(this ParseTreeNode node)
        {
            return node.Term.Name;
        }

        /// <summary>
        /// Check if a node is of a particular type
        /// </summary>
        public static bool Is(this ParseTreeNode pt, string type)
        {
            return pt.Type() == type;
        }

        /// <summary>
        /// Checks whether this node is a function
        /// </summary>
        public static Boolean IsFunction(this ParseTreeNode input)
        {
            return input.Is(GrammarNames.FunctionCall)
                || input.Is(GrammarNames.ReferenceFunctionCall)
                || input.Is(GrammarNames.UDFunctionCall)
                // This gives potential problems/duplication on external UDFs, but they are so rare that I think this is acceptable
                || (input.Is(GrammarNames.Reference) && input.ChildNodes.Count == 2 && input.ChildNodes[1].IsFunction())
                ;
        }

        /// <summary>
        /// Whether or not this node represents parentheses "(_)"
        /// </summary>
        public static bool IsParentheses(this ParseTreeNode input)
        {
            switch (input.Type())
            {
                case GrammarNames.Formula:
                    return input.ChildNodes.Count == 1 && input.ChildNodes[0].Is(GrammarNames.Formula);
                case GrammarNames.Reference:
                    return input.ChildNodes.Count == 1 && input.ChildNodes[0].Is(GrammarNames.Reference);
                default:
                    return false;
            }
        }

        public static bool IsBinaryOperation(this ParseTreeNode input)
        {
            return input.IsFunction()
                   && input.ChildNodes.Count == 3
                   && input.ChildNodes[1].Term.Flags.HasFlag(TermFlags.IsOperator);
        }

        public static bool IsBinaryNonReferenceOperation(this ParseTreeNode input)
        {
            return input.IsBinaryOperation() && input.Is(GrammarNames.FunctionCall);
        }

        public static bool IsBinaryReferenceOperation(this ParseTreeNode input)
        {
            return input.IsBinaryOperation() && input.Is(GrammarNames.ReferenceFunctionCall);
        }

        public static bool IsUnaryOperation(this ParseTreeNode input)
        {
            return IsUnaryPrefixOperation(input) || IsUnaryPostfixOperation(input);
        }

        public static bool IsUnaryPrefixOperation(this ParseTreeNode input)
        {
            return input.IsFunction()
                   && input.ChildNodes.Count == 2
                   && input.ChildNodes[0].Term.Flags.HasFlag(TermFlags.IsOperator);
        }

        public static bool IsUnaryPostfixOperation(this ParseTreeNode input)
        {
            return input.IsFunction()
                   && input.ChildNodes.Count == 2
                   && input.ChildNodes[1].Term.Flags.HasFlag(TermFlags.IsOperator);

        }

        private static string RemoveFinalSymbol(string input)
        {
            input = input.Substring(0, input.Length - 1);
            return input;
        }

        /// <summary>
        /// Get the function or operator name of this function call
        /// </summary>
        public static string GetFunction(this ParseTreeNode input)
        {
            if (input.IsIntersection())
            {
                return GrammarNames.TokenIntersect;
            }
            if (input.IsUnion())
            {
                return GrammarNames.TokenUnionOperator;
            }
            if (input.IsBinaryOperation() || input.IsUnaryPostfixOperation())
            {
                return input.ChildNodes[1].Print();
            }
            if (input.IsUnaryPrefixOperation())
            {
                return input.ChildNodes[0].Print();
            }
            if (input.IsNamedFunction())
            {
                return RemoveFinalSymbol(input.ChildNodes[0].Print()).ToUpper();
            }
            if (input.IsExternalUDFunction())
            {
                return $"{input.ChildNodes[0].Print()}{GetFunction(input.ChildNodes[1])}";
            }

            throw new ArgumentException("Not a function call", nameof(input));
        }

        /// <summary>
        /// Check if this node is a specific function
        /// </summary>
        public static bool MatchFunction(this ParseTreeNode input, string functionName)
        {
            return IsFunction(input) && GetFunction(input) == functionName;
        }

        /// <summary>
        /// Get all the arguments of a function or operation
        /// </summary>
        public static IEnumerable<ParseTreeNode> GetFunctionArguments(this ParseTreeNode input)
        {
            if (input.IsNamedFunction())
            {
                return input
                    .ChildNodes[1] // "Arguments" non-terminal
                    .ChildNodes    // "Argument" non-terminals
                    .Select(node => node.ChildNodes[0])
                    ;
            }
            if (input.IsBinaryOperation())
            {
                return new[] {input.ChildNodes[0], input.ChildNodes[2]};
            }
            if (input.IsUnaryPrefixOperation())
            {
                return new[] {input.ChildNodes[1]};
            }
            if (input.IsUnaryPostfixOperation())
            {
                return new[] {input.ChildNodes[0]};
            }
            if (input.IsUnion())
            {
                return input.ChildNodes[0].ChildNodes;
            }
            if (input.IsExternalUDFunction())
            {
                return input // Reference
                    .ChildNodes[1] // UDFunctionCall
                    .ChildNodes[1] // Arguments
                    .ChildNodes // Argument non-terminals
                    .Select(node => node.ChildNodes[0])
                    ;
            }
            throw new ArgumentException("Not a function call", nameof(input));
        }

        /// <summary>
        /// Checks whether this node is a built-in excel function
        /// </summary>
        public static bool IsBuiltinFunction(this ParseTreeNode node)
        {
            return node.IsFunction() &&
                (node.ChildNodes[0].Is(GrammarNames.FunctionName) || node.ChildNodes[0].Is(GrammarNames.RefFunctionName));
        }

        /// <summary>
        /// Whether or not this node represents an intersection
        /// </summary>
        public static bool IsIntersection(this ParseTreeNode input)
        {
            return IsBinaryOperation(input) &&
                       input.ChildNodes[1].Token.Terminal.Name == GrammarNames.TokenIntersect;
        }

        /// <summary>
        /// Whether or not this node represents an union
        /// </summary>
        public static bool IsUnion(this ParseTreeNode input)
        {
            return input.Is(GrammarNames.ReferenceFunctionCall)
                && input.ChildNodes.Count == 1
                && input.ChildNodes[0].Is(GrammarNames.Union);
        }

        /// <summary>
        /// Checks whether this node is a function call with name, and not just a unary or binary operation
        /// </summary>
        public static bool IsNamedFunction(this ParseTreeNode input)
        {
            return (input.Is(GrammarNames.FunctionCall) && input.ChildNodes[0].Is(GrammarNames.FunctionName))
                || (input.Is(GrammarNames.ReferenceFunctionCall) && input.ChildNodes[0].Is(GrammarNames.RefFunctionName))
                || input.Is(GrammarNames.UDFunctionCall);
        }

        public static bool IsOperation(this ParseTreeNode input)
        {
            return input.IsBinaryOperation() || input.IsUnaryOperation();
        }

        public static bool IsExternalUDFunction(this ParseTreeNode input)
        {
            return input.Is(GrammarNames.Reference) && input.ChildNodes.Count == 2 && input.ChildNodes[1].IsNamedFunction();
        }

        /// <summary>
        /// True if this node presents a number constant with a sign
        /// </summary>
        public static bool IsNumberWithSign(this ParseTreeNode input)
        {
            return IsUnaryPrefixOperation(input)
                   && input.ChildNodes[1].ChildNodes[0].Is(GrammarNames.Constant)
                   && input.ChildNodes[1].ChildNodes[0].ChildNodes[0].Is(GrammarNames.Number);
        }

        /// <summary>
        /// Extract all of the information from a Prefix non-terminal
        /// </summary>
        public static PrefixInfo GetPrefixInfo(this ParseTreeNode prefix) => PrefixInfo.From(prefix);

        /// <summary>
        /// Go to the first non-formula child node
        /// </summary>
        public static ParseTreeNode SkipFormula(this ParseTreeNode input)
        {
            while (input.Is(GrammarNames.Formula))
            {
                input = input.ChildNodes.First();
            }
            return input;
        }

        /// <summary>
        /// Get all child nodes that are references and aren't part of another reference expression
        /// </summary>
        public static IEnumerable<ParseTreeNode> GetReferenceNodes(this ParseTreeNode input)
        {
            return input.AllNodesConditional(node => node.Is(GrammarNames.Reference))
                .Where(node => node.Is(GrammarNames.Reference))
                .Select(node => node.SkipToRelevant())
                ;
        }

        /// <summary>
        /// Gets the ParserReferences from the input parse tree node and its children
        /// </summary>
        /// <remarks>
        /// 5 cases:
        /// 1. ReferenceItem node: convert to ParserReference
        /// 2. Reference node (Prefix ReferenceItem): convert to ParserReference, recursive call on the nodes returned from GetReferenceNodes(node)
        ///     (to include the references in the arguments of external UDFs)
        /// 3. Range node (Cell:Cell): recursive call to retrieve the 2 limits, create ParserReference of type CellRange
        /// 4. Range node with complex limits: recursive call to retrieve limits as 2 ParserReferences
        /// 5. Other cases (RefFunctionCall, Union, Arguments):recursive call on the nodes returned from GetReferenceNodes(node)
        /// </remarks>
        public static IEnumerable<ParserReference> GetParserReferences(this ParseTreeNode node)
        {
            if (node.Type() == GrammarNames.Reference && node.ChildNodes.Count == 1)
                node = node.ChildNodes[0];

            var list = new List<ParserReference>();

            switch (node.Type())
            {
                case GrammarNames.Cell:
                case GrammarNames.NamedRange:
                case GrammarNames.HorizontalRange:
                case GrammarNames.VerticalRange:
                case GrammarNames.StructuredReference:
                    list.Add(new ParserReference(node));
                    break;
                case GrammarNames.Reference:
                    list.Add(new ParserReference(node));
                    list.AddRange(node.ChildNodes[1].GetReferenceNodes().SelectMany(x => x.GetParserReferences()));
                    break;
                default:
                    if (node.IsRange())
                    {
                        var rangeStart = GetParserReferences(node.ChildNodes[0].SkipToRelevant()).ToArray();
                        var rangeEnd = GetParserReferences(node.ChildNodes[2].SkipToRelevant()).ToArray();
                        if (IsCellReference(rangeStart) && IsCellReference(rangeEnd))
                        {
                            ParserReference range = rangeStart.First();
                            range.MaxLocation = rangeEnd.First().MinLocation;
                            range.ReferenceType = ReferenceType.CellRange;
                            range.LocationString = node.Print();
                            list.Add(range);
                        }
                        else
                        {
                            list.AddRange(rangeStart);
                            list.AddRange(rangeEnd);
                        }
                    }
                    else
                    {
                        list.AddRange(node.GetReferenceNodes().SelectMany(x => x.GetParserReferences()));
                    }
                    break;
            }
            return list;
        }

        private static bool IsCellReference(IList<ParserReference> references)
        {
            return references.Count == 1 && references.First().ReferenceType == ReferenceType.Cell;
        }

        /// <summary>
        /// Whether or not this node represents a range
        /// </summary>
        public static bool IsRange(this ParseTreeNode input)
        {
            return input.IsBinaryReferenceOperation() &&
                       input.ChildNodes[1].Is(":");
        }

        /// <summary>
        /// Go to the first "relevant" child node, i.e. skips wrapper nodes
        /// </summary>
        /// <param name="input">The input parse tree node</param>
        /// <param name="skipReferencesWithoutPrefix">If true, skip all reference nodes without a prefix instead of only parentheses</param>
        /// <remarks>
        /// Skips:
        /// * FormulaWithEq and ArrayFormula nodes
        /// * Formula nodes
        /// * Parentheses
        /// * Reference nodes which are just wrappers
        /// </remarks>
        public static ParseTreeNode SkipToRelevant(this ParseTreeNode input, bool skipReferencesWithoutPrefix = false)
        {
            while (true)
            {
                switch (input.Type())
                {
                    case GrammarNames.FormulaWithEq:
                    case GrammarNames.ArrayFormula:
                        input = input.ChildNodes[1];
                        break;
                    case GrammarNames.Argument:
                    case GrammarNames.Formula:
                        if (input.ChildNodes.Count == 1)
                        {
                            input = input.ChildNodes[0];
                        }
                        else
                        {
                            return input;
                        }
                        break;
                    case GrammarNames.Reference:
                        // Skip references which are parentheses
                        // Skip references without a prefix (=> they only have one child node) if the option is set
                        if ((skipReferencesWithoutPrefix && input.ChildNodes.Count == 1) || input.IsParentheses())
                        {
                            input = input.ChildNodes[0];
                        }
                        else
                        {
                            return input;
                        }
                        break;
                    default:
                        return input;
                }
            }
        }

        /// <summary>
        /// Pretty-print a parse tree to a string
        /// </summary>
        public static string Print(this ParseTreeNode input)
        {
            // For terminals, just print the token text
            if (input.Term is Terminal)
            {
                return input.Token.Text;
            }

            // (Lazy) enumerable for printed children
            var children = input.ChildNodes.Select(Print);
            // Concrete list when needed
            List<string> childrenList;

            // Switch on non-terminals
            switch (input.Term.Name)
            {
                case GrammarNames.Formula:
                    // Check if these are brackets, otherwise print first child
                    return IsParentheses(input) ? $"({children.First()})" : children.First();

                case GrammarNames.FunctionCall:
                case GrammarNames.ReferenceFunctionCall:
                case GrammarNames.UDFunctionCall:
                    childrenList = children.ToList();

                    if (input.IsNamedFunction())
                    {
                        return string.Join("", childrenList) + ")";
                    }

                    if (input.IsBinaryOperation())
                    {
                        // format string for "normal" binary operation
                        string format = "{0}{1}{2}";
                        if (input.IsIntersection())
                        {
                            format = "{0} {2}";
                        }

                        return string.Format(format, childrenList[0], childrenList[1], childrenList[2]);
                    }

                    if (input.IsUnion())
                    {
                        return $"({string.Join(",", childrenList)})";
                    }

                    if (input.IsUnaryOperation())
                    {
                        return string.Join("", childrenList);
                    }

                    throw new ArgumentException("Unknown function type.");

                case GrammarNames.Reference:
                    return IsParentheses(input) ? $"({children.First()})" : string.Concat(children);

                case GrammarNames.Prefix:
                    var ret = string.Join("", children);
                    // The exclamation mark token is not included in the parse tree, so we have to add that if it's a single file
                    if (input.ChildNodes.Count == 1 && input.ChildNodes[0].Is(GrammarNames.File))
                    {
                        ret += "!";
                    }
                    return ret;

                case GrammarNames.ArrayFormula:
                    return "{=" + children.ElementAt(1) + "}";

                case GrammarNames.StructuredReference:
                    var sb = new StringBuilder();
                    var hashtable = input.ChildNodes.Count >= 1 && input.ChildNodes[0].Is(GrammarNames.StructuredReferenceTable);
                    var contentsNode = hashtable ? 1 : 0;
                    childrenList = children.ToList();
                    if (hashtable)
                    {
                        sb.Append(childrenList[0]);
                    }

                    if (hashtable && input.ChildNodes.Count == 1)
                    {
                        // Full table reference
                        sb.Append("[]");
                    }
                    else if (input.ChildNodes[contentsNode].Is(GrammarNames.StructuredReferenceElement))
                    {
                        sb.Append(childrenList[contentsNode]);
                    }
                    else
                    {
                        sb.Append($"[{childrenList[contentsNode]}]");
                    }

                    return sb.ToString();

                // Terms for which to print all child nodes concatenated
                case GrammarNames.ArrayConstant:
                case GrammarNames.DynamicDataExchange:
                case GrammarNames.FormulaWithEq:
                case GrammarNames.File:
                case GrammarNames.StructuredReferenceExpression:
                    return string.Join("", children);

                // Terms for which we print the children comma-separated
                case GrammarNames.Arguments:
                case GrammarNames.ArrayRows:
                case GrammarNames.Union:
                    return string.Join(",", children);

                case GrammarNames.ArrayColumns:
                    return string.Join(";", children);

                case GrammarNames.ConstantArray:
                    return $"{{{children.First()}}}";

                default:
                    // If it is not defined above and the number of children is exactly one, we want to just print the first child
                    if (input.ChildNodes.Count == 1)
                    {
                        return children.First();
                    }

                    throw new ArgumentException($"Could not print node of type '{input.Term.Name}'." + Environment.NewLine +
                                                "This probably means the Excel grammar was modified without the print function being modified");
            }
        }
    }

}

